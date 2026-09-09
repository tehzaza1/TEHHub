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
    /// Handles static file serving for the dashboard HTML and dump artifacts.
    /// </summary>
    public static class StaticFileHandler
    {
        public static async Task ServeDashboardHtml(HttpListenerContext ctx)
        {
            string htmlContent = "";
            string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins", "AutoExile2", "WebServer", "wwwroot", "index.html");

            if (File.Exists(localPath))
            {
                htmlContent = await File.ReadAllTextAsync(localPath);
            }
            else
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("webui.index.html");
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    htmlContent = await reader.ReadToEndAsync();
                }
                else
                {
                    htmlContent = "<!DOCTYPE html><html><head><title>AutoExile 2</title></head><body>Dashboard</body></html>";
                }
            }

            byte[] bytes = Encoding.UTF8.GetBytes(htmlContent);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
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
