// <copyright file="TEHhubOverlay.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using ClickableTransparentOverlay;
    using Coroutine;
    using CoroutineEvents;
    using TEHhub.Utils;
    using ImGuiNET;
    using Plugin;
    using Settings;
    using Ui;

    /// <inheritdoc />
    public sealed class TEHhubOverlay : Overlay
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="TEHhubOverlay" /> class.
        /// </summary>
        internal TEHhubOverlay(string windowTitle)
            : base(windowTitle, true, 3840, 2160)
        {
            CoroutineHandler.Start(this.UpdateOverlayBounds(), priority: int.MaxValue);
            SettingsWindow.InitializeCoroutines();
            PerformanceStats.InitializeCoroutines();
            DataVisualization.InitializeCoroutines();
            GameUiExplorer.InitializeCoroutines();
            ElementFinder.InitializeCoroutines();
            PerformanceProfiler.InitializeCoroutines();
            MemoryReadDiagnostics.InitializeCoroutines();
            OffsetHelper.InitializeCoroutines();
            OffsetHelperV2.InitializeCoroutines();
#if DEBUG
            LocalDiagnosticsApi.Start();
#endif
            OverlayKiller.InitializeCoroutines();
            NearbyVisualization.InitializeCoroutines();
            KrangledPassiveDetector.InitializeCoroutines();
        }

        /// <summary>
        ///     Gets the fonts loaded in the overlay.
        /// </summary>
        public ImFontPtr[]? Fonts { get; private set; }

        /// <inheritdoc />
        public override async Task Run()
        {
            Core.Initialize();
            Core.InitializeCororutines();
            this.VSync = Core.GHSettings.Vsync;
            this.FPSLimit = Core.GHSettings.FPSLimit;
            await base.Run();
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
#if DEBUG
                LocalDiagnosticsApi.Stop();
#endif
                Core.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <inheritdoc />
        protected override Task PostInitialized()
        {
            Ui.ImGuiTheme.Apply();

            UniversalFont.ApplyFromSettings();

            PManager.InitializePlugins();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        protected override void Render()
        {
            var captureTimestamp = BottleneckCapture.BeginFrame();
            PerformanceProfiler.StartFrame();

            try { CoroutineHandler.Tick(ImGui.GetIO().DeltaTime); }
            catch (Exception ex) { Console.Error.WriteLine($"[TEHhubOverlay.Render.Tick] {ex}"); }

            try { CoroutineHandler.RaiseEvent(TEHhubEvents.PerFrameDataUpdate); }
            catch (Exception ex) { Console.Error.WriteLine($"[TEHhubOverlay.Render.PerFrameDataUpdate] {ex}"); }

            try { CoroutineHandler.RaiseEvent(TEHhubEvents.PostPerFrameDataUpdate); }
            catch (Exception ex) { Console.Error.WriteLine($"[TEHhubOverlay.Render.PostPerFrameDataUpdate] {ex}"); }

            try { CoroutineHandler.RaiseEvent(TEHhubEvents.OnRender); }
            catch (Exception ex) { Console.Error.WriteLine($"[TEHhubOverlay.Render.OnRender] {ex}"); }

            try { CoroutineHandler.RaiseEvent(TEHhubEvents.OnPostRender); }
            catch (Exception ex) { Console.Error.WriteLine($"[TEHhubOverlay.Render.OnPostRender] {ex}"); }

            PerformanceProfiler.EndFrame();
            MemoryReadDiagnostics.RecordFrame();
            BottleneckCapture.EndFrame(captureTimestamp);

            if (!Core.GHSettings.IsOverlayRunning)
            {
                this.Close();
            }
        }

        private IEnumerator<Wait> UpdateOverlayBounds()
        {
            while (true)
            {
                yield return new Wait(TEHhubEvents.OnMoved);
                this.Position = Core.Process.WindowArea.Location;
                this.Size = Core.Process.WindowArea.Size -
                    (Core.GHSettings.FixTaskbarNotShowing ?
                        new System.Drawing.Size(0, 1) :
                        System.Drawing.Size.Empty);
            }
        }
    }
}
