// <copyright file="Botinput.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using ClickableTransparentOverlay.Win32;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using GameOffsets.Natives;

    /// <summary>
    /// Handles native Windows keyboard and mouse input simulation for PoE 2.
    /// Ported and enhanced with AutoExile Humanized Input System:
    /// - Gaussian delay distribution (Box-Muller transform)
    /// - Organic curved mouse movement with distance scaling and perpendicular jitter
    /// - Landing jitter and triangular bounding box randomization
    /// - Minimum input event gap / APM limiter
    /// - WASD continuous movement and sprint
    /// </summary>
    public static class BotInput
    {
        private const int MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const int MOUSEEVENTF_LEFTUP = 0x0004;
        private const int MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const int MOUSEEVENTF_RIGHTUP = 0x0010;
        private const int MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const int MOUSEEVENTF_MIDDLEUP = 0x0040;
        private const int KEYEVENTF_KEYUP = 0x0002;
        private const uint WM_KEYUP = 0x0101;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern void mouse_event(int dwFlags, int dx, int dy, int cButtons, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private static readonly HashSet<VK> HeldKeys = new();
        private static readonly object KeyLock = new();
        private static readonly Random Rng = new();

        // ── AutoExile Humanized Timing Constants (Box-Muller) ──
        public const float SettleMeanMs = 70f;
        public const float SettleStdDevMs = 10f;
        public const float HoldMeanMs = 40f;
        public const float HoldStdDevMs = 10f;
        private const int DelayFloorMs = 20;
        private const int DelayCeilingMs = 120;
        private const int MinInputEventGapMs = 15;

        // ── AutoExile Mouse Movement Interpolation Constants ──
        private const int MoveMinMs = 15;
        private const int MoveMaxMs = 80;
        private const float MoveMaxDistance = 2000f;
        private const int MoveSteps = 8;
        private const float MoveJitterPx = 3f;
        private const float LandingJitterPx = 3f;

        private static DateTime lastInputEvent = DateTime.MinValue;

        /// <summary>
        /// Checks if a physical key is currently pressed.
        /// </summary>
        public static bool IsKeyDown(VK key)
        {
            return (GetAsyncKeyState((int)key) & 0x8000) != 0;
        }

        /// <summary>
        /// Gets current cursor position.
        /// </summary>
        public static Vector2 GetCurrentCursorPos()
        {
            if (GetCursorPos(out var pt))
            {
                return new Vector2(pt.X, pt.Y);
            }

            return Vector2.Zero;
        }

        /// <summary>
        /// Generates a Gaussian-distributed random delay using Box-Muller transform.
        /// Ported directly from AutoExile BotInput.
        /// </summary>
        public static int GaussianDelay(float mean, float stdDev)
        {
            double u1 = 1.0 - Rng.NextDouble();
            double u2 = Rng.NextDouble();
            double normal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
            int result = (int)(mean + stdDev * normal);
            return Math.Clamp(result, DelayFloorMs, DelayCeilingMs);
        }

        public static int RandSettle() => GaussianDelay(SettleMeanMs, SettleStdDevMs);
        public static int RandHold() => GaussianDelay(HoldMeanMs, HoldStdDevMs);

        /// <summary>
        /// Returns a center-biased random point within a bounding box using triangular distribution.
        /// Ported directly from AutoExile BotInput.
        /// </summary>
        public static Vector2 RandomizeWithinRect(float centerX, float centerY, float halfWidth, float halfHeight)
        {
            float rx = (float)(Rng.NextDouble() + Rng.NextDouble() - 1.0);
            float ry = (float)(Rng.NextDouble() + Rng.NextDouble() - 1.0);
            return new Vector2(centerX + (rx * halfWidth), centerY + (ry * halfHeight));
        }

        /// <summary>
        /// Enforces minimum gap between input events to prevent inhuman APM spikes.
        /// </summary>
        private static async Task SendDelayAsync()
        {
            var elapsed = (DateTime.Now - lastInputEvent).TotalMilliseconds;
            if (elapsed < MinInputEventGapMs)
            {
                await Task.Delay((int)(MinInputEventGapMs - elapsed));
            }

            lastInputEvent = DateTime.Now;
        }

        /// <summary>
        /// Moves cursor to a screen position instantly (utility/fallback).
        /// </summary>
        public static void MoveCursor(Vector2 screenPos)
        {
            SetCursorPos((int)screenPos.X, (int)screenPos.Y);
        }

        /// <summary>
        /// Interpolate cursor organically from current position to target with distance-scaled duration,
        /// perpendicular midpoint jitter, and landing variance.
        /// Ported directly from AutoExile BotInput.MoveCursorTo.
        /// </summary>
        public static async Task MoveCursorOrganic(Vector2 target)
        {
            var start = GetCurrentCursorPos();
            var delta = target - start;
            var dist = delta.Length();

            if (dist < 5f)
            {
                SetCursorPos((int)target.X, (int)target.Y);
                return;
            }

            float t = Math.Clamp(dist / MoveMaxDistance, 0f, 1f);
            int totalMs = MoveMinMs + (int)((MoveMaxMs - MoveMinMs) * t);
            int stepDelayMs = Math.Max(1, totalMs / MoveSteps);

            var perp = Vector2.Normalize(new Vector2(-delta.Y, delta.X));

            for (int i = 1; i <= MoveSteps; i++)
            {
                float progress = (float)i / MoveSteps;
                var pos = start + (delta * progress);

                if (i < MoveSteps)
                {
                    float jitter = (float)((Rng.NextDouble() * 2.0) - 1.0) * MoveJitterPx;
                    float taper = 1f - (Math.Abs(progress - 0.5f) * 2f);
                    pos += perp * jitter * taper;
                }

                SetCursorPos((int)pos.X, (int)pos.Y);
                await Task.Delay(stepDelayMs);
            }

            // Landing jitter: slight natural offset
            var jitterX = (float)((Rng.NextDouble() * 2.0) - 1.0) * LandingJitterPx;
            var jitterY = (float)((Rng.NextDouble() * 2.0) - 1.0) * LandingJitterPx;
            SetCursorPos((int)(target.X + jitterX), (int)(target.Y + jitterY));
        }

        /// <summary>
        /// Performs a humanized left or right mouse click at screen position.
        /// </summary>
        public static void HumanClick(Vector2 screenPos, bool rightClick = false)
        {
            Task.Run(async () =>
            {
                await MoveCursorOrganic(screenPos);
                await Task.Delay(RandSettle());
                await SendDelayAsync();

                int downFlag = rightClick ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN;
                int upFlag = rightClick ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP;

                mouse_event(downFlag, 0, 0, 0, 0);
                await Task.Delay(RandHold());
                await SendDelayAsync();
                mouse_event(upFlag, 0, 0, 0, 0);
            });
        }

        /// <summary>
        /// Sends a key down event if not already down.
        /// </summary>
        public static void KeyDown(VK key)
        {
            lock (KeyLock)
            {
                if (!HeldKeys.Contains(key))
                {
                    keybd_event((byte)key, 0, 0, 0);
                    HeldKeys.Add(key);
                    lastInputEvent = DateTime.Now;
                }
            }
        }

        /// <summary>
        /// Sends a key up event if currently held.
        /// </summary>
        public static void KeyUp(VK key)
        {
            lock (KeyLock)
            {
                if (HeldKeys.Contains(key))
                {
                    keybd_event((byte)key, 0, KEYEVENTF_KEYUP, 0);
                    HeldKeys.Remove(key);
                    lastInputEvent = DateTime.Now;
                }
            }
        }

        /// <summary>
        /// Instantly fires a key release/tap with ZERO artificial delay or sleep.
        /// Sends WM_KEYUP directly to PoE's window message queue (matching AutoHotKeyTrigger's ultra-responsive input)
        /// combined with an immediate keybd_event sequence to register without any 40-60ms hold delay.
        /// </summary>
        public static void FastPressKey(VK key)
        {
            if ((int)key <= 0)
            {
                return;
            }

            try
            {
                // 1. Post directly to PoE window message queue (identical to AutoHotKeyTrigger / MiscHelper)
                IntPtr hWnd = GameHelper.Core.Process.MainWindowHandle;
                if (hWnd == IntPtr.Zero)
                {
                    hWnd = GetForegroundWindow();
                }

                if (hWnd != IntPtr.Zero)
                {
                    PostMessage(hWnd, WM_KEYUP, (IntPtr)(int)key, IntPtr.Zero);
                }

                // 2. Also send immediate keybd_event down & up with 0ms hold for any DirectInput/GetAsyncKeyState listeners
                keybd_event((byte)key, 0, 0, 0);
                keybd_event((byte)key, 0, KEYEVENTF_KEYUP, 0);
                lastInputEvent = DateTime.Now;
            }
            catch
            {
                // Ignore transient errors
            }
        }

        public static void FastPressKey(int vKey) => FastPressKey((VK)vKey);

        /// <summary>
        /// Presses a key. Uses fast instant press for responsive triggers (hold &lt;= 25ms),
        /// or short hold duration if explicitly requested.
        /// </summary>
        public static void TapKey(VK key, int baseHoldMs = 25)
        {
            if (baseHoldMs <= 25)
            {
                FastPressKey(key);
                return;
            }

            Task.Run(async () =>
            {
                keybd_event((byte)key, 0, 0, 0);
                int hold = Math.Max(10, GaussianDelay(baseHoldMs, 5f));
                await Task.Delay(hold);
                keybd_event((byte)key, 0, KEYEVENTF_KEYUP, 0);
                lastInputEvent = DateTime.Now;
            });
        }

        public static void TapKey(int vKey, int baseHoldMs = 25) => TapKey((VK)vKey, baseHoldMs);
        public static void SendKey(VK key, int baseHoldMs = 25) => TapKey(key, baseHoldMs);
        public static void SendKey(int vKey, int baseHoldMs = 25) => TapKey((VK)vKey, baseHoldMs);

        /// <summary>
        /// Executes attack action immediately with zero pre-delay and snappy execution.
        /// </summary>
        public static void ExecuteAttack(AttackInputType type, VK key, int baseHoldMs = 50)
        {
            if (type == AttackInputType.KeyboardKey && baseHoldMs <= 30)
            {
                FastPressKey(key);
                return;
            }

            Task.Run(async () =>
            {
                int hold = Math.Max(15, GaussianDelay(baseHoldMs, 5f));

                switch (type)
                {
                    case AttackInputType.MouseRight:
                        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                        await Task.Delay(hold);
                        mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                        break;
                    case AttackInputType.MouseLeft:
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                        await Task.Delay(hold);
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                        break;
                    case AttackInputType.MouseMiddle:
                        mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, 0);
                        await Task.Delay(hold);
                        mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, 0);
                        break;
                    case AttackInputType.KeyboardKey:
                        FastPressKey(key);
                        break;
                }
                lastInputEvent = DateTime.Now;
            });
        }

        private static bool isRightMouseDown = false;
        private static bool isLeftMouseDown = false;
        private static bool isMiddleMouseDown = false;
        private static VK? activeChannelKey = null;

        /// <summary>
        /// Starts holding an input down for channeling skills.
        /// </summary>
        public static void StartChannel(AttackInputType type, VK key)
        {
            switch (type)
            {
                case AttackInputType.MouseRight:
                    if (!isRightMouseDown)
                    {
                        mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, 0);
                        isRightMouseDown = true;
                    }
                    break;
                case AttackInputType.MouseLeft:
                    if (!isLeftMouseDown)
                    {
                        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
                        isLeftMouseDown = true;
                    }
                    break;
                case AttackInputType.MouseMiddle:
                    if (!isMiddleMouseDown)
                    {
                        mouse_event(MOUSEEVENTF_MIDDLEDOWN, 0, 0, 0, 0);
                        isMiddleMouseDown = true;
                    }
                    break;
                case AttackInputType.KeyboardKey:
                    KeyDown(key);
                    activeChannelKey = key;
                    break;
            }
        }

        /// <summary>
        /// Stops holding an input down for channeling skills.
        /// </summary>
        public static void StopChannel(AttackInputType type, VK key)
        {
            switch (type)
            {
                case AttackInputType.MouseRight:
                    if (isRightMouseDown)
                    {
                        mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                        isRightMouseDown = false;
                    }
                    break;
                case AttackInputType.MouseLeft:
                    if (isLeftMouseDown)
                    {
                        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                        isLeftMouseDown = false;
                    }
                    break;
                case AttackInputType.MouseMiddle:
                    if (isMiddleMouseDown)
                    {
                        mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, 0);
                        isMiddleMouseDown = false;
                    }
                    break;
                case AttackInputType.KeyboardKey:
                    KeyUp(key);
                    if (activeChannelKey == key) activeChannelKey = null;
                    break;
            }
        }

        /// <summary>
        /// Releases all currently held attack inputs and channels.
        /// </summary>
        public static void ReleaseAllAttackInputs()
        {
            if (isRightMouseDown)
            {
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, 0);
                isRightMouseDown = false;
            }
            if (isLeftMouseDown)
            {
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
                isLeftMouseDown = false;
            }
            if (isMiddleMouseDown)
            {
                mouse_event(MOUSEEVENTF_MIDDLEUP, 0, 0, 0, 0);
                isMiddleMouseDown = false;
            }
            if (activeChannelKey.HasValue)
            {
                KeyUp(activeChannelKey.Value);
                activeChannelKey = null;
            }
        }

        /// <summary>
        /// Converts a target Grid position into a camera-relative screen direction vector for accurate WASD movement.
        /// Uses WorldToScreen matrix projection with isometric fallback.
        /// </summary>
        public static Vector2 GridToScreenDirection(
            WorldData? world,
            Entity? player,
            Vector2 targetGrid,
            Vector2 playerGrid,
            float gridToWorldFactor = 10.87f)
        {
            var gridDelta = targetGrid - playerGrid;
            float dist = gridDelta.Length();
            if (dist < 0.1f)
            {
                return Vector2.Zero;
            }

            var gridDir = gridDelta / dist;

            if (player != null && player.TryGetComponent<Render>(out var pRender) && world != null)
            {
                // Sample a lookahead point only 8-10 grid units ahead of the player.
                // At 8-10 units, it is GUARANTEED to be in front of the camera, on-screen, and height-matched.
                float sampleDist = Math.Min(dist, 10f);
                var sampleGrid = playerGrid + (gridDir * sampleDist);
                float terrainZ = pRender.TerrainHeight;

                var playerScreen = world.WorldToScreen(new Vector2(pRender.WorldPosition.X, pRender.WorldPosition.Y), terrainZ);
                var sampleScreen = world.WorldToScreen(new Vector2(sampleGrid.X * gridToWorldFactor, sampleGrid.Y * gridToWorldFactor), terrainZ);

                if (playerScreen != Vector2.Zero && sampleScreen != Vector2.Zero)
                {
                    var diff = sampleScreen - playerScreen;
                    if (diff.LengthSquared() > 1f)
                    {
                        return Vector2.Normalize(diff);
                    }
                }
            }

            // Isometric projection fallback:
            // Camera tilted at PoE's ~38.7 deg angle.
            // Screen X = (Grid X - Grid Y) * cos(38.7°) = (Grid X - Grid Y) * 0.78f
            // Screen Y = (Grid X + Grid Y) * sin(38.7°) = (Grid X + Grid Y) * 0.62f (screen Y points down: -Y is UP, +Y is DOWN)
            float sx = (gridDelta.X - gridDelta.Y) * 0.78f;
            float sy = (gridDelta.X + gridDelta.Y) * 0.62f;
            if (Math.Abs(sx) > 0.01f || Math.Abs(sy) > 0.01f)
            {
                return Vector2.Normalize(new Vector2(sx, sy));
            }

            return Vector2.Zero;
        }

        /// <summary>
        /// Updates WASD movement based on screen direction vector (normalized).
        /// </summary>
        public static void WasdMove(Vector2 screenDir, AutoExile2Settings settings)
        {
            if (screenDir.LengthSquared() < 0.01f)
            {
                ReleaseAllMovementKeys(settings);
                return;
            }

            // 8-directional determination
            bool up = screenDir.Y < -0.38f;
            bool down = screenDir.Y > 0.38f;
            bool left = screenDir.X < -0.38f;
            bool right = screenDir.X > 0.38f;

            if (up) KeyDown(settings.MoveUp); else KeyUp(settings.MoveUp);
            if (down) KeyDown(settings.MoveDown); else KeyUp(settings.MoveDown);
            if (left) KeyDown(settings.MoveLeft); else KeyUp(settings.MoveLeft);
            if (right) KeyDown(settings.MoveRight); else KeyUp(settings.MoveRight);
        }

        /// <summary>
        /// Sets sprint key state.
        /// </summary>
        public static void SetSprint(VK sprintKey, bool hold)
        {
            if (hold)
            {
                KeyDown(sprintKey);
            }
            else
            {
                KeyUp(sprintKey);
            }
        }

        /// <summary>
        /// Releases sprint key.
        /// </summary>
        public static void ReleaseSprint(VK sprintKey)
        {
            KeyUp(sprintKey);
        }

        /// <summary>
        /// Releases all movement and sprint keys.
        /// </summary>
        public static void ReleaseAllMovementKeys(AutoExile2Settings settings)
        {
            KeyUp(settings.MoveUp);
            KeyUp(settings.MoveDown);
            KeyUp(settings.MoveLeft);
            KeyUp(settings.MoveRight);
            KeyUp(settings.SprintKey);
        }
    }


    /// <summary>
    /// Alias for BotInput matching exact user-specified casing (Botinput).
    /// </summary>
    public static class Botinput
    {
        public static bool IsKeyDown(VK key) => BotInput.IsKeyDown(key);
        public static Vector2 GetCurrentCursorPos() => BotInput.GetCurrentCursorPos();
        public static int GaussianDelay(float mean, float stdDev) => BotInput.GaussianDelay(mean, stdDev);
        public static int RandSettle() => BotInput.RandSettle();
        public static int RandHold() => BotInput.RandHold();
        public static Vector2 RandomizeWithinRect(float cx, float cy, float hw, float hh) => BotInput.RandomizeWithinRect(cx, cy, hw, hh);
        public static void MoveCursor(Vector2 screenPos) => BotInput.MoveCursor(screenPos);
        public static Task MoveCursorOrganic(Vector2 target) => BotInput.MoveCursorOrganic(target);
        public static void HumanClick(Vector2 screenPos, bool rightClick = false) => BotInput.HumanClick(screenPos, rightClick);
        public static void KeyDown(VK key) => BotInput.KeyDown(key);
        public static void KeyUp(VK key) => BotInput.KeyUp(key);
        public static void FastPressKey(VK key) => BotInput.FastPressKey(key);
        public static void FastPressKey(int vKey) => BotInput.FastPressKey(vKey);
        public static void TapKey(VK key, int baseHoldMs = 50) => BotInput.TapKey(key, baseHoldMs);
        public static void ExecuteAttack(AttackInputType type, VK key, int baseHoldMs = 100) => BotInput.ExecuteAttack(type, key, baseHoldMs);
        public static void StartChannel(AttackInputType type, VK key) => BotInput.StartChannel(type, key);
        public static void StopChannel(AttackInputType type, VK key) => BotInput.StopChannel(type, key);
        public static void ReleaseAllAttackInputs() => BotInput.ReleaseAllAttackInputs();
        public static Vector2 GridToScreenDirection(GameHelper.RemoteObjects.States.InGameStateObjects.WorldData? world, Entity? player, Vector2 targetGrid, Vector2 playerGrid, float gridToWorldFactor = 10.87f) => BotInput.GridToScreenDirection(world, player, targetGrid, playerGrid, gridToWorldFactor);
        public static void WasdMove(Vector2 screenDir, AutoExile2Settings settings) => BotInput.WasdMove(screenDir, settings);
        public static void SetSprint(VK sprintKey, bool hold) => BotInput.SetSprint(sprintKey, hold);
        public static void ReleaseSprint(VK sprintKey) => BotInput.ReleaseSprint(sprintKey);
        public static void ReleaseAllMovementKeys(AutoExile2Settings settings) => BotInput.ReleaseAllMovementKeys(settings);
    }

    /// <summary>
    /// Backward-compatibility forwarder for InputController.
    /// </summary>
    public static class InputController
    {
        public static bool IsKeyDown(VK key) => BotInput.IsKeyDown(key);
        public static Vector2 GetCurrentCursorPos() => BotInput.GetCurrentCursorPos();
        public static int GaussianDelay(float mean, float stdDev) => BotInput.GaussianDelay(mean, stdDev);
        public static int RandSettle() => BotInput.RandSettle();
        public static int RandHold() => BotInput.RandHold();
        public static Vector2 RandomizeWithinRect(float cx, float cy, float hw, float hh) => BotInput.RandomizeWithinRect(cx, cy, hw, hw);
        public static void MoveCursor(Vector2 screenPos) => BotInput.MoveCursor(screenPos);
        public static Task MoveCursorOrganic(Vector2 target) => BotInput.MoveCursorOrganic(target);
        public static void HumanClick(Vector2 screenPos, bool rightClick = false) => BotInput.HumanClick(screenPos, rightClick);
        public static void KeyDown(VK key) => BotInput.KeyDown(key);
        public static void KeyUp(VK key) => BotInput.KeyUp(key);
        public static void FastPressKey(VK key) => BotInput.FastPressKey(key);
        public static void FastPressKey(int vKey) => BotInput.FastPressKey(vKey);
        public static void TapKey(VK key, int baseHoldMs = 50) => BotInput.TapKey(key, baseHoldMs);
        public static void ExecuteAttack(AttackInputType type, VK key, int baseHoldMs = 100) => BotInput.ExecuteAttack(type, key, baseHoldMs);
        public static void StartChannel(AttackInputType type, VK key) => BotInput.StartChannel(type, key);
        public static void StopChannel(AttackInputType type, VK key) => BotInput.StopChannel(type, key);
        public static void ReleaseAllAttackInputs() => BotInput.ReleaseAllAttackInputs();
        public static Vector2 GridToScreenDirection(GameHelper.RemoteObjects.States.InGameStateObjects.WorldData? world, Entity? player, Vector2 targetGrid, Vector2 playerGrid, float gridToWorldFactor = 10.87f) => BotInput.GridToScreenDirection(world, player, targetGrid, playerGrid, gridToWorldFactor);
        public static void WasdMove(Vector2 screenDir, AutoExile2Settings settings) => BotInput.WasdMove(screenDir, settings);
        public static void SetSprint(VK sprintKey, bool hold) => BotInput.SetSprint(sprintKey, hold);
        public static void ReleaseSprint(VK sprintKey) => BotInput.ReleaseSprint(sprintKey);
        public static void ReleaseAllMovementKeys(AutoExile2Settings settings) => BotInput.ReleaseAllMovementKeys(settings);
    }

}
