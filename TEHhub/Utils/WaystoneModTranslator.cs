// <copyright file="WaystoneModTranslator.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Utils
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json;

    /// <summary>
    ///     Translates PoE2 Waystone explicit mod identifiers into human-readable text.
    /// </summary>
    public static class WaystoneModTranslator
    {
        private static readonly Dictionary<string, (string Tag, string Template)> ModMap = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object LockObj = new();
        private static volatile bool isInitialized;

        /// <summary>
        ///     Maps the actual JSON <c>family</c> values to human-readable templates.
        ///     Keys must match exactly the first element of the "family" array in Waystones.json.
        /// </summary>
        private static readonly Dictionary<string, string> FamilyTemplates = new(StringComparer.OrdinalIgnoreCase)
        {
            // Prefix families whose text field is a raw stat key string
            ["MapMonsterFast"] = "Monsters have {0}% increased Attack, Cast and Movement Speed",
            ["MapMonsterCriticalStrikesAndDamage"] = "Monsters have {0}% increased Critical Hit Chance / Monsters have {1}% Critical Damage Bonus",

            // Suffix families whose text field is a raw stat key string
            ["MapBurningGround"] = "Area has patches of Burning Ground",
            ["MapChilledGround"] = "Area has patches of Chilled Ground",
            ["MapShockedGround"] = "Area has patches of Shocked Ground",
            ["MapMonsterElementalAilmentChance"] = "Monsters have {0}% chance to inflict Elemental Ailments on Hit",
            ["MapMonstersStunAndAilmentThreshold"] = "Monsters have {0}% increased Stun Threshold / Monsters have {1}% increased Ailment Threshold",
        };

        /// <summary>
        ///     Fallback: maps mod-ID prefixes to templates when the family name is not in <see cref="FamilyTemplates"/>.
        ///     Keys must be a prefix of the tier ID (e.g. "MapMonsterSpeedIncrease" matches "MapMonsterSpeedIncrease4").
        /// </summary>
        private static readonly Dictionary<string, string> IdPrefixTemplates = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MapMonsterSpeedIncrease"] = "Monsters have {0}% increased Attack, Cast and Movement Speed",
            ["MapSpreadBurningGround"] = "Area has patches of Burning Ground",
            ["MapSpreadChilledGround"] = "Area has patches of Chilled Ground",
            ["MapSpreadShockedGround"] = "Area has patches of Shocked Ground",
            ["MapMonsterElementAilmentChance"] = "Monsters have {0}% chance to inflict Elemental Ailments on Hit",
        };

        /// <summary>
        ///     Translates a modifier ID and values into a clean human-readable string (e.g. "[P] Monsters deal 17% of Damage as Extra Chaos").
        /// </summary>
        /// <param name="modId">Internal modifier identifier (e.g. MapMonsterDamageAsChaos3).</param>
        /// <param name="value0">First modifier value.</param>
        /// <param name="value1">Second modifier value.</param>
        /// <returns>Human-readable string, or modId if not recognized.</returns>
        public static string Translate(string modId, float value0, float value1)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return string.Empty;
            }

            EnsureInitialized();

            if (!ModMap.TryGetValue(modId, out var entry))
            {
                return modId;
            }

            var (tag, template) = entry;
            string body;

            if (template.Contains("{0}", StringComparison.Ordinal) ||
                template.Contains("{1}", StringComparison.Ordinal))
            {
                var useAbsoluteValue =
                    template.Contains("less", StringComparison.OrdinalIgnoreCase) ||
                    template.Contains("reduced", StringComparison.OrdinalIgnoreCase);
                body = template
                    .Replace("{0}", FormatValue(value0, useAbsoluteValue), StringComparison.Ordinal)
                    .Replace("{1}", FormatValue(value1, useAbsoluteValue), StringComparison.Ordinal);
            }
            else
            {
                body = template;
            }

            return $"[{tag}] {body}";
        }

        private static string FormatValue(float value, bool useAbsoluteValue)
        {
            if (float.IsNaN(value))
            {
                return "?";
            }

            return ((int)Math.Round(useAbsoluteValue ? Math.Abs(value) : value)).ToString();
        }

        /// <summary>
        ///     Checks if a given mod ID is a recognized Waystone modifier.
        /// </summary>
        /// <param name="modId">Internal modifier identifier.</param>
        /// <returns>True if the modifier is present in the Waystones dataset.</returns>
        public static bool IsWaystoneMod(string modId)
        {
            if (string.IsNullOrEmpty(modId))
            {
                return false;
            }

            EnsureInitialized();
            return ModMap.ContainsKey(modId);
        }

        private static void EnsureInitialized()
        {
            if (isInitialized)
            {
                return;
            }

            lock (LockObj)
            {
                if (isInitialized)
                {
                    return;
                }

                LoadMappings();
                isInitialized = true;
            }
        }

        private static void LoadMappings()
        {
            var candidates = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "resources", "mod_categories", "Waystones.json"),
                Path.Combine(Directory.GetCurrentDirectory(), "resources", "mod_categories", "Waystones.json"),
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "resources", "mod_categories", "Waystones.json"),
            };

            string? targetFile = null;
            foreach (var c in candidates)
            {
                if (File.Exists(c))
                {
                    targetFile = c;
                    break;
                }
            }

            if (targetFile == null)
            {
                Console.WriteLine("[WaystoneModTranslator] Waystones.json not found.");
                return;
            }

            try
            {
                using var stream = File.OpenRead(targetFile);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                void ProcessModList(JsonElement array, string affixType)
                {
                    if (array.ValueKind != JsonValueKind.Array)
                    {
                        return;
                    }

                    var tagPrefix = affixType.Equals("prefix", StringComparison.OrdinalIgnoreCase) ? "P" : "S";

                    foreach (var modGroup in array.EnumerateArray())
                    {
                        var rawText = modGroup.TryGetProperty("text", out var tProp) ? tProp.GetString() ?? string.Empty : string.Empty;
                        var firstPart = rawText.Split(" / ")[0].Trim();

                        string? family = null;
                        if (modGroup.TryGetProperty("family", out var fProp) && fProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var fItem in fProp.EnumerateArray())
                            {
                                family = fItem.GetString();
                                break;
                            }
                        }

                        if (!modGroup.TryGetProperty("tiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var tierEl in tiers.EnumerateArray())
                        {
                            if (!tierEl.TryGetProperty("id", out var idProp))
                            {
                                continue;
                            }

                            var id = idProp.GetString();
                            if (string.IsNullOrEmpty(id))
                            {
                                continue;
                            }

                            string template;

                            // 1) Match by the actual JSON family name
                            if (family != null && FamilyTemplates.TryGetValue(family, out var familyTpl))
                            {
                                template = familyTpl;
                            }
                            else
                            {
                                // 2) Match by mod-ID prefix as fallback
                                var matchedPrefix = false;
                                template = firstPart;
                                foreach (var kv in IdPrefixTemplates)
                                {
                                    if (id.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                                    {
                                        template = kv.Value;
                                        matchedPrefix = true;
                                        break;
                                    }
                                }

                                // 3) Parse the raw text field normally
                                if (!matchedPrefix)
                                {
                                    template = firstPart
                                        .Replace("#%", "{0}%")
                                        .Replace("#", "{0}")
                                        .Replace("+-", "-")
                                        .Replace("++", "+");
                                }
                            }

                            ModMap[id] = (tagPrefix, template);
                        }
                    }
                }

                if (root.TryGetProperty("prefixes", out var prefixes))
                {
                    ProcessModList(prefixes, "prefix");
                }

                if (root.TryGetProperty("suffixes", out var suffixes))
                {
                    ProcessModList(suffixes, "suffix");
                }

                Console.WriteLine($"[WaystoneModTranslator] Loaded {ModMap.Count} Waystone mod mappings from {targetFile}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WaystoneModTranslator] Failed to load Waystones.json: {ex.Message}");
            }
        }
    }
}
