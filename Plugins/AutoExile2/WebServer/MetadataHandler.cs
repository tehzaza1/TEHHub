// <copyright file="MetadataHandler.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.WebServer
{
    using System;
    using System.Collections.Generic;
    using AutoExile2.Modes;

    /// <summary>
    /// Provides dynamic system metadata (modes, enums, category presets, and limits)
    /// so the Web UI reads directly from C# system files rather than hardcoded HTML.
    /// </summary>
    public static class MetadataHandler
    {
        public static object GetMetadata(IEnumerable<IBotMode>? registeredModes = null)
        {
            var modesList = new List<object>();
            if (registeredModes != null)
            {
                foreach (var m in registeredModes)
                {
                    modesList.Add(new
                    {
                        id = m.ModeType.ToString(),
                        label = m.Name,
                        desc = m.Description,
                        icon = m.Icon,
                    });
                }
            }

            if (modesList.Count == 0)
            {
                modesList.Add(new { id = "MapFarm", label = "Map Farm", desc = "Auto explore, clear & loot maps", icon = "🗺️" });
                modesList.Add(new { id = "Follower", label = "Follower", desc = "Party Aurabot / Co-op support", icon = "🤝" });
                modesList.Add(new { id = "Boss", label = "Boss Encounter", desc = "Arena boss targeting & spacing", icon = "⚔️" });
                modesList.Add(new { id = "Idle", label = "Idle", desc = "Standby mode", icon = "💤" });
            }

            return new
            {
                success = true,
                modes = modesList,
                roles = new object[]
                {
                    new { id = SkillRole.EnemyTargeted.ToString(), label = "Enemy Targeted (Direct / Single)", desc = "Aim cursor directly at target monster" },
                    new { id = SkillRole.PackTargeted.ToString(), label = "Pack Targeted (AoE / Cluster)", desc = "Aim cursor at pack center" },
                    new { id = SkillRole.TotemOrMinion.ToString(), label = "Totem Deploy", desc = "Deploy a totem toward enemies; persistent PoE2 minions are not auto-resummoned" },
                    new { id = SkillRole.SelfBuffGuard.ToString(), label = "Self Buff / Guard", desc = "Cast on self without moving cursor" },
                    new { id = SkillRole.CorpseTargeted.ToString(), label = "Corpse Targeted (Unavailable)", desc = "Reserved until authoritative corpse detection/selection is implemented" },
                    new { id = SkillRole.Culler.ToString(), label = "Culler (Focused Fire ahead of Host)", desc = "Aims in front of Leader when close to host" },
                    new { id = SkillRole.Disabled.ToString(), label = "Disabled", desc = "Do not cast this skill automatically" },
                },
                targetFilters = new object[]
                {
                    new { id = SkillTargetFilter.Any.ToString(), label = "Any Hostile Monster" },
                    new { id = SkillTargetFilter.NormalOnly.ToString(), label = "Normal (White) Only" },
                    new { id = SkillTargetFilter.MagicOrAbove.ToString(), label = "Magic (Blue) or Above" },
                    new { id = SkillTargetFilter.RareOrAbove.ToString(), label = "Rare (Yellow) & Bosses Only" },
                    new { id = SkillTargetFilter.UniqueOnly.ToString(), label = "Unique (Bosses) Only" },
                },
                inputTypes = new object[]
                {
                    new { id = AttackInputType.MouseRight.ToString(), label = "Right Mouse Button (RMB)" },
                    new { id = AttackInputType.MouseLeft.ToString(), label = "Left Mouse Button (LMB)" },
                    new { id = AttackInputType.KeyboardKey.ToString(), label = "Keyboard Key" },
                    new { id = AttackInputType.MouseMiddle.ToString(), label = "Middle Mouse Button (MMB)" },
                },
                categoryPresets = new Dictionary<string, object>
                {
                    ["Attack"] = new { role = "EnemyTargeted", priority = 1, interval = 200, hold = 150, filter = "Any", lowHp = false, lowHpThresh = 60, minEnemies = 0, maxRange = 0, label = "⚔️ Attack", color = "#38bdf8", bg = "rgba(56,189,248,0.15)" },
                    ["Buff"] = new { role = "SelfBuffGuard", priority = 5, interval = 8000, hold = 100, filter = "Any", lowHp = false, lowHpThresh = 60, minEnemies = 0, maxRange = 0, onlyWhenBuffMissing = true, label = "✨ Buff", color = "#10b981", bg = "rgba(16,185,129,0.15)" },
                    ["Curse"] = new { role = "PackTargeted", priority = 4, interval = 5000, hold = 150, filter = "MagicOrAbove", lowHp = false, lowHpThresh = 60, minEnemies = 1, maxRange = 70, onlyWhenBuffMissing = true, label = "🔮 Curse / Debuff", color = "#a855f7", bg = "rgba(168,85,247,0.15)" },
                    ["Totem"] = new { role = "TotemOrMinion", priority = 6, interval = 4000, hold = 150, filter = "Any", lowHp = false, lowHpThresh = 60, minEnemies = 1, maxRange = 65, maxTotems = 1, label = "🗿 Totem", color = "#f59e0b", bg = "rgba(245,158,11,0.15)" },
                    ["Guard"] = new { role = "SelfBuffGuard", priority = 9, interval = 4000, hold = 100, filter = "Any", lowHp = true, lowHpThresh = 60, minEnemies = 0, maxRange = 0, onlyWhenBuffMissing = true, label = "🛡️ Guard", color = "#ef4444", bg = "rgba(239,68,68,0.15)" },
                    ["Warcry"] = new { role = "SelfBuffGuard", priority = 4, interval = 4000, hold = 100, filter = "Any", lowHp = false, lowHpThresh = 60, minEnemies = 2, maxRange = 0, label = "🗣️ Warcry", color = "#ec4899", bg = "rgba(236,72,153,0.15)" },
                    ["Minion"] = new { role = "Disabled", priority = 0, interval = 500, hold = 80, filter = "Any", lowHp = false, lowHpThresh = 60, minEnemies = 0, maxRange = 0, label = "🧟 Minion (No auto-resummon)", color = "#06b6d4", bg = "rgba(6,182,212,0.15)" },
                    ["Movement"] = new { role = "Disabled", priority = 0, interval = 500, hold = 80, filter = "Any", lowHp = false, lowHpThresh = 60, minEnemies = 0, maxRange = 0, label = "⚡ Movement", color = "#6366f1", bg = "rgba(99,102,241,0.15)" },
                    ["Culler"] = new { role = "Culler", priority = 2, interval = 200, hold = 120, filter = "Any", lowHp = false, lowHpThresh = 60, minEnemies = 0, maxRange = 0, cullerAimDist = 75f, cullerStartDist = 35f, cullerRequireMonsters = false, label = "🎯 Culler", color = "#f43f5e", bg = "rgba(244,63,94,0.15)" },
                    ["Custom"] = new { role = "EnemyTargeted", priority = 5, interval = 500, hold = 150, filter = "Any", lowHp = false, lowHpThresh = 60, minEnemies = 0, maxRange = 0, label = "⚙️ Custom", color = "#94a3b8", bg = "rgba(148,163,184,0.15)" },
                },
                combatStyles = new object[]
                {
                    new { id = "Melee", label = "⚔️ Melee", desc = "Close in to strike range" },
                    new { id = "Ranged", label = "🏹 Ranged", desc = "Kite & keep distance at fight range" },
                },
                limits = new
                {
                    minFightRange = 10,
                    maxFightRange = 90,
                    minCombatRange = 20,
                    maxCombatRange = 150,
                    maxTotems = 10,
                    maxSkills = 16,
                },
                keyOptions = new object[]
                {
                    new
                    {
                        group = "Movement & Common",
                        options = new object[]
                        {
                            new { value = "KEY_W", label = "W" },
                            new { value = "KEY_A", label = "A" },
                            new { value = "KEY_S", label = "S" },
                            new { value = "KEY_D", label = "D" },
                            new { value = "SPACE", label = "Space" },
                        }
                    },
                    new
                    {
                        group = "Number Keys (0 - 9)",
                        options = new object[]
                        {
                            new { value = "KEY_1", label = "1" },
                            new { value = "KEY_2", label = "2" },
                            new { value = "KEY_3", label = "3" },
                            new { value = "KEY_4", label = "4" },
                            new { value = "KEY_5", label = "5" },
                            new { value = "KEY_6", label = "6" },
                            new { value = "KEY_7", label = "7" },
                            new { value = "KEY_8", label = "8" },
                            new { value = "KEY_9", label = "9" },
                            new { value = "KEY_0", label = "0" },
                        }
                    },
                    new
                    {
                        group = "Letter Keys (A - Z)",
                        options = new object[]
                        {
                            new { value = "KEY_Q", label = "Q" },
                            new { value = "KEY_W", label = "W" },
                            new { value = "KEY_E", label = "E" },
                            new { value = "KEY_R", label = "R" },
                            new { value = "KEY_T", label = "T" },
                            new { value = "KEY_Y", label = "Y" },
                            new { value = "KEY_U", label = "U" },
                            new { value = "KEY_I", label = "I" },
                            new { value = "KEY_O", label = "O" },
                            new { value = "KEY_P", label = "P" },
                            new { value = "KEY_A", label = "A" },
                            new { value = "KEY_S", label = "S" },
                            new { value = "KEY_D", label = "D" },
                            new { value = "KEY_F", label = "F" },
                            new { value = "KEY_G", label = "G" },
                            new { value = "KEY_H", label = "H" },
                            new { value = "KEY_J", label = "J" },
                            new { value = "KEY_K", label = "K" },
                            new { value = "KEY_L", label = "L" },
                            new { value = "KEY_Z", label = "Z" },
                            new { value = "KEY_X", label = "X" },
                            new { value = "KEY_C", label = "C" },
                            new { value = "KEY_V", label = "V" },
                            new { value = "KEY_B", label = "B" },
                            new { value = "KEY_N", label = "N" },
                            new { value = "KEY_M", label = "M" },
                        }
                    },
                    new
                    {
                        group = "Function Keys (F1 - F12)",
                        options = new object[]
                        {
                            new { value = "F1", label = "F1" },
                            new { value = "F2", label = "F2" },
                            new { value = "F3", label = "F3" },
                            new { value = "F4", label = "F4" },
                            new { value = "F5", label = "F5" },
                            new { value = "F6", label = "F6" },
                            new { value = "F7", label = "F7" },
                            new { value = "F8", label = "F8" },
                            new { value = "F9", label = "F9" },
                            new { value = "F10", label = "F10" },
                            new { value = "F11", label = "F11" },
                            new { value = "F12", label = "F12" },
                        }
                    },
                    new
                    {
                        group = "Special & System Keys",
                        options = new object[]
                        {
                            new { value = "INSERT", label = "Insert" },
                            new { value = "SPACE", label = "Space" },
                            new { value = "TAB", label = "Tab" },
                            new { value = "ESCAPE", label = "Esc" },
                            new { value = "DELETE", label = "Delete" },
                            new { value = "HOME", label = "Home" },
                            new { value = "END", label = "End" },
                            new { value = "PRIOR", label = "Page Up" },
                            new { value = "NEXT", label = "Page Down" },
                            new { value = "OEM_3", label = "~" },
                            new { value = "LSHIFT", label = "Shift" },
                            new { value = "LCONTROL", label = "Ctrl" },
                            new { value = "LMENU", label = "Alt" },
                        }
                    },
                    new
                    {
                        group = "Numpad Keys",
                        options = new object[]
                        {
                            new { value = "NUMPAD0", label = "Num 0" },
                            new { value = "NUMPAD1", label = "Num 1" },
                            new { value = "NUMPAD2", label = "Num 2" },
                            new { value = "NUMPAD3", label = "Num 3" },
                            new { value = "NUMPAD4", label = "Num 4" },
                            new { value = "NUMPAD5", label = "Num 5" },
                            new { value = "NUMPAD6", label = "Num 6" },
                            new { value = "NUMPAD7", label = "Num 7" },
                            new { value = "NUMPAD8", label = "Num 8" },
                            new { value = "NUMPAD9", label = "Num 9" },
                        }
                    },
                },
            };
        }
    }
}
