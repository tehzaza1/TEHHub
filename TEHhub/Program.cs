// <copyright file="Program.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub
{
    using System;
    using System.IO;
    using System.Threading.Tasks;
    using Utils;

    /// <summary>
    ///     Class executed when the application starts.
    /// </summary>
    internal class Program
    {
        /// <summary>
        ///     function executed when the application starts.
        /// </summary>
        private static async Task Main()
        {
            AppDomain.CurrentDomain.UnhandledException += (sender, exceptionArgs) =>
            {
                var errorText = "Program exited with message:\n " + exceptionArgs.ExceptionObject;
                try
                {
                    var logPath = Path.Combine(AppContext.BaseDirectory, "Error.log");
                    File.AppendAllText(logPath, $"{DateTime.Now:g} {errorText}\r\n{new string('-', 30)}\r\n");
                }
                catch (Exception loggingException)
                {
                    Console.Error.WriteLine($"Unable to write Error.log: {loggingException.Message}");
                }

                // Do NOT call Environment.Exit — it skips `using` Dispose and leaks
                // the SafeMemoryHandle (audit F-061). The runtime will terminate
                // the process naturally because IsTerminating == true for unhandled
                // exceptions on the main thread.
            };

            using (Core.Overlay = new TEHhubOverlay(MiscHelper.GenerateRandomString()))
            {
                await Core.Overlay.Run();
            }
        }
    }
}
