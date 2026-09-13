// <copyright file="JsonHelper.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Utils
{
    using System;
    using System.IO;
    using System.Text.Json.Serialization.Metadata;
    using SystemTextJson = System.Text.Json.JsonSerializer;
    using SystemTextJsonException = System.Text.Json.JsonException;

    /// <summary>
    ///     Utility functions to help read/write to Json files.
    /// </summary>
    internal static class JsonHelper
    {
        /// <summary>
        ///     Creates or loads a JSON file through supplied source-generated metadata.
        ///     This keeps the existing corruption recovery behaviour while avoiding reflection
        ///     for stable schemas that do not depend on Newtonsoft-specific features.
        /// </summary>
        /// <typeparam name="T">Class name to (de)serialize.</typeparam>
        /// <param name="file">file to load from.</param>
        /// <param name="jsonTypeInfo">source-generated type metadata.</param>
        /// <returns>class object containing the data (if data exists).</returns>
        public static T CreateOrLoadJsonFile<T>(FileInfo file, JsonTypeInfo<T> jsonTypeInfo)
            where T : new()
        {
            ArgumentNullException.ThrowIfNull(jsonTypeInfo);
            file.Refresh();
            file.Directory?.Create();
            if (file.Exists)
            {
                try
                {
                    var content = File.ReadAllText(file.FullName);
                    var loaded = SystemTextJson.Deserialize(content, jsonTypeInfo);
                    if (loaded != null)
                    {
                        return loaded;
                    }

                    Console.WriteLine($"[JsonHelper] {file.FullName} deserialized to null; falling back to defaults.");
                }
                catch (SystemTextJsonException ex)
                {
                    Console.WriteLine($"[JsonHelper] {file.FullName} is corrupt or schema-mismatched: {ex.Message}. Falling back to defaults.");
                    QuarantineCorruptFile(file);
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"[JsonHelper] {file.FullName} could not be read: {ex.Message}. Falling back to defaults.");
                }
            }

            T obj = new();
            SafeToFile(obj, file, jsonTypeInfo);
            return obj;
        }

        /// <summary>
        ///     Saves through source-generated metadata, preserving the existing atomic write.
        /// </summary>
        /// <typeparam name="T">object type.</typeparam>
        /// <param name="classObject">object to save.</param>
        /// <param name="file">file to save in.</param>
        /// <param name="jsonTypeInfo">source-generated type metadata.</param>
        public static void SafeToFile<T>(T classObject, FileInfo file, JsonTypeInfo<T> jsonTypeInfo)
        {
            ArgumentNullException.ThrowIfNull(jsonTypeInfo);
            var content = SystemTextJson.Serialize(classObject, jsonTypeInfo);
            var tempPath = file.FullName + ".tmp";
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, file.FullName, overwrite: true);
        }

        private static void QuarantineCorruptFile(FileInfo file)
        {
            try
            {
                var corruptName = $"{file.FullName}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
                File.Move(file.FullName, corruptName, overwrite: true);
                Console.WriteLine($"[JsonHelper] Renamed corrupt file to {corruptName} for inspection.");
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[JsonHelper] Failed to quarantine corrupt file: {ex.Message}");
            }
        }
    }
}
