// <copyright file="Program.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Launcher
{
    using System;
    using System.Diagnostics;
    using System.IO;
    using System.Threading.Tasks;

    public static class Program
    {
        private static async Task Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--check")
            {
                var corePath = Path.Combine(AppContext.BaseDirectory, "TEHhub.exe");
                var ready = File.Exists(corePath) && DotNetRuntimeDetector.IsNet10Installed();
                Console.WriteLine(ready ? "PASS: TEHhub executable and .NET 10 Runtime x64 are available." : "FAIL: TEHhub executable or .NET 10 Runtime x64 is missing.");
                Environment.ExitCode = ready ? 0 : 1;
                return;
            }

            if (!TEHhubFinder.TryFindTEHhubExe(out var gameHelperDir, out var gameHelperLoc))
            {
                Console.WriteLine($"TEHhub.exe was not found in {gameHelperDir}");
                Console.ReadKey();
                return;
            }

            if (!DotNetRuntimeDetector.IsNet10Installed())
            {
                DotNetRuntimeDetector.ShowInstallPrompt();
                return;
            }
            
            var updateAvailable = await AutoUpdate.CheckAndUpdateAsync(gameHelperLoc);
            if (updateAvailable)
            {
                AutoUpdate.LaunchUpdateAndExit();
                return;
            }
            
            try
            {
                Console.WriteLine("\n\nPreparing TEHhub...");
                var newName = MiscHelper.GenerateRandomString();
                TemporaryFileManager.Purge();
                //TODO: if functionality extends, should probably utilize an argument parser, but good for now
                if (!LocationValidator.IsTEHhubLocationGood(out var message))
                {
                    Console.WriteLine(message);
                }
            
                var gameHelperPath = TEHhubTransformer.TransformTEHhubExecutable(gameHelperDir, gameHelperLoc, newName);
                Console.WriteLine($"Starting TEHhub at '{gameHelperPath}'...");
                Process.Start(new ProcessStartInfo
                {
                    FileName = gameHelperPath,
                    WorkingDirectory = gameHelperDir,
                    UseShellExecute = false,
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to launch TEHhub due to: {ex}");
                Console.ReadKey();
            }
        }
    }
}
