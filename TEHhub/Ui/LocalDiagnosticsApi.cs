namespace TEHhub.Ui
{
    using System;
    using System.Net;
    using System.Text;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Text.Json.Serialization.Metadata;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// A loopback-only diagnostic endpoint for local tooling. It intentionally exposes no game
    /// input, plugin control, or network listener outside this computer.
    /// </summary>
#if DEBUG
    internal static class LocalDiagnosticsApi
    {
        private const int Port = 9877;
        private static readonly object Gate = new();
        private static HttpListener? listener;
        private static CancellationTokenSource? cancellation;

        internal static void Start()
        {
            lock (Gate)
            {
                if (listener != null)
                {
                    return;
                }

                try
                {
                    var newListener = new HttpListener();
                    newListener.Prefixes.Add($"http://localhost:{Port}/");
                    newListener.Start();

                    var newCancellation = new CancellationTokenSource();
                    listener = newListener;
                    cancellation = newCancellation;
                    _ = Task.Run(() => ListenAsync(newListener, newCancellation.Token));
                    Console.WriteLine($"[LocalDiagnosticsApi] Listening on http://localhost:{Port}/.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LocalDiagnosticsApi] Unable to start: {ex.Message}");
                }
            }
        }

        internal static void Stop()
        {
            lock (Gate)
            {
                cancellation?.Cancel();
                cancellation?.Dispose();
                cancellation = null;

                listener?.Close();
                listener = null;
            }
        }

        private static async Task ListenAsync(HttpListener activeListener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await activeListener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LocalDiagnosticsApi] Listener failure: {ex.Message}");
                    break;
                }

                _ = HandleRequestAsync(context);
            }
        }

        private static async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                var path = context.Request.Url?.AbsolutePath;
                var method = context.Request.HttpMethod;
                if (method == "GET" && path == "/api/tools")
                {
                    var catalog = new ToolApiCatalog($"http://localhost:{Port}", new[] {
                        "GET /api/tools/state", "GET /api/tools/diagnostics", "GET /api/tools/diagnostics/{id}",
                        "GET /api/tools/logs", "POST /api/tools/windows/{ShowSetting}?visible=true|false",
                        "POST /api/diagnostics/skill-research", "GET /api/diagnostics/offset-status",
                        "POST /api/diagnostics/offset-verify", "GET /api/diagnostics/memory-status",
                        "GET /api/diagnostics/memory-snapshot", "POST /api/diagnostics/memory-reset",
                        "POST /api/diagnostics/memory-dump", "POST /api/diagnostics/memory-stop",
                        "GET /api/diagnostics/performance-snapshot", "POST /api/diagnostics/performance-reset",
                        "GET /api/diagnostics/capture-status", "GET /api/diagnostics/bottleneck-snapshot",
                        "POST /api/diagnostics/capture-start", "POST /api/diagnostics/capture-area-start",
                        "POST /api/diagnostics/capture-stop" }, Array.ConvertAll(ToolDiagnostics.Snapshot(), t => t.Id));
                    await WriteJsonAsync(context, 200, catalog, DiagnosticsApiJsonContext.Default.ToolApiCatalog).ConfigureAwait(false);
                    return;
                }
                if (method == "POST" && path != null && path.StartsWith("/api/tools/windows/", StringComparison.Ordinal))
                {
                    var valid = bool.TryParse(context.Request.QueryString["visible"], out var visible) &&
                        ToolHub.RequestWindow(path["/api/tools/windows/".Length..], visible);
                    await WriteJsonAsync(context, valid ? 202 : 400, new DiagnosticsApiResponse(valid ? "Window change queued on render thread." : "Invalid window or visible flag."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    return;
                }
                if (method == "GET" && path == "/api/tools/state")
                {
                    var state = ToolHub.Snapshot;
                    if (state == null)
                        await WriteJsonAsync(context, 202, new DiagnosticsApiResponse("Waiting for render-thread state."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    else
                        await WriteJsonAsync(context, 200, state, DiagnosticsApiJsonContext.Default.ToolHubSnapshot).ConfigureAwait(false);
                    return;
                }
                if (method == "GET" && path == "/api/tools/diagnostics")
                {
                    await WriteJsonAsync(context, 200, ToolDiagnostics.Snapshot(), DiagnosticsApiJsonContext.Default.ToolDiagnosticArray).ConfigureAwait(false);
                    return;
                }
                if (method == "GET" && path == "/api/tools/logs")
                {
                    await WriteJsonAsync(context, 200, RuntimeLog.Snapshot(), DiagnosticsApiJsonContext.Default.RuntimeLogEntryArray).ConfigureAwait(false);
                    return;
                }
                if (method == "GET" && path != null && path.StartsWith("/api/tools/diagnostics/", StringComparison.Ordinal))
                {
                    var entry = ToolDiagnostics.Find(Uri.UnescapeDataString(path["/api/tools/diagnostics/".Length..]));
                    if (entry == null)
                        await WriteJsonAsync(context, 404, new DiagnosticsApiResponse("Tool has not published diagnostics."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    else
                        await WriteJsonAsync(context, 200, entry, DiagnosticsApiJsonContext.Default.ToolDiagnostic).ConfigureAwait(false);
                    return;
                }
                if (method == "POST" && path == "/api/diagnostics/skill-research")
                {
                    var capture = SkillResearchCapture.Request();
                    if (capture == null)
                        await WriteJsonAsync(context, 409, new DiagnosticsApiResponse("Capture already pending."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    else
                    {
                        var snapshot = await capture.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        await WriteJsonAsync(context, 200, snapshot, DiagnosticsApiJsonContext.Default.SkillResearchSnapshot).ConfigureAwait(false);
                    }
                    return;
                }
                if (method == "POST" && path == "/api/diagnostics/capture-area-start")
                {
                    BottleneckCapture.RequestAreaStart();
                    await WriteJsonAsync(context, 202, new DiagnosticsApiResponse("Area capture queued; excludes town and hideout."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    return;
                }
                if (method == "GET" && path == "/api/diagnostics/capture-status")
                {
                    await WriteJsonAsync(context, 200, BottleneckCapture.GetStatus(), DiagnosticsApiJsonContext.Default.CaptureStatus).ConfigureAwait(false);
                    return;
                }
                if (method == "POST" && path == "/api/diagnostics/capture-start")
                {
                    BottleneckCapture.RequestStart();
                    await WriteJsonAsync(context, 202, new DiagnosticsApiResponse("Capture queued; automatic stop after 120 seconds."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    return;
                }
                if (method == "POST" && path == "/api/diagnostics/capture-stop")
                {
                    BottleneckCapture.RequestStop();
                    await WriteJsonAsync(context, 202, new DiagnosticsApiResponse("Capture stop queued."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    return;
                }
                if (method == "GET" && path == "/api/diagnostics/bottleneck-snapshot")
                {
                    var snapshot = BottleneckCapture.Snapshot;
                    if (snapshot == null)
                        await WriteJsonAsync(context, 202, new DiagnosticsApiResponse("No published capture; start capture and wait for render frames."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    else
                        await WriteJsonAsync(context, 200, snapshot, DiagnosticsApiJsonContext.Default.BottleneckSnapshot).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && path == "/api/diagnostics/offset-status")
                {
                    await WriteJsonAsync(context, 200, OffsetHelper.GetVerificationStatus(), DiagnosticsApiJsonContext.Default.OffsetVerificationStatus).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && path == "/api/diagnostics/offset-verify")
                {
                    OffsetHelper.RequestVerification();
                    await WriteJsonAsync(context, 202, new DiagnosticsApiResponse("Offset verification queued."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && path == "/api/diagnostics/memory-status")
                {
                    await WriteJsonAsync(context, 200, MemoryReadDiagnostics.GetApiStatus(), DiagnosticsApiJsonContext.Default.MemoryDiagnosticsStatus).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && path == "/api/diagnostics/memory-snapshot")
                {
                    await WriteJsonAsync(context, 200, MemoryReadDiagnostics.GetApiSnapshot(), DiagnosticsApiJsonContext.Default.MemoryDiagnosticsSnapshot).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && path == "/api/diagnostics/performance-snapshot")
                {
                    await WriteJsonAsync(context, 200, PerformanceProfiler.GetApiSnapshot(), DiagnosticsApiJsonContext.Default.PerformanceProfilerSnapshot).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && path == "/api/diagnostics/performance-reset")
                {
                    PerformanceProfiler.Reset();
                    await WriteJsonAsync(context, 202, new DiagnosticsApiResponse("Performance profiler reset."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && path == "/api/diagnostics/memory-reset")
                {
                    MemoryReadDiagnostics.RequestReset();
                    await WriteJsonAsync(context, 202, new DiagnosticsApiResponse("Memory diagnostics reset queued."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && path == "/api/diagnostics/memory-dump")
                {
                    MemoryReadDiagnostics.RequestDump();
                    await WriteJsonAsync(context, 202, new DiagnosticsApiResponse("Memory diagnostics dump queued."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    return;
                }

                if (method == "POST" && path == "/api/diagnostics/memory-stop")
                {
                    MemoryReadDiagnostics.RequestStop();
                    await WriteJsonAsync(context, 202, new DiagnosticsApiResponse("Memory diagnostics stop queued."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(context, 404, new DiagnosticsApiResponse("Route not found."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LocalDiagnosticsApi] Request failure: {ex.Message}");
                try
                {
                    await WriteJsonAsync(context, 500, new DiagnosticsApiResponse("Diagnostic request failed."), DiagnosticsApiJsonContext.Default.DiagnosticsApiResponse).ConfigureAwait(false);
                }
                catch
                {
                    // The caller may already have disconnected; diagnostics must not affect the overlay.
                }
            }
        }

        private static async Task WriteJsonAsync<T>(HttpListenerContext context, int statusCode, T value, JsonTypeInfo<T> typeInfo)
        {
            var json = JsonSerializer.Serialize(value, typeInfo);
            var data = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = statusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = data.Length;
            await context.Response.OutputStream.WriteAsync(data).ConfigureAwait(false);
            context.Response.Close();
        }
    }

#endif
    internal sealed record DiagnosticsApiResponse(string Message);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(OffsetHelper.OffsetVerificationStatus))]
    [JsonSerializable(typeof(MemoryDiagnosticsStatus))]
    [JsonSerializable(typeof(MemoryDiagnosticsSnapshot))]
    [JsonSerializable(typeof(PerformanceProfilerSnapshot))]
    [JsonSerializable(typeof(DiagnosticsApiResponse))]
    [JsonSerializable(typeof(BottleneckSnapshot))]
    [JsonSerializable(typeof(CaptureStatus))]
    [JsonSerializable(typeof(AreaCaptureReport))]
    [JsonSerializable(typeof(SkillResearchSnapshot))]
#if DEBUG
    [JsonSerializable(typeof(ToolHubSnapshot))]
    [JsonSerializable(typeof(ToolDiagnostic[]))]
    [JsonSerializable(typeof(ToolDiagnostic))]
    [JsonSerializable(typeof(RuntimeLogEntry[]))]
    [JsonSerializable(typeof(ToolApiCatalog))]
#endif
    internal sealed partial class DiagnosticsApiJsonContext : JsonSerializerContext
    {
    }
}
