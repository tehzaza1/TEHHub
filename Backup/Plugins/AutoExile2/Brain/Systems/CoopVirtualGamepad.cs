// <copyright file="CoopVirtualGamepad.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Numerics;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using ClickableTransparentOverlay.Win32;
    using Nefarius.ViGEm.Client;
    using Nefarius.ViGEm.Client.Targets;
    using Nefarius.ViGEm.Client.Targets.Xbox360;

    /// <summary>
    /// Gamepad button mapping for Co-op controls.
    /// </summary>
    public enum CoopPadButton
    {
        None = 0,
        A = 1,
        B = 2,
        X = 3,
        Y = 4,
        LeftShoulder = 5,  // LB
        RightShoulder = 6, // RB
        LeftTrigger = 7,   // LT
        RightTrigger = 8,  // RT
        Start = 9,
        Back = 10,
        DPadUp = 11,
        DPadDown = 12,
        DPadLeft = 13,
        DPadRight = 14,
    }

    /// <summary>
    /// Manages virtual Xbox 360 gamepads for Couch Co-op via ViGEm:
    /// - Player 2 (Follower): Full autonomous analog steering, aiming, buttons and flasks.
    /// - Player 1 (Leader): High-speed passthrough from physical controller + auto flask and buff injection.
    /// </summary>
    public class CoopVirtualGamepad : IDisposable
    {
        private ViGEmClient? vigemClient;
        private IXbox360Controller? followerXbox;
        private IXbox360Controller? leaderXbox;

        private CancellationTokenSource? passthroughCts;
        private CoopPadButton leaderActiveOverrideButton = CoopPadButton.None;
        private bool leaderActiveFlaskLife = false;
        private bool leaderActiveFlaskMana = false;

        public int FollowerSlotIndex { get; private set; } = -1;
        public int LeaderSlotIndex { get; private set; } = -1;
        public bool IsFollowerConnected => this.followerXbox != null;
        public bool IsLeaderConnected => this.leaderXbox != null;
        public string? LastError { get; private set; }
        public bool IsFollowerManualMoving { get; private set; } = false;
        public bool IsFollowerManualAiming { get; private set; } = false;
        private CancellationTokenSource? followerPassthroughCts;
        private readonly HashSet<CoopPadButton> followerBotActiveButtons = new();
        public bool IsLeaderSprinting { get; private set; } = false;
        private volatile bool followerSprintActive = false;

        // Rate-limit stopwatches
        private readonly Stopwatch leaderFlaskTimer = Stopwatch.StartNew();
        private readonly Stopwatch followerFlaskTimer = Stopwatch.StartNew();
        private readonly Stopwatch followerSkillTimer = Stopwatch.StartNew();

        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_GAMEPAD
        {
            public ushort wButtons;
            public byte bLeftTrigger;
            public byte bRightTrigger;
            public short sThumbLX;
            public short sThumbLY;
            public short sThumbRX;
            public short sThumbRY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XINPUT_STATE
        {
            public uint dwPacketNumber;
            public XINPUT_GAMEPAD Gamepad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern int XInputGetState(int dwUserIndex, ref XINPUT_STATE pState);

        /// <summary>
        /// Checks if a specific XInput slot is currently active and connected.
        /// </summary>
        public bool IsSlotActive(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= 4) return false;
            var state = default(XINPUT_STATE);
            return XInputGetState(slotIndex, ref state) == 0;
        }

        /// <summary>
        /// Ensures virtual controllers are initialized and connected.
        /// - Player 1 (Leader): Creates Virtual Controller #1 and forwards physical pad inputs (passthrough) + injects flasks/buffs.
        /// - Player 2 (Follower): Creates Virtual Controller #2 for autonomous bot control.
        /// </summary>
        public bool EnsureConnected(bool enableLeaderPassthrough = true, int physicalIndex = 0, bool enableFollowerPad = true)
        {
            try
            {
                if (this.vigemClient == null)
                {
                    this.vigemClient = new ViGEmClient();
                }

                // 1. Initialize Leader Controller if requested and not yet active
                bool justCreatedLeader = false;
                if (enableLeaderPassthrough && this.leaderXbox == null)
                {
                    var slotsBefore = this.GetActiveXInputSlots();
                    this.leaderXbox = this.vigemClient.CreateXbox360Controller();
                    this.leaderXbox.Connect();
                    this.LeaderSlotIndex = this.WaitForNewActiveSlot(slotsBefore);
                    this.StartLeaderPassthrough(physicalIndex);
                    justCreatedLeader = true;
                }
                else if (!enableLeaderPassthrough && this.leaderXbox != null)
                {
                    this.passthroughCts?.Cancel();
                    this.leaderXbox.Disconnect();
                    this.leaderXbox = null;
                    this.LeaderSlotIndex = -1;
                }

                // If Leader pad was just created, introduce a deliberate delay (~700ms)
                // so Windows PnP and PoE 2 fully register Controller #1 (Leader) before Controller #2 (Follower) is plugged in.
                if (justCreatedLeader && enableFollowerPad && this.followerXbox == null)
                {
                    Thread.Sleep(700);
                }

                // 2. Initialize Follower Controller if requested and not yet active
                if (enableFollowerPad && this.followerXbox == null)
                {
                    var slotsBefore = this.GetActiveXInputSlots();
                    this.followerXbox = this.vigemClient.CreateXbox360Controller();
                    this.followerXbox.Connect();
                    this.FollowerSlotIndex = this.WaitForNewActiveSlot(slotsBefore);
                    this.StartFollowerKeyboardPassthrough();
                }
                else if (this.followerXbox != null && (this.followerPassthroughCts == null || this.followerPassthroughCts.IsCancellationRequested))
                {
                    this.StartFollowerKeyboardPassthrough();
                }
                else if (!enableFollowerPad && this.followerXbox != null)
                {
                    this.followerXbox.Disconnect();
                    this.followerXbox = null;
                    this.FollowerSlotIndex = -1;
                }

                return (this.leaderXbox != null || !enableLeaderPassthrough) && (this.followerXbox != null || !enableFollowerPad);
            }
            catch (Exception ex)
            {
                this.LastError = ex.Message;
                Console.WriteLine($"[CoopVirtualGamepad] Initialization error: {ex.Message}");
                return false;
            }
        }

        private bool[] GetActiveXInputSlots()
        {
            var slots = new bool[4];
            for (int i = 0; i < 4; i++)
            {
                var state = default(XINPUT_STATE);
                slots[i] = XInputGetState(i, ref state) == 0;
            }

            return slots;
        }

        private int WaitForNewActiveSlot(bool[] slotsBefore, int timeoutMs = 1500)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                for (int i = 0; i < 4; i++)
                {
                    var state = default(XINPUT_STATE);
                    if (XInputGetState(i, ref state) == 0 && !slotsBefore[i])
                    {
                        return i;
                    }
                }

                Thread.Sleep(25);
            }

            return -1;
        }

        private void StartLeaderPassthrough(int physicalIndex)
        {
            this.passthroughCts?.Cancel();
            this.passthroughCts = new CancellationTokenSource();
            var token = this.passthroughCts.Token;

            Task.Run(async () =>
            {
                TimeBeginPeriod(1);
                try
                {
                    while (!token.IsCancellationRequested && this.leaderXbox != null)
                    {
                        try
                        {
                            var state = default(XINPUT_STATE);
                            if (XInputGetState(physicalIndex, ref state) == 0)
                            {
                                var pad = state.Gamepad;
                                ushort buttons = pad.wButtons;

                                var xbox = this.leaderXbox;
                                xbox.SetButtonState(Xbox360Button.Up, (buttons & 0x0001) != 0);
                                xbox.SetButtonState(Xbox360Button.Down, (buttons & 0x0002) != 0);
                                // D-Pad Left = Life Flask | D-Pad Right = Mana Flask (Default in PoE 2)
                                xbox.SetButtonState(Xbox360Button.Left, ((buttons & 0x0004) != 0) || this.leaderActiveFlaskLife);
                                xbox.SetButtonState(Xbox360Button.Right, ((buttons & 0x0008) != 0) || this.leaderActiveFlaskMana);
                                xbox.SetButtonState(Xbox360Button.Start, (buttons & 0x0010) != 0);
                                xbox.SetButtonState(Xbox360Button.Back, (buttons & 0x0020) != 0);
                                xbox.SetButtonState(Xbox360Button.LeftThumb, (buttons & 0x0040) != 0);
                                xbox.SetButtonState(Xbox360Button.RightThumb, (buttons & 0x0080) != 0);
                                xbox.SetButtonState(Xbox360Button.LeftShoulder, (buttons & 0x0100) != 0);
                                xbox.SetButtonState(Xbox360Button.RightShoulder, (buttons & 0x0200) != 0);
                                xbox.SetButtonState(Xbox360Button.A, (buttons & 0x1000) != 0);
                                bool leaderB = (buttons & 0x2000) != 0;
                                xbox.SetButtonState(Xbox360Button.B, leaderB);
                                this.IsLeaderSprinting = leaderB || BotInput.IsKeyDown(VK.SPACE);
                                xbox.SetButtonState(Xbox360Button.X, (buttons & 0x4000) != 0);
                                xbox.SetButtonState(Xbox360Button.Y, (buttons & 0x8000) != 0);

                                xbox.SetSliderValue(Xbox360Slider.LeftTrigger, pad.bLeftTrigger);
                                xbox.SetSliderValue(Xbox360Slider.RightTrigger, pad.bRightTrigger);
                                xbox.SetAxisValue(Xbox360Axis.LeftThumbX, pad.sThumbLX);
                                xbox.SetAxisValue(Xbox360Axis.LeftThumbY, pad.sThumbLY);
                                xbox.SetAxisValue(Xbox360Axis.RightThumbX, pad.sThumbRX);
                                xbox.SetAxisValue(Xbox360Axis.RightThumbY, pad.sThumbRY);

                                if (this.leaderActiveOverrideButton != CoopPadButton.None)
                                {
                                    this.ApplyButton(xbox, this.leaderActiveOverrideButton, true);
                                }

                                xbox.SubmitReport();
                            }
                        }
                        catch
                        {
                            // Transient skip
                        }

                        await Task.Delay(2, token);
                    }
                }
                finally
                {
                    TimeEndPeriod(1);
                }
            }, token);
        }

        // ── Player 2 (Follower) Autonomous Controls ───────────────────────

        /// <summary>
        /// Sets Player 2 Left Thumbstick analog steering (-1.0 to 1.0).
        /// Note: In XInput, +Y is UP, -Y is DOWN.
        /// </summary>
        public void SetFollowerMovement(Vector2 dir)
        {
            if (this.followerXbox == null) return;
            if (this.IsFollowerManualMoving) return; // User is manually walking via keyboard arrow keys!

            short lx = 0;
            short ly = 0;

            if (dir != Vector2.Zero && dir.LengthSquared() > 0.001f)
            {
                var norm = Vector2.Normalize(dir);
                float mag = Math.Clamp(dir.Length(), 0f, 1f);
                lx = (short)Math.Clamp((int)(norm.X * mag * 32767f), -32768, 32767);
                // Grid Y points downward in screen space; flip for standard gamepad +Y = UP
                ly = (short)Math.Clamp((int)(-norm.Y * mag * 32767f), -32768, 32767);
            }

            lock (this.followerXbox)
            {
                this.followerXbox.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
                this.followerXbox.SetAxisValue(Xbox360Axis.LeftThumbY, ly);
            }
        }

        /// <summary>
        /// Sets Player 2 Right Thumbstick aiming direction (-1.0 to 1.0).
        /// </summary>
        public void SetFollowerAim(Vector2 dir)
        {
            if (this.followerXbox == null) return;
            if (this.IsFollowerManualAiming) return; // User is manually aiming via Numpad!

            short rx = 0;
            short ry = 0;

            if (dir != Vector2.Zero && dir.LengthSquared() > 0.001f)
            {
                var norm = Vector2.Normalize(dir);
                rx = (short)Math.Clamp((int)(norm.X * 32767f), -32768, 32767);
                ry = (short)Math.Clamp((int)(-norm.Y * 32767f), -32768, 32767);
            }

            lock (this.followerXbox)
            {
                this.followerXbox.SetAxisValue(Xbox360Axis.RightThumbX, rx);
                this.followerXbox.SetAxisValue(Xbox360Axis.RightThumbY, ry);
            }
        }

        /// <summary>
        /// Sets Player 2 sprint state (holds B button continuously for sprinting).
        /// This is separate from PressFollowerButton to avoid race conditions.
        /// In PoE 2, holding B = Sprint, tapping B = Dodge Roll.
        /// </summary>
        public void SetFollowerSprint(bool sprinting)
        {
            this.followerSprintActive = sprinting;
        }

        /// <summary>
        /// Triggers a quick Dodge Roll on Player 2 (taps B for 50ms).
        /// In PoE 2, tapping B triggers Dodge Roll (keeping weapons drawn and attack stance),
        /// whereas holding B enters Sprint mode (which sheathes weapons).
        /// </summary>
        public void TapFollowerDodgeRoll(int tapMs = 50)
        {
            if (this.followerXbox == null) return;
            if (this.followerSprintActive) return;

            this.PressFollowerButton(CoopPadButton.B, Math.Max(30, tapMs));
        }

        /// <summary>
        /// Presses a button on Player 2's controller for a specified hold duration.
        /// </summary>
        public void PressFollowerButton(CoopPadButton button, int holdMs = 60)
        {
            if (this.followerXbox == null || button == CoopPadButton.None) return;

            var controller = this.followerXbox;
            Task.Run(async () =>
            {
                try
                {
                    lock (this.followerBotActiveButtons)
                    {
                        this.followerBotActiveButtons.Add(button);
                    }

                    lock (controller)
                    {
                        this.ApplyButton(controller, button, true);
                        controller.SubmitReport();
                    }

                    await Task.Delay(Math.Max(30, holdMs));

                    lock (this.followerBotActiveButtons)
                    {
                        this.followerBotActiveButtons.Remove(button);
                    }

                    lock (controller)
                    {
                        this.ApplyButton(controller, button, false);
                        controller.SubmitReport();
                    }
                }
                catch
                {
                    // Ignore transient
                }
            });
        }

        /// <summary>
        /// Triggers Auto Flask on Player 2 (D-Pad Left for Life, D-Pad Right for Mana - PoE 2 Default).
        /// Submits immediately to ViGEm virtual controller for instant in-game reaction.
        /// </summary>
        public void PressFollowerFlask(bool isLife, int cooldownMs = 3000)
        {
            if (this.followerXbox == null) return;
            if (this.followerFlaskTimer.ElapsedMilliseconds < cooldownMs) return;

            this.followerFlaskTimer.Restart();
            var button = isLife ? Xbox360Button.Left : Xbox360Button.Right;
            var controller = this.followerXbox;

            Task.Run(async () =>
            {
                try
                {
                    lock (controller)
                    {
                        controller.SetButtonState(button, true);
                        controller.SubmitReport();
                    }

                    await Task.Delay(50);

                    lock (controller)
                    {
                        controller.SetButtonState(button, false);
                        controller.SubmitReport();
                    }
                }
                catch
                {
                    // Ignore transient
                }
            });
        }

        /// <summary>
        /// Triggers Auto Flask on Player 1 (D-Pad Left for Life, D-Pad Right for Mana - PoE 2 Default).
        /// Submits immediately to ViGEm virtual controller for instant in-game reaction.
        /// </summary>
        public void PressLeaderFlask(bool isLife, int cooldownMs = 3000)
        {
            if (this.leaderXbox == null) return;
            if (this.leaderFlaskTimer.ElapsedMilliseconds < cooldownMs) return;

            this.leaderFlaskTimer.Restart();
            var button = isLife ? Xbox360Button.Left : Xbox360Button.Right;
            var controller = this.leaderXbox;

            Task.Run(async () =>
            {
                try
                {
                    if (isLife) this.leaderActiveFlaskLife = true;
                    else this.leaderActiveFlaskMana = true;

                    lock (controller)
                    {
                        controller.SetButtonState(button, true);
                        controller.SubmitReport();
                    }

                    await Task.Delay(50);

                    if (isLife) this.leaderActiveFlaskLife = false;
                    else this.leaderActiveFlaskMana = false;

                    lock (controller)
                    {
                        controller.SetButtonState(button, false);
                        controller.SubmitReport();
                    }
                }
                catch
                {
                    this.leaderActiveFlaskLife = false;
                    this.leaderActiveFlaskMana = false;
                }
            });
        }

        /// <summary>
        /// Injects an Auto Buff or Guard skill into Player 1's controller immediately.
        /// </summary>
        public void PressLeaderBuff(CoopPadButton button, int holdMs = 50)
        {
            if (this.leaderXbox == null || button == CoopPadButton.None) return;

            var controller = this.leaderXbox;
            Task.Run(async () =>
            {
                try
                {
                    this.leaderActiveOverrideButton = button;
                    lock (controller)
                    {
                        this.ApplyButton(controller, button, true);
                        controller.SubmitReport();
                    }

                    await Task.Delay(Math.Max(30, holdMs));

                    this.leaderActiveOverrideButton = CoopPadButton.None;
                    lock (controller)
                    {
                        this.ApplyButton(controller, button, false);
                        controller.SubmitReport();
                    }
                }
                catch
                {
                    this.leaderActiveOverrideButton = CoopPadButton.None;
                }
            });
        }

                /// <summary>
        /// Starts real-time keyboard passthrough for Player 2 (Follower) into Virtual Controller #2.
        /// Allows the user to physically press keyboard keys (Arrow keys, Numpad, A/B/X/Y, Shift, Z, C, 1-8)
        /// to directly control and walk Character 2 manually with automatic override.
        /// </summary>
        private void StartFollowerKeyboardPassthrough()
        {
            this.followerPassthroughCts?.Cancel();
            this.followerPassthroughCts = new CancellationTokenSource();
            var token = this.followerPassthroughCts.Token;

            Task.Run(async () =>
            {
                TimeBeginPeriod(1);
                try
                {
                    bool prevUp = false, prevDown = false, prevLeft = false, prevRight = false;
                    bool prevAimUp = false, prevAimDown = false, prevAimLeft = false, prevAimRight = false;

                    while (!token.IsCancellationRequested && this.followerXbox != null)
                    {
                        try
                        {
                            var xbox = this.followerXbox;

                            // ── 1. Left Stick (Movement via Arrow Keys) ──────────────────────
                            // Move Left: Left Arrow | Move Right: Right Arrow
                            // Move Up: Up Arrow     | Move Down: Down Arrow
                            bool up = BotInput.IsKeyDown(VK.UP);
                            bool down = BotInput.IsKeyDown(VK.DOWN);
                            bool left = BotInput.IsKeyDown(VK.LEFT);
                            bool right = BotInput.IsKeyDown(VK.RIGHT);

                            if (up || down || left || right)
                            {
                                this.IsFollowerManualMoving = true;
                                short lx = 0;
                                short ly = 0;
                                if (left) lx -= 32767;
                                if (right) lx += 32767;
                                if (up) ly += 32767;
                                if (down) ly -= 32767;

                                xbox.SetAxisValue(Xbox360Axis.LeftThumbX, lx);
                                xbox.SetAxisValue(Xbox360Axis.LeftThumbY, ly);
                            }
                            else if (prevUp || prevDown || prevLeft || prevRight)
                            {
                                this.IsFollowerManualMoving = false;
                                xbox.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                                xbox.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                            }
                            prevUp = up; prevDown = down; prevLeft = left; prevRight = right;

                            // ── 2. Right Stick (Aiming via Numpad) ───────────────────────────
                            // Move Up: Numpad 8   | Move Down: Numpad 2
                            // Move Left: Numpad 4 | Move Right: Numpad 6
                            bool aimUp = BotInput.IsKeyDown(VK.NUMPAD8);
                            bool aimDown = BotInput.IsKeyDown(VK.NUMPAD2);
                            bool aimLeft = BotInput.IsKeyDown(VK.NUMPAD4);
                            bool aimRight = BotInput.IsKeyDown(VK.NUMPAD6);

                            if (aimUp || aimDown || aimLeft || aimRight)
                            {
                                this.IsFollowerManualAiming = true;
                                short rx = 0;
                                short ry = 0;
                                if (aimLeft) rx -= 32767;
                                if (aimRight) rx += 32767;
                                if (aimUp) ry += 32767;
                                if (aimDown) ry -= 32767;

                                xbox.SetAxisValue(Xbox360Axis.RightThumbX, rx);
                                xbox.SetAxisValue(Xbox360Axis.RightThumbY, ry);
                            }
                            else if (prevAimUp || prevAimDown || prevAimLeft || prevAimRight)
                            {
                                this.IsFollowerManualAiming = false;
                                xbox.SetAxisValue(Xbox360Axis.RightThumbX, 0);
                                xbox.SetAxisValue(Xbox360Axis.RightThumbY, 0);
                            }
                            prevAimUp = aimUp; prevAimDown = aimDown; prevAimLeft = aimLeft; prevAimRight = aimRight;

                            // ── 3. Buttons, Triggers, D-Pad, Controls (Merged User + Bot) ────
                            lock (this.followerBotActiveButtons)
                            {
                                // Buttons: A: A | B: B | X: X | Y: Y
                                bool btnA = BotInput.IsKeyDown(VK.KEY_A) || this.followerBotActiveButtons.Contains(CoopPadButton.A);
                                bool btnB = BotInput.IsKeyDown(VK.KEY_B) || this.followerBotActiveButtons.Contains(CoopPadButton.B) || this.followerSprintActive;
                                bool btnX = BotInput.IsKeyDown(VK.KEY_X) || this.followerBotActiveButtons.Contains(CoopPadButton.X);
                                bool btnY = BotInput.IsKeyDown(VK.KEY_Y) || this.followerBotActiveButtons.Contains(CoopPadButton.Y);

                                // Shoulders: Left Shoulder: Left Shift | Right Shoulder: Right Shift
                                bool btnLB = BotInput.IsKeyDown(VK.LSHIFT) || BotInput.IsKeyDown(VK.SHIFT) || this.followerBotActiveButtons.Contains(CoopPadButton.LeftShoulder);
                                bool btnRB = BotInput.IsKeyDown(VK.RSHIFT) || this.followerBotActiveButtons.Contains(CoopPadButton.RightShoulder);

                                // Triggers: Left Trigger: Z | Right Trigger: C
                                bool btnLT = BotInput.IsKeyDown(VK.KEY_Z) || this.followerBotActiveButtons.Contains(CoopPadButton.LeftTrigger);
                                bool btnRT = BotInput.IsKeyDown(VK.KEY_C) || this.followerBotActiveButtons.Contains(CoopPadButton.RightTrigger);

                                // D-Pad: Up: 1 | Down: 2 | Left: 3 | Right: 4
                                bool dUp = BotInput.IsKeyDown(VK.KEY_1) || this.followerBotActiveButtons.Contains(CoopPadButton.DPadUp);
                                bool dDown = BotInput.IsKeyDown(VK.KEY_2) || this.followerBotActiveButtons.Contains(CoopPadButton.DPadDown);
                                bool dLeft = BotInput.IsKeyDown(VK.KEY_3) || this.followerBotActiveButtons.Contains(CoopPadButton.DPadLeft);
                                bool dRight = BotInput.IsKeyDown(VK.KEY_4) || this.followerBotActiveButtons.Contains(CoopPadButton.DPadRight);

                                // Controls: Back: 5 | Start: 6 | Left Thumb: 7 | Right Thumb: 8
                                bool btnBack = BotInput.IsKeyDown(VK.KEY_5) || this.followerBotActiveButtons.Contains(CoopPadButton.Back);
                                bool btnStart = BotInput.IsKeyDown(VK.KEY_6) || this.followerBotActiveButtons.Contains(CoopPadButton.Start);
                                bool btnLS = BotInput.IsKeyDown(VK.KEY_7);
                                bool btnRS = BotInput.IsKeyDown(VK.KEY_8);

                                xbox.SetButtonState(Xbox360Button.A, btnA);
                                xbox.SetButtonState(Xbox360Button.B, btnB);
                                xbox.SetButtonState(Xbox360Button.X, btnX);
                                xbox.SetButtonState(Xbox360Button.Y, btnY);
                                xbox.SetButtonState(Xbox360Button.LeftShoulder, btnLB);
                                xbox.SetButtonState(Xbox360Button.RightShoulder, btnRB);
                                xbox.SetSliderValue(Xbox360Slider.LeftTrigger, btnLT ? (byte)255 : (byte)0);
                                xbox.SetSliderValue(Xbox360Slider.RightTrigger, btnRT ? (byte)255 : (byte)0);

                                xbox.SetButtonState(Xbox360Button.Up, dUp);
                                xbox.SetButtonState(Xbox360Button.Down, dDown);
                                xbox.SetButtonState(Xbox360Button.Left, dLeft);
                                xbox.SetButtonState(Xbox360Button.Right, dRight);
                                xbox.SetButtonState(Xbox360Button.Back, btnBack);
                                xbox.SetButtonState(Xbox360Button.Start, btnStart);
                                xbox.SetButtonState(Xbox360Button.LeftThumb, btnLS);
                                xbox.SetButtonState(Xbox360Button.RightThumb, btnRS);
                            }

                            lock (this.followerXbox)
                            {
                                xbox.SubmitReport();
                            }
                        }
                        catch
                        {
                            // Transient skip
                        }

                        await Task.Delay(4, token);
                    }
                }
                finally
                {
                    TimeEndPeriod(1);
                }
            }, token);
        }

        private void ApplyButton(IXbox360Controller controller, CoopPadButton button, bool pressed)
        {
            switch (button)
            {
                case CoopPadButton.A: controller.SetButtonState(Xbox360Button.A, pressed); break;
                case CoopPadButton.B: controller.SetButtonState(Xbox360Button.B, pressed); break;
                case CoopPadButton.X: controller.SetButtonState(Xbox360Button.X, pressed); break;
                case CoopPadButton.Y: controller.SetButtonState(Xbox360Button.Y, pressed); break;
                case CoopPadButton.LeftShoulder: controller.SetButtonState(Xbox360Button.LeftShoulder, pressed); break;
                case CoopPadButton.RightShoulder: controller.SetButtonState(Xbox360Button.RightShoulder, pressed); break;
                case CoopPadButton.LeftTrigger: controller.SetSliderValue(Xbox360Slider.LeftTrigger, pressed ? (byte)255 : (byte)0); break;
                case CoopPadButton.RightTrigger: controller.SetSliderValue(Xbox360Slider.RightTrigger, pressed ? (byte)255 : (byte)0); break;
                case CoopPadButton.Start: controller.SetButtonState(Xbox360Button.Start, pressed); break;
                case CoopPadButton.Back: controller.SetButtonState(Xbox360Button.Back, pressed); break;
                case CoopPadButton.DPadUp: controller.SetButtonState(Xbox360Button.Up, pressed); break;
                case CoopPadButton.DPadDown: controller.SetButtonState(Xbox360Button.Down, pressed); break;
                case CoopPadButton.DPadLeft: controller.SetButtonState(Xbox360Button.Left, pressed); break;
                case CoopPadButton.DPadRight: controller.SetButtonState(Xbox360Button.Right, pressed); break;
            }
        }

        private static Xbox360Button? MapToXboxButton(CoopPadButton button)
        {
            return button switch
            {
                CoopPadButton.A => Xbox360Button.A,
                CoopPadButton.B => Xbox360Button.B,
                CoopPadButton.X => Xbox360Button.X,
                CoopPadButton.Y => Xbox360Button.Y,
                CoopPadButton.LeftShoulder => Xbox360Button.LeftShoulder,
                CoopPadButton.RightShoulder => Xbox360Button.RightShoulder,
                CoopPadButton.Start => Xbox360Button.Start,
                CoopPadButton.Back => Xbox360Button.Back,
                CoopPadButton.DPadUp => Xbox360Button.Up,
                CoopPadButton.DPadDown => Xbox360Button.Down,
                CoopPadButton.DPadLeft => Xbox360Button.Left,
                CoopPadButton.DPadRight => Xbox360Button.Right,
                _ => null,
            };
        }

        /// <summary>
        /// Nudges Controller 2 in a small circular wobble for 400ms so user can visually verify in-game.
        /// </summary>
        public void TestWobbleFollower()
        {
            if (this.followerXbox == null) return;

            var xbox = this.followerXbox;
            Task.Run(async () =>
            {
                try
                {
                    float[] angles = { 0f, 90f, 180f, 270f, 0f };
                    foreach (var deg in angles)
                    {
                        float rad = deg * (MathF.PI / 180f);
                        short x = (short)(MathF.Cos(rad) * 28000f);
                        short y = (short)(MathF.Sin(rad) * 28000f);
                        xbox.SetAxisValue(Xbox360Axis.LeftThumbX, x);
                        xbox.SetAxisValue(Xbox360Axis.LeftThumbY, y);
                        xbox.SubmitReport();
                        await Task.Delay(80);
                    }

                    xbox.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                    xbox.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                    xbox.SubmitReport();
                }
                catch
                {
                    xbox.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                    xbox.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                    xbox.SubmitReport();
                }
            });
        }

        /// <summary>
        /// Resets all sticks and buttons on virtual controllers to neutral/released state
        /// without unplugging/disconnecting the controllers from Windows and the game.
        /// </summary>
        public void ResetAllInputs()
        {
            this.followerSprintActive = false;
            lock (this.followerBotActiveButtons)
            {
                this.followerBotActiveButtons.Clear();
            }

            if (this.followerXbox != null)
            {
                try
                {
                    lock (this.followerXbox)
                    {
                        this.followerXbox.SetAxisValue(Xbox360Axis.LeftThumbX, 0);
                        this.followerXbox.SetAxisValue(Xbox360Axis.LeftThumbY, 0);
                        this.followerXbox.SetAxisValue(Xbox360Axis.RightThumbX, 0);
                        this.followerXbox.SetAxisValue(Xbox360Axis.RightThumbY, 0);
                        this.followerXbox.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
                        this.followerXbox.SetSliderValue(Xbox360Slider.RightTrigger, 0);
                        this.followerXbox.ResetReport();
                        this.followerXbox.SubmitReport();
                    }
                }
                catch
                {
                    // Ignore transient
                }
            }

            this.leaderActiveOverrideButton = CoopPadButton.None;
            this.leaderActiveFlaskLife = false;
            this.leaderActiveFlaskMana = false;
        }

        public void Disconnect()
        {
            this.passthroughCts?.Cancel();
            this.passthroughCts = null;
            this.followerPassthroughCts?.Cancel();
            this.followerPassthroughCts = null;
            this.IsFollowerManualMoving = false;
            this.IsFollowerManualAiming = false;

            try
            {
                if (this.followerXbox != null)
                {
                    this.followerXbox.Disconnect();
                    this.followerXbox = null;
                }

                if (this.leaderXbox != null)
                {
                    this.leaderXbox.Disconnect();
                    this.leaderXbox = null;
                }

                this.vigemClient?.Dispose();
                this.vigemClient = null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CoopVirtualGamepad] Disconnect error: {ex.Message}");
            }
            finally
            {
                this.FollowerSlotIndex = -1;
                this.LeaderSlotIndex = -1;
            }
        }

        public void Dispose()
        {
            this.Disconnect();
            GC.SuppressFinalize(this);
        }
    }
}
