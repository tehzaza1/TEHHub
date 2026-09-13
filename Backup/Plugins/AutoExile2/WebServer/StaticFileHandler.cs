// <copyright file="StaticFileHandler.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.WebServer
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Reflection;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>
    /// Handles static file serving for the dashboard HTML, styles, scripts, and dump artifacts.
    /// </summary>
    public static class StaticFileHandler
    {
        private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".html", "text/html; charset=utf-8" },
            { ".htm", "text/html; charset=utf-8" },
            { ".js", "application/javascript; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" },
            { ".json", "application/json; charset=utf-8" },
            { ".png", "image/png" },
            { ".jpg", "image/jpeg" },
            { ".jpeg", "image/jpeg" },
            { ".svg", "image/svg+xml" },
            { ".ico", "image/x-icon" },
        };

        public static async Task ServeStaticFile(HttpListenerContext ctx, string requestPath)
        {
            string fileName = requestPath.TrimStart('/');
            if (string.IsNullOrEmpty(fileName) || fileName.Equals("index.html", StringComparison.OrdinalIgnoreCase))
            {
                fileName = "index.html";
            }

            string ext = Path.GetExtension(fileName);
            string contentType = MimeTypes.TryGetValue(ext, out var mime) ? mime : "application/octet-stream";

            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "AutoExile2", "WebServer", "wwwroot", fileName);

            byte[]? contentBytes = null;
            if (File.Exists(localPath))
            {
                contentBytes = await File.ReadAllBytesAsync(localPath);
            }
            else
            {
                string resName = $"webui.{fileName}";
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream != null)
                {
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms);
                    contentBytes = ms.ToArray();
                }
            }

            if (contentBytes != null)
            {
                ctx.Response.ContentType = contentType;
                ctx.Response.ContentLength64 = contentBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(contentBytes);
                ctx.Response.Close();
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }

        public static Task ServeDashboardHtml(HttpListenerContext ctx)
        {
            return ServeStaticFile(ctx, "index.html");
        }

        public static async Task ServeDumpsList(HttpListenerContext ctx, Func<object, Task> sendJson)
        {
            string dumpDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dumps");
            var list = new List<object>();
            if (Directory.Exists(dumpDir))
            {
                var files = Directory.GetFiles(dumpDir, "*.*")
                    .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.CreationTime)
                    .Take(40);

                foreach (var fi in files)
                {
                    list.Add(new
                    {
                        name = fi.Name,
                        sizeKb = fi.Length / 1024,
                        created = fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        isImage = fi.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase),
                    });
                }
            }

            await sendJson(new { success = true, dumps = list });
        }

        public static async Task ServeDumpFile(HttpListenerContext ctx, string fileName)
        {
            string cleanName = Path.GetFileName(fileName);
            string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dumps", cleanName);
            if (File.Exists(filePath))
            {
                byte[] bytes = await File.ReadAllBytesAsync(filePath);
                ctx.Response.ContentType = cleanName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? "image/png"
                    : "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
                return;
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
    }
}
