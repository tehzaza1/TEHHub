// <copyright file="TEHhubFinder.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Launcher
{
    using System;
    using System.IO;

    public static class TEHhubFinder
    {
        private const string TEHhubFileName = "TEHhub.exe";

        /// <summary>
        ///     Finds the TEHhub on the file system.
        /// </summary>
        /// <param name="gameHelperDir">directory in which game helper is located</param>
        /// <param name="gameHelperLoc">path to game helper exe file</param>
        /// <returns></returns>
        public static bool TryFindTEHhubExe(out string gameHelperDir, out string gameHelperLoc)
        {
            gameHelperDir = AppContext.BaseDirectory;
            gameHelperLoc = Path.Join(gameHelperDir, TEHhubFileName);
            if (!new FileInfo(gameHelperLoc).Exists)
            {
                Console.WriteLine($"TEHhub.exe not found in {gameHelperDir} directory.");
                Console.Write("Provide TEHhub.exe path:");
                var path = (Console.ReadLine() ?? string.Empty).Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(path))
                {
                    return false;
                }

                try
                {
                    if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                    {
                        gameHelperDir = Path.GetFullPath(path);
                    }
                    else
                    {
                        gameHelperDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
                {
                    Console.WriteLine($"The provided path is invalid or inaccessible: {ex.Message}");
                    return false;
                }

                gameHelperLoc = Path.Join(gameHelperDir, TEHhubFileName);
                if (!new FileInfo(gameHelperLoc).Exists)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
