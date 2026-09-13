// <copyright file="WorldAreaTags.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Utils
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text.Json;

    /// <summary>
    ///     Classification metadata for one WorldArea MapId, loaded from the
    ///     embedded <c>Data/WorldAreaTags.json</c> resource.
    /// </summary>
    public readonly struct WorldAreaMeta
    {
        internal WorldAreaMeta(string type, IReadOnlyList<string> tags)
        {
            this.Type = type ?? "normal";
            this.Tags = new ReadOnlyCollection<string>(new List<string>(tags ?? []));
        }

        /// <summary>"normal" or "unique".</summary>
        public string Type { get; }

        /// <summary>Feature tags, e.g. "lineage", "arbiter".</summary>
        public IReadOnlyList<string> Tags { get; }
    }

    /// <summary>
    ///     Maps internal WorldArea MapIds to classification metadata (type, tags).
    ///     <para>
    ///     The mapping is loaded once from the embedded <c>Data/WorldAreaTags.json</c>
    ///     resource. To refresh, regenerate the JSON file and rebuild.
    ///     </para>
    /// </summary>
    public static class WorldAreaTags
    {
        private const string ResourceSuffix = "Data.WorldAreaTags.json";

        private static readonly Lazy<IReadOnlyDictionary<string, WorldAreaMeta>> Map = new(Load);

        /// <summary>
        ///     Gets the metadata for an internal MapId, or null when unmapped.
        /// </summary>
        public static WorldAreaMeta? GetMeta(string internalId)
        {
            if (string.IsNullOrEmpty(internalId))
            {
                return null;
            }

            return Map.Value.TryGetValue(internalId, out var meta) ? meta : null;
        }

        private static IReadOnlyDictionary<string, WorldAreaMeta> Load()
        {
            var result = new Dictionary<string, WorldAreaMeta>(StringComparer.Ordinal);
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(ResourceSuffix, StringComparison.Ordinal));
                if (resourceName == null)
                {
                    Console.WriteLine($"[WorldAreaTags] Embedded resource '{ResourceSuffix}' not found.");
                    return result;
                }

                using var stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                {
                    return result;
                }

                using var document = JsonDocument.Parse(stream);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    return result;
                }

                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    var metadata = entry.Value;
                    var type = metadata.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString() ?? "normal"
                        : "normal";
                    var tags = new List<string>();
                    if (metadata.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var tag in tagsElement.EnumerateArray())
                        {
                            var value = tag.GetString();
                            if (!string.IsNullOrEmpty(value))
                            {
                                tags.Add(value);
                            }
                        }
                    }

                    result[entry.Name] = new WorldAreaMeta(type, tags);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WorldAreaTags] Failed to load mapping: {ex}");
            }

            return result;
        }
    }
}
