// <copyright file="SettingsHandler.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.WebServer
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using ClickableTransparentOverlay.Win32;
    using System.Text.Json.Nodes;

    /// <summary>
    /// Handles parsing and updating of AutoExile2Settings from JSON requests.
    /// </summary>
    public static class SettingsHandler
    {
        public static void UpdateSettingsFromJson(AutoExile2Settings settings, string body)
        {
            var jObj = JsonNode.Parse(body)?.AsObject() ?? throw new FormatException("Settings payload must be a JSON object.");
            foreach (var prop in jObj)
            {
                string name = prop.Key;
                if (prop.Value == null)
                {
                    ClearNullableWaystoneThreshold(settings, name);
                    continue;
                }

                if (prop.Value is not JsonNode val)
                {
                    continue;
                }
                switch (name.ToLowerInvariant())
                {
                    case "isrunning":
                        settings.IsRunning = val.Value<bool>();
                        break;
                    case "mode":
                        if (Enum.TryParse<AutoExileMode>(val.ToString(), true, out var parsedMode))
                            settings.Mode = parsedMode;
                        break;
                    case "togglekey":
                        settings.ToggleKey = ParseVk(val.ToString());
                        break;
                    case "dumpkey":
                        settings.DumpKey = ParseVk(val.ToString());
                        break;
                    case "portalkey":
                        settings.PortalKey = ParseVk(val.ToString());
                        break;
                    case "waystonetab":
                        settings.WaystoneTab = NormalizeStashTabName(val.ToString());
                        break;
                    case "currencytab":
                        settings.CurrencyTab = NormalizeStashTabName(val.ToString());
                        break;
                    case "mintier":
                        settings.MinTier = Math.Clamp(val.Value<int>(), 1, 16);
                        break;
                    case "maxtier":
                        settings.MaxTier = Math.Clamp(val.Value<int>(), 1, 16);
                        break;
                    case "enablewaystonebatch":
                        settings.EnableWaystoneBatch = val.Value<bool>();
                        break;
                    case "waystonebatchsize":
                        settings.WaystoneBatchSize = Math.Clamp(val.Value<int>(), 5, 10);
                        break;
                    case "minwaystoneitemrarity":
                        settings.MinWaystoneItemRarity = ParseOptionalNonNegativeInt(val);
                        break;
                    case "minwaystonepacksize":
                        settings.MinWaystonePackSize = ParseOptionalNonNegativeInt(val);
                        break;
                    case "minwaystonemonsterrarity":
                        settings.MinWaystoneMonsterRarity = ParseOptionalNonNegativeInt(val);
                        break;
                    case "minwaystonemonstereffectiveness":
                        settings.MinWaystoneMonsterEffectiveness = ParseOptionalNonNegativeInt(val);
                        break;
                    case "minwaystonedropchance":
                        settings.MinWaystoneDropChance = ParseOptionalNonNegativeInt(val);
                        break;
                    case "maxwaystonemods":
                        settings.MaxWaystoneMods = Math.Clamp(val.Value<int>(), 4, 6);
                        break;
                    case "blockedwaystonemods":
                        settings.BlockedWaystoneMods = WaystoneFilter.NormalizeBlockedMods(
                            val is JsonArray array
                                ? array.Select(entry => entry?.ToString() ?? string.Empty)
                                : Array.Empty<string>());
                        break;
                    case "dumptab":
                        settings.DumpTab = NormalizeStashTabName(val.ToString());
                        break;
                    case "moveup":
                        settings.MoveUp = ParseVk(val.ToString());
                        break;
                    case "movedown":
                        settings.MoveDown = ParseVk(val.ToString());
                        break;
                    case "moveleft":
                        settings.MoveLeft = ParseVk(val.ToString());
                        break;
                    case "moveright":
                        settings.MoveRight = ParseVk(val.ToString());
                        break;
                    case "usesprint":
                        settings.UseSprint = val.Value<bool>();
                        break;
                    case "sprintkey":
                        settings.SprintKey = ParseVk(val.ToString());
                        break;
                    case "sprintmindistance":
                        settings.SprintMinDistance = val.Value<float>();
                        break;
                    case "primaryattacktype":
                        settings.PrimaryAttackType = ParseAttackType(val.ToString());
                        break;
                    case "primaryattackkey":
                        settings.PrimaryAttackKey = ParseVk(val.ToString());
                        break;
                    case "attackholddurationms":
                        settings.AttackHoldDurationMs = val.Value<int>();
                        break;
                    case "attackcooldownms":
                        settings.AttackCooldownMs = val.Value<int>();
                        break;
                    case "combatrange":
                        settings.CombatRange = val.Value<float>();
                        break;
                    case "combatstyle":
                        if (Enum.TryParse<CombatStyle>(val.ToString(), true, out var style))
                        {
                            settings.CombatStyle = style;
                        }
                        break;
                    case "fightrange":
                        settings.FightRange = val.Value<float>();
                        break;
                    case "minpackdensity":
                        settings.MinPackDensity = val.Value<int>();
                        break;
                    case "usesecondaryattack":
                        settings.UseSecondaryAttack = val.Value<bool>();
                        break;
                    case "secondaryattacktype":
                        settings.SecondaryAttackType = ParseAttackType(val.ToString());
                        break;
                    case "secondaryattackkey":
                        settings.SecondaryAttackKey = ParseVk(val.ToString());
                        break;
                    case "autolifeflask":
                        settings.AutoLifeFlask = val.Value<bool>();
                        break;
                    case "lifeflaskkey":
                        settings.LifeFlaskKey = ParseVk(val.ToString());
                        break;
                    case "lifeflaskthresholdpercent":
                        settings.LifeFlaskThresholdPercent = val.Value<float>();
                        break;
                    case "lifeflaskcooldownms":
                        settings.LifeFlaskCooldownMs = val.Value<int>();
                        break;
                    case "automanaflask":
                        settings.AutoManaFlask = val.Value<bool>();
                        break;
                    case "manaflaskkey":
                        settings.ManaFlaskKey = ParseVk(val.ToString());
                        break;
                    case "manaflaskthresholdpercent":
                        settings.ManaFlaskThresholdPercent = val.Value<float>();
                        break;
                    case "manaflaskcooldownms":
                        settings.ManaFlaskCooldownMs = val.Value<int>();
                        break;
                    case "checkflaskactiveeffect":
                        settings.CheckFlaskActiveEffect = val.Value<bool>();
                        break;
                    case "checkflaskcharges":
                        settings.CheckFlaskCharges = val.Value<bool>();
                        break;
                    case "skills":
                        try
                        {
                            settings.Skills = ParseSkillSlots(val);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SettingsHandler] Failed to parse skills: {ex.Message}");
                        }
                        break;
                    case "p1skills":
                        try
                        {
                            settings.P1Skills = ParseSkillSlots(val);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SettingsHandler] Failed to parse p1skills: {ex.Message}");
                        }
                        break;
                    case "p1buffs":
                        if (!jObj.ContainsKey("p1skills") && !jObj.ContainsKey("P1Skills"))
                        {
                            try
                            {
                                settings.P1Skills = ParseSkillSlots(val);
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[SettingsHandler] Failed to parse p1buffs: {ex.Message}");
                            }
                        }
                        break;
                    case "p2skills":
                        try
                        {
                            settings.P2Skills = ParseSkillSlots(val);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[SettingsHandler] Failed to parse p2skills: {ex.Message}");
                        }
                        break;
                    case "followercharactername":
                    case "followerleadername":
                        settings.FollowerCharacterName = val.ToString();
                        break;
                    case "followdistance":
                        settings.FollowDistance = val.Value<float>();
                        break;
                    case "followstopdistance":
                        settings.FollowStopDistance = val.Value<float>();
                        break;
                    case "followerenablecombat":
                        settings.FollowerEnableCombat = val.Value<bool>();
                        break;
                    case "coopphysicalpadindex":
                        settings.CoopPhysicalPadIndex = val.Value<int>();
                        break;
                    case "p1autolifeflask":
                        settings.P1AutoLifeFlask = val.Value<bool>();
                        break;
                    case "p1lifeflaskthresholdpercent":
                        settings.P1LifeFlaskThresholdPercent = val.Value<float>();
                        break;
                    case "p1automanaflask":
                        settings.P1AutoManaFlask = val.Value<bool>();
                        break;
                    case "p1manaflaskthresholdpercent":
                        settings.P1ManaFlaskThresholdPercent = val.Value<float>();
                        break;
                    case "coopfollowdistance":
                        settings.CoopFollowDistance = val.Value<float>();
                        break;
                    case "followerposition":
                        if (Enum.TryParse<CoopFollowerPosition>(val.ToString(), true, out var fPos))
                        {
                            settings.FollowerPosition = fPos;
                        }
                        break;
                    case "coopstopdistance":
                        settings.CoopStopDistance = val.Value<float>();
                        break;
                    case "coopsprintdistance":
                        settings.CoopSprintDistance = val.Value<float>();
                        break;
                    case "positionrelativetoleaderrotation":
                        settings.PositionRelativeToLeaderRotation = val.Value<bool>();
                        break;
                    case "reducebodyblocking":
                        settings.ReduceBodyBlocking = val.Value<bool>();
                        break;
                    case "bodyblockingrepulsion":
                        settings.BodyBlockingRepulsion = val.Value<float>();
                        break;
                    case "headingsmoothing":
                        settings.HeadingSmoothing = val.Value<int>();
                        break;
                    case "p2autolifeflask":
                        settings.P2AutoLifeFlask = val.Value<bool>();
                        break;
                    case "p2lifeflaskthresholdpercent":
                        settings.P2LifeFlaskThresholdPercent = val.Value<float>();
                        break;
                    case "p2automanaflask":
                        settings.P2AutoManaFlask = val.Value<bool>();
                        break;
                    case "p2manaflaskthresholdpercent":
                        settings.P2ManaFlaskThresholdPercent = val.Value<float>();
                        break;
                    case "p2enablecombat":
                        settings.P2EnableCombat = val.Value<bool>();
                        break;
                    case "showoverlay":
                        settings.ShowOverlay = val.Value<bool>();
                        break;
                    case "showdistancecircles":
                        settings.ShowDistanceCircles = val.Value<bool>();
                        break;
                    case "webserverport":
                        settings.WebServerPort = val.Value<int>();
                        break;
                    case "webservernetworkaccess":
                        settings.WebServerNetworkAccess = val.Value<bool>();
                        break;
                    case "enableactionlogging":
                        settings.EnableActionLogging = val.Value<bool>();
                        break;
                }
            }

            settings.MinTier = Math.Clamp(settings.MinTier, 1, 16);
            settings.MaxTier = Math.Clamp(settings.MaxTier, 1, 16);
            settings.WaystoneBatchSize = Math.Clamp(settings.WaystoneBatchSize, 5, 10);
            if (settings.MinTier > settings.MaxTier)
            {
                (settings.MinTier, settings.MaxTier) = (settings.MaxTier, settings.MinTier);
            }

            settings.MaxWaystoneMods = Math.Clamp(settings.MaxWaystoneMods, 4, 6);
            settings.BlockedWaystoneMods = WaystoneFilter.NormalizeBlockedMods(settings.BlockedWaystoneMods);
        }

        private static void ClearNullableWaystoneThreshold(AutoExile2Settings settings, string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "minwaystoneitemrarity":
                    settings.MinWaystoneItemRarity = null;
                    break;
                case "minwaystonepacksize":
                    settings.MinWaystonePackSize = null;
                    break;
                case "minwaystonemonsterrarity":
                    settings.MinWaystoneMonsterRarity = null;
                    break;
                case "minwaystonemonstereffectiveness":
                    settings.MinWaystoneMonsterEffectiveness = null;
                    break;
                case "minwaystonedropchance":
                    settings.MinWaystoneDropChance = null;
                    break;
            }
        }

        private static int? ParseOptionalNonNegativeInt(JsonNode value)
        {
            var text = value.ToString().Trim();
            return int.TryParse(text, out var parsed) ? Math.Max(0, parsed) : null;
        }

        private static bool SafeBool(JsonNode? token, bool defaultVal = false)
        {
            if (token == null) return defaultVal;
            try { return token.Value<bool>(); } catch { }
            if (bool.TryParse(token.ToString(), out var b)) return b;
            return defaultVal;
        }

        private static int SafeInt(JsonNode? token, int defaultVal = 0)
        {
            if (token == null) return defaultVal;
            try { return token.Value<int>(); } catch { }
            if (int.TryParse(token.ToString(), out var i)) return i;
            if (double.TryParse(token.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)) return (int)Math.Round(d);
            return defaultVal;
        }

        private static float SafeFloat(JsonNode? token, float defaultVal = 0f)
        {
            if (token == null) return defaultVal;
            try { return token.Value<float>(); } catch { }
            if (float.TryParse(token.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f)) return f;
            return defaultVal;
        }

        private static string SafeString(JsonNode? token, string defaultVal = "")
        {
            if (token == null) return defaultVal;
            return token.ToString();
        }

        private static string NormalizeStashTabName(string value)
        {
            const int maxLength = 80;
            var trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        public static List<SkillSlotConfig> ParseSkillSlots(JsonNode? val)
        {
            var skills = new List<SkillSlotConfig>();
            if (val is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item == null) continue;
                    try
                    {
                        var slot = new SkillSlotConfig();
                        var enabledToken = item["Enabled"] ?? item["enabled"];
                        if (enabledToken != null) slot.Enabled = SafeBool(enabledToken, true);

                        var nameToken = item["Name"] ?? item["name"];
                        if (nameToken != null) slot.Name = SafeString(nameToken, "Skill Slot");

                        var assignedToken = item["AssignedSkillName"] ?? item["assignedSkillName"];
                        if (assignedToken != null) slot.AssignedSkillName = SafeString(assignedToken, "");

                        var catToken = item["Category"] ?? item["category"];
                        if (catToken != null) slot.Category = SafeString(catToken, "Attack");

                        var roleToken = item["Role"] ?? item["role"];
                        if (roleToken != null) slot.Role = ParseSkillRole(roleToken.ToString());

                        var inputToken = item["InputType"] ?? item["inputType"];
                        if (inputToken != null) slot.InputType = ParseAttackType(inputToken.ToString());

                        var keyToken = item["Key"] ?? item["key"];
                        if (keyToken != null) slot.Key = ParseVk(keyToken.ToString());

                        var padBtnToken = item["GamepadButton"] ?? item["gamepadButton"] ?? item["Button"] ?? item["button"];
                        if (padBtnToken != null) slot.GamepadButton = ParseCoopPadButton(padBtnToken.ToString());

                        var priToken = item["Priority"] ?? item["priority"];
                        if (priToken != null) slot.Priority = SafeInt(priToken, 5);

                        var targetToken = item["TargetFilter"] ?? item["targetFilter"];
                        if (targetToken != null) slot.TargetFilter = ParseSkillTargetFilter(targetToken.ToString());

                        var intervalToken = item["MinCastIntervalMs"] ?? item["minCastIntervalMs"] ?? item["CooldownMs"] ?? item["cooldownMs"];
                        if (intervalToken != null) slot.MinCastIntervalMs = SafeInt(intervalToken, 250);

                        var holdToken = item["HoldDurationMs"] ?? item["holdDurationMs"] ?? item["HoldMs"] ?? item["holdMs"];
                        if (holdToken != null) slot.HoldDurationMs = SafeInt(holdToken, 150);

                        var rangeToken = item["MaxTargetRange"] ?? item["maxTargetRange"];
                        if (rangeToken != null) slot.MaxTargetRange = SafeFloat(rangeToken, 0f);

                        var enemiesToken = item["MinNearbyEnemies"] ?? item["minNearbyEnemies"];
                        if (enemiesToken != null) slot.MinNearbyEnemies = SafeInt(enemiesToken, 0);

                        var lowHpToken = item["OnlyOnLowHp"] ?? item["onlyOnLowHp"];
                        if (lowHpToken != null) slot.OnlyOnLowHp = SafeBool(lowHpToken, false);

                        var vitalCondToken = item["VitalCondition"] ?? item["vitalCondition"];
                        if (vitalCondToken != null) slot.VitalCondition = ParseVitalCondition(vitalCondToken.ToString());

                        var hpThreshToken = item["LowHpThresholdPercent"] ?? item["lowHpThresholdPercent"];
                        if (hpThreshToken != null) slot.LowHpThresholdPercent = SafeFloat(hpThreshToken, 60f);

                        var manaToken = item["MinManaPercent"] ?? item["minManaPercent"];
                        if (manaToken != null) slot.MinManaPercent = SafeFloat(manaToken, 0f);

                        var channelToken = item["IsChannel"] ?? item["isChannel"];
                        if (channelToken != null) slot.IsChannel = SafeBool(channelToken, false);

                        var buffMissingToken = item["OnlyWhenBuffMissing"] ?? item["onlyWhenBuffMissing"];
                        if (buffMissingToken != null) slot.OnlyWhenBuffMissing = SafeBool(buffMissingToken, false);

                        var buffNameToken = item["BuffDebuffName"] ?? item["buffDebuffName"] ?? item["BuffName"] ?? item["buffName"];
                        if (buffNameToken != null) slot.BuffDebuffName = SafeString(buffNameToken, "");

                        var totemToken = item["MaxTotemCount"] ?? item["maxTotemCount"];
                        if (totemToken != null) slot.MaxTotemCount = SafeInt(totemToken, 1);

                        var cullerAimToken = item["CullerAimDistance"] ?? item["cullerAimDistance"];
                        if (cullerAimToken != null) slot.CullerAimDistance = SafeFloat(cullerAimToken, 75f);

                        var cullerStartToken = item["CullerStartAttackDistance"] ?? item["cullerStartAttackDistance"];
                        if (cullerStartToken != null) slot.CullerStartAttackDistance = SafeFloat(cullerStartToken, 35f);

                        var cullerReqToken = item["CullerRequireMonsters"] ?? item["cullerRequireMonsters"];
                        if (cullerReqToken != null) slot.CullerRequireMonsters = SafeBool(cullerReqToken, false);

                        skills.Add(slot);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[SettingsHandler] Error parsing individual skill slot: {ex.Message}");
                    }
                }
            }

            return skills;
        }

        public static SkillRole ParseSkillRole(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return SkillRole.EnemyTargeted;
            if (Enum.TryParse<SkillRole>(s.Trim(), true, out var role)) return role;
            if (int.TryParse(s, out int intVal) && Enum.IsDefined(typeof(SkillRole), intVal)) return (SkillRole)intVal;
            return SkillRole.EnemyTargeted;
        }

        public static SkillTargetFilter ParseSkillTargetFilter(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return SkillTargetFilter.Any;
            if (Enum.TryParse<SkillTargetFilter>(s.Trim(), true, out var filter)) return filter;
            if (int.TryParse(s, out int intVal) && Enum.IsDefined(typeof(SkillTargetFilter), intVal)) return (SkillTargetFilter)intVal;
            return SkillTargetFilter.Any;
        }

        public static VK ParseVk(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return VK.INSERT;
            s = s.Trim().ToUpperInvariant();
            if (s.Length == 1 && char.IsLetterOrDigit(s[0]))
            {
                if (Enum.TryParse<VK>($"KEY_{s}", true, out var parsedLetter)) return parsedLetter;
            }
            if (Enum.TryParse<VK>(s, true, out var direct)) return direct;
            if (Enum.TryParse<VK>($"KEY_{s}", true, out var prefixed)) return prefixed;
            if (int.TryParse(s, out int intVal) && Enum.IsDefined(typeof(VK), intVal)) return (VK)intVal;
            return VK.INSERT;
        }

        public static AttackInputType ParseAttackType(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return AttackInputType.MouseRight;
            s = s.Trim();
            if (Enum.TryParse<AttackInputType>(s, true, out var direct)) return direct;
            if (int.TryParse(s, out int intVal) && Enum.IsDefined(typeof(AttackInputType), intVal)) return (AttackInputType)intVal;
            return AttackInputType.MouseRight;
        }

        public static AutoExile2.Systems.CoopPadButton ParseCoopPadButton(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return AutoExile2.Systems.CoopPadButton.RightShoulder;
            s = s.Trim();
            if (Enum.TryParse<AutoExile2.Systems.CoopPadButton>(s, true, out var parsed)) return parsed;
            if (int.TryParse(s, out int intVal) && Enum.IsDefined(typeof(AutoExile2.Systems.CoopPadButton), intVal)) return (AutoExile2.Systems.CoopPadButton)intVal;

            string lower = s.ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");
            if (lower == "up" || lower == "dup" || lower == "dpadup" || lower == "arrowup") return AutoExile2.Systems.CoopPadButton.DPadUp;
            if (lower == "down" || lower == "ddown" || lower == "dpaddown" || lower == "arrowdown") return AutoExile2.Systems.CoopPadButton.DPadDown;
            if (lower == "left" || lower == "dleft" || lower == "dpadleft" || lower == "arrowleft") return AutoExile2.Systems.CoopPadButton.DPadLeft;
            if (lower == "right" || lower == "dright" || lower == "dpadright" || lower == "arrowright") return AutoExile2.Systems.CoopPadButton.DPadRight;

            return AutoExile2.Systems.CoopPadButton.RightShoulder;
        }

        public static VitalConditionType ParseVitalCondition(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return VitalConditionType.CombinedHpEs;
            s = s.Trim();
            if (int.TryParse(s, out int intVal))
            {
                if (Enum.IsDefined(typeof(VitalConditionType), intVal))
                    return (VitalConditionType)intVal;
            }
            if (Enum.TryParse<VitalConditionType>(s, true, out var parsed))
                return parsed;

            if (s.Equals("hp", StringComparison.OrdinalIgnoreCase) || s.Equals("hponly", StringComparison.OrdinalIgnoreCase)) return VitalConditionType.HpOnly;
            if (s.Equals("es", StringComparison.OrdinalIgnoreCase) || s.Equals("esonly", StringComparison.OrdinalIgnoreCase)) return VitalConditionType.EsOnly;
            if (s.Equals("combined", StringComparison.OrdinalIgnoreCase) || s.Equals("hpes", StringComparison.OrdinalIgnoreCase) || s.Equals("hp+es", StringComparison.OrdinalIgnoreCase) || s.Equals("combinedhpes", StringComparison.OrdinalIgnoreCase)) return VitalConditionType.CombinedHpEs;

            return VitalConditionType.CombinedHpEs;
        }
    }
}
