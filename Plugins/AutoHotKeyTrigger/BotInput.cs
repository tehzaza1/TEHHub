// <copyright file="ControllerInput.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace AutoHotKeyTrigger
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Runtime.InteropServices;
    using System.Threading.Tasks;
    using ClickableTransparentOverlay.Win32;
    using GameHelper;
    using Nefarius.ViGEm.Client;
    using Nefarius.ViGEm.Client.Targets;
    using Nefarius.ViGEm.Client.Targets.Xbox360;

    /// <summary>
    ///     Gamepad button values matching XInput XINPUT_GAMEPAD.wButtons.
    /// </summary>
    public enum GamepadButton : ushort
    {
        /// <summary>No button.</summary>
        None = 0,

        /// <summary>D-Pad Up.</summary>
        DPadUp = 0x0001,

        /// <summary>D-Pad Down.</summary>
        DPadDown = 0x0002,

        /// <summary>D-Pad Left.</summary>
        DPadLeft = 0x0004,

        /// <summary>D-Pad Right.</summary>
        DPadRight = 0x0008,

        /// <summary>Start button.</summary>
        Start = 0x0010,

        /// <summary>Back / Select button.</summary>
        Back = 0x0020,

        /// <summary>Left stick click (L3).</summary>
        LeftThumb = 0x0040,

        /// <summary>Right stick click (R3).</summary>
        RightThumb = 0x0080,

        /// <summary>Left bumper (LB).</summary>
        LeftShoulder = 0x0100,

        /// <summary>Right bumper (RB).</summary>
        RightShoulder = 0x0200,

        /// <summary>A button.</summary>
        A = 0x1000,

        /// <summary>B button.</summary>
        B = 0x2000,

        /// <summary>X button.</summary>
        X = 0x4000,

        /// <summary>Y button.</summary>
        Y = 0x8000,

        /// <summary>Left trigger (LT) — virtual flag, checked via analog threshold.</summary>
        LeftTrigger = 0x0400,

        /// <summary>Right trigger (RT) — virtual flag, checked via analog threshold.</summary>
        RightTrigger = 0x0800,
    }

    /// <summary>
    ///     Provides XInput gamepad polling and direct keyboard key-press
    ///     to bypass <c>MiscHelper.KeyUp</c> which is blocked in controller mode.
    /// </summary>
    internal static partial class BotInput
    {
        [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static partial uint TimeBeginPeriod(uint uMilliseconds);

        [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static partial uint TimeEndPeriod(uint uMilliseconds);

        private const uint InputKeyboard = 1;
        private const uint KeyEventFlagKeyUp = 0x0002;

        /// <summary>
        ///     Analog trigger threshold (0-255). Values above this count as "pressed".
        /// </summary>
        private const byte TriggerThreshold = 100;

        /// <summary>
        ///     Rate-limit stopwatch mirroring MiscHelper's delay between key-presses.
        /// </summary>
        private static readonly Stopwatch DelayBetweenKeys = Stopwatch.StartNew();

        /// <summary>
        ///     Random jitter for the delay, same idea as MiscHelper.
        /// </summary>
        private static readonly Random Rand = new();

        /// <summary>
        ///     Background task for the most recent input call.
        /// </summary>
        private static Task? sendingMessage;

        // ── ViGEm Virtual Controller ─────────────────────────────────
        private static ViGEmClient? vigemClient;
        private static IXbox360Controller? virtualXbox;
        public static int ConnectedUserIndex = -1;
        private static int storedPhysicalIndex = 0;
        private static System.Threading.CancellationTokenSource? passthroughCts;
        private static int activeOverrideButton = 0;

        public static bool EnsureViGEm(int physicalControllerIndex = 0)
        {
            if (virtualXbox != null)
            {
                return true;
            }

            // virtualXbox is null — always try to connect (allows retry after transient failures).
            try
            {
                // Snapshot which slots are active BEFORE connecting our virtual controller
                var slotsBefore = new bool[4];
                for (int slot = 0; slot < 4; slot++)
                {
                    var s = default(XINPUT_STATE);
                    slotsBefore[slot] = XInputGetState(slot, ref s) == 0;
                }

                vigemClient = new ViGEmClient();
                virtualXbox = vigemClient.CreateXbox360Controller();
                virtualXbox.Connect();

                // Small delay so Windows registers the new controller
                System.Threading.Thread.Sleep(200);

                // The slot that was NOT active before is our virtual controller
                ConnectedUserIndex = -1;
                for (int slot = 0; slot < 4; slot++)
                {
                    var s = default(XINPUT_STATE);
                    bool activeNow = XInputGetState(slot, ref s) == 0;
                    if (activeNow && !slotsBefore[slot])
                    {
                        ConnectedUserIndex = slot;
                        break;
                    }
                }

                // Fallback: if detection failed (e.g. all slots were already used), pick last active slot
                if (ConnectedUserIndex < 0)
                {
                    for (int slot = 0; slot < 4; slot++)
                    {
                        var s = default(XINPUT_STATE);
                        if (XInputGetState(slot, ref s) == 0)
                        {
                            ConnectedUserIndex = slot;
                        }
                    }
                }

                storedPhysicalIndex = physicalControllerIndex;
                StartPassthrough(physicalControllerIndex);
                return true;
            }
            catch (Exception ex)
            {
                virtualXbox = null;
                vigemClient = null;
                Console.WriteLine($"[AutoHotKeyTrigger] Failed to initialize ViGEm: {ex.Message}");
                return false;
            }
        }

        private static void StartPassthrough(int physicalIndex)
        {
            passthroughCts?.Cancel();
            passthroughCts = new System.Threading.CancellationTokenSource();
            var token = passthroughCts.Token;

            Task.Run(async () =>
            {
                TimeBeginPeriod(1);
                try
                {
                    while (!token.IsCancellationRequested && virtualXbox != null)
                    {
                        try
                        {
                            var state = default(XINPUT_STATE);
                            if (XInputGetState(physicalIndex, ref state) == 0)
                            {
                                var pad = state.Gamepad;
                                ushort buttons = pad.wButtons;

                                // Inject Auto Flask button if active
                                if (activeOverrideButton != 0)
                                {
                                    buttons |= (ushort)activeOverrideButton;
                                }

                                // Update Virtual Controller Buttons
                                virtualXbox.SetButtonState(Xbox360Button.Up, (buttons & 0x0001) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.Down, (buttons & 0x0002) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.Left, (buttons & 0x0004) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.Right, (buttons & 0x0008) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.Start, (buttons & 0x0010) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.Back, (buttons & 0x0020) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.LeftThumb, (buttons & 0x0040) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.RightThumb, (buttons & 0x0080) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.LeftShoulder, (buttons & 0x0100) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.RightShoulder, (buttons & 0x0200) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.A, (buttons & 0x1000) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.B, (buttons & 0x2000) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.X, (buttons & 0x4000) != 0);
                                virtualXbox.SetButtonState(Xbox360Button.Y, (buttons & 0x8000) != 0);

                                // Update Triggers & Sticks
                                virtualXbox.SetSliderValue(Xbox360Slider.LeftTrigger, pad.bLeftTrigger);
                                virtualXbox.SetSliderValue(Xbox360Slider.RightTrigger, pad.bRightTrigger);
                                virtualXbox.SetAxisValue(Xbox360Axis.LeftThumbX, pad.sThumbLX);
                                virtualXbox.SetAxisValue(Xbox360Axis.LeftThumbY, pad.sThumbLY);
                                virtualXbox.SetAxisValue(Xbox360Axis.RightThumbX, pad.sThumbRX);
                                virtualXbox.SetAxisValue(Xbox360Axis.RightThumbY, pad.sThumbRY);

                                virtualXbox.SubmitReport();
                            }
                        }
                        catch
                        {
                            // Ignore transient exceptions
                        }

                        await Task.Delay(2, token); // True 2ms update rate (~500 Hz) with 1ms timer precision
                    }
                }
                finally
                {
                    TimeEndPeriod(1);
                }
            }, token);
        }

        // ── XInput P/Invoke ──────────────────────────────────────────

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

        [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static partial int XInputGetState(int dwUserIndex, ref XINPUT_STATE pState);

        [LibraryImport("user32.dll")]
        private static partial IntPtr GetForegroundWindow();

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial uint SendInput(uint inputCount, [In] Input[] inputs, int inputSize);

        // ── Public API ───────────────────────────────────────────────

        /// <summary>
        ///     Checks whether the specified gamepad button is currently pressed.
        /// </summary>
        /// <param name="button">Button to check.</param>
        /// <param name="controllerIndex">XInput user index (0-3).</param>
        /// <returns>true when pressed.</returns>
        public static bool IsGamepadButtonDown(GamepadButton button, int controllerIndex = 0)
        {
            if (button == GamepadButton.None)
            {
                return false;
            }

            var state = default(XINPUT_STATE);
            var result = XInputGetState(controllerIndex, ref state);
            if (result != 0) // ERROR_SUCCESS = 0
            {
                return false;
            }

            // Handle analog triggers via the virtual flags.
            if (button == GamepadButton.LeftTrigger)
            {
                return state.Gamepad.bLeftTrigger > TriggerThreshold;
            }

            if (button == GamepadButton.RightTrigger)
            {
                return state.Gamepad.bRightTrigger > TriggerThreshold;
            }

            return (state.Gamepad.wButtons & (ushort)button) != 0;
        }

        /// <summary>
        ///     Sends a real keyboard down/up input pair, bypassing <c>MiscHelper.KeyUp</c>
        ///     which is blocked in controller mode. Unlike window messages, SendInput reaches
        ///     the game's regular keyboard input path.
        ///     Includes rate-limiting equivalent to MiscHelper's 30-40ms delay.
        /// </summary>
        /// <param name="key">Virtual key to send.</param>
        /// <returns>true if the key was actually sent.</returns>
        public static bool PressKey(VK key)
        {
            if (sendingMessage != null && !sendingMessage.IsCompleted)
            {
                return false;
            }

            var timeout = Core.GHSettings.KeyPressTimeout + Rand.Next() % 10;
            if (DelayBetweenKeys.ElapsedMilliseconds < timeout)
            {
                return false;
            }

            DelayBetweenKeys.Restart();

            // ShouldExecutePlugin() already confirmed the game is foreground; repeat the
            // check immediately before injecting to avoid sending input into another app.
            if (GetForegroundWindow() != Core.Process.MainWindowHandle)
            {
                return false;
            }

            var keyDown = new Input((ushort)key, 0);
            var keyUp = new Input((ushort)key, KeyEventFlagKeyUp);
            sendingMessage = Task.Run(async () =>
            {
                _ = SendInput(1, [keyDown], Marshal.SizeOf<Input>());
                await Task.Delay(35);
                _ = SendInput(1, [keyUp], Marshal.SizeOf<Input>());
            });

            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Input
        {
            public Input(uint virtualKey, uint flags)
            {
                this.Type = InputKeyboard;
                this.Union = new InputUnion(new KeyboardInput(virtualKey, flags));
            }

            public uint Type;
            public InputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            public InputUnion(KeyboardInput keyboard)
            {
                this.Keyboard = keyboard;
            }

            [FieldOffset(0)]
            public KeyboardInput Keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public KeyboardInput(uint virtualKey, uint flags)
            {
                this.VirtualKey = (ushort)virtualKey;
                this.ScanCode = 0;
                this.Flags = flags;
                this.Time = 0;
                this.ExtraInfo = IntPtr.Zero;
            }

            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        /// <summary>
        ///     Simulates pressing a real Gamepad button (e.g. DPadRight for Mana, DPadLeft for Life)
        ///     via ViGEm virtual Xbox360 controller.
        /// </summary>
        public static bool PressGamepadButton(GamepadButton button)
        {
            if (button == GamepadButton.None)
            {
                return false;
            }

            if (sendingMessage != null && !sendingMessage.IsCompleted)
            {
                return false;
            }

            var timeout = Core.GHSettings.KeyPressTimeout + Rand.Next() % 10;
            if (DelayBetweenKeys.ElapsedMilliseconds < timeout)
            {
                return false;
            }

            if (!EnsureViGEm(storedPhysicalIndex) || virtualXbox == null)
            {
                return false;
            }

            DelayBetweenKeys.Restart();

            // Capture local reference so Shutdown() can't null it mid-press
            var controller = virtualXbox;
            sendingMessage = Task.Run(async () =>
            {
                try
                {
                    activeOverrideButton = (int)button;
                    ApplyGamepadButtonState(controller, button, true);
                    controller.SubmitReport();

                    await Task.Delay(50);

                    activeOverrideButton = 0;
                    ApplyGamepadButtonState(controller, button, false);
                    controller.SubmitReport();
                }
                catch
                {
                    activeOverrideButton = 0;
                }
            });

            return true;
        }

        private static void ApplyGamepadButtonState(IXbox360Controller controller, GamepadButton button, bool pressed)
        {
            switch (button)
            {
                case GamepadButton.DPadUp:
                    controller.SetButtonState(Xbox360Button.Up, pressed);
                    break;
                case GamepadButton.DPadDown:
                    controller.SetButtonState(Xbox360Button.Down, pressed);
                    break;
                case GamepadButton.DPadLeft:
                    controller.SetButtonState(Xbox360Button.Left, pressed);
                    break;
                case GamepadButton.DPadRight:
                    controller.SetButtonState(Xbox360Button.Right, pressed);
                    break;
                case GamepadButton.A:
                    controller.SetButtonState(Xbox360Button.A, pressed);
                    break;
                case GamepadButton.B:
                    controller.SetButtonState(Xbox360Button.B, pressed);
                    break;
                case GamepadButton.X:
                    controller.SetButtonState(Xbox360Button.X, pressed);
                    break;
                case GamepadButton.Y:
                    controller.SetButtonState(Xbox360Button.Y, pressed);
                    break;
                case GamepadButton.LeftShoulder:
                    controller.SetButtonState(Xbox360Button.LeftShoulder, pressed);
                    break;
                case GamepadButton.RightShoulder:
                    controller.SetButtonState(Xbox360Button.RightShoulder, pressed);
                    break;
                case GamepadButton.LeftThumb:
                    controller.SetButtonState(Xbox360Button.LeftThumb, pressed);
                    break;
                case GamepadButton.RightThumb:
                    controller.SetButtonState(Xbox360Button.RightThumb, pressed);
                    break;
                case GamepadButton.Start:
                    controller.SetButtonState(Xbox360Button.Start, pressed);
                    break;
                case GamepadButton.Back:
                    controller.SetButtonState(Xbox360Button.Back, pressed);
                    break;
                case GamepadButton.LeftTrigger:
                    controller.SetSliderValue(Xbox360Slider.LeftTrigger, pressed ? (byte)255 : (byte)0);
                    break;
                case GamepadButton.RightTrigger:
                    controller.SetSliderValue(Xbox360Slider.RightTrigger, pressed ? (byte)255 : (byte)0);
                    break;
            }
        }

        /// <summary>
        ///     Cleans up ViGEm virtual controller on unload.
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                passthroughCts?.Cancel();
                passthroughCts = null;

                virtualXbox?.Disconnect();
                virtualXbox = null;
                vigemClient?.Dispose();
                vigemClient = null;
                ConnectedUserIndex = -1;
            }
            catch
            {
            }
        }
    }
}
