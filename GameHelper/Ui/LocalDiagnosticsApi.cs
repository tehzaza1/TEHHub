namespace GameHelper.Ui
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

    internal sealed record DiagnosticsApiResponse(string Message);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(OffsetHelper.OffsetVerificationStatus))]
    [JsonSerializable(typeof(MemoryDiagnosticsStatus))]
    [JsonSerializable(typeof(DiagnosticsApiResponse))]
    internal sealed partial class DiagnosticsApiJsonContext : JsonSerializerContext
    {
    }
}
