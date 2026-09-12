// <copyright file="DotNetRuntimeDetector.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Launcher
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;

    /// <summary>
    /// Detects the x64 .NET runtime required by the framework-dependent TEHhub executable.
    /// The launcher itself is published as self-contained so this check can run on a clean machine.
    /// </summary>
    internal static class DotNetRuntimeDetector
    {
        internal const string DownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0/runtime";

        internal static bool IsNet10Installed()
        {
            foreach (var dotnetPath in GetDotNetCandidates())
            {
                if (HasNet10Runtime(dotnetPath))
                {
                    return true;
                }
            }

            return false;
        }

        internal static void ShowInstallPrompt()
        {
            var previousColor = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Microsoft .NET 10 Runtime for Windows x64 is required to run TEHhub.");
            Console.ForegroundColor = previousColor;
            Console.WriteLine($"Download: {DownloadUrl}");
            Console.Write("Press D to open the download page, or any other key to exit: ");

            if (Console.ReadKey(intercept: true).Key == ConsoleKey.D)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(DownloadUrl) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to open the download page: {ex.Message}");
                    Console.WriteLine("Copy the download URL above into your browser.");
                    Console.ReadKey(intercept: true);
                }
            }

            Console.WriteLine();
        }

        private static IEnumerable<string> GetDotNetCandidates()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(dotnetRoot))
            {
                var configuredPath = Path.Combine(dotnetRoot, "dotnet.exe");
                if (seen.Add(configuredPath))
                {
                    yield return configuredPath;
                }
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                var standardPath = Path.Combine(programFiles, "dotnet", "dotnet.exe");
                if (seen.Add(standardPath))
                {
                    yield return standardPath;
                }
            }

            if (seen.Add("dotnet"))
            {
                yield return "dotnet";
            }
        }

        private static bool HasNet10Runtime(string dotnetPath)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = dotnetPath,
                    Arguments = "--list-runtimes",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });

                if (process == null)
                {
                    return false;
                }

                if (!process.WaitForExit(5000))
                {
                    process.Kill(entireProcessTree: true);
                    return false;
                }

                var output = process.StandardOutput.ReadToEnd();
                return process.ExitCode == 0 &&
                    output.Contains("Microsoft.NETCore.App 10.", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }
}
