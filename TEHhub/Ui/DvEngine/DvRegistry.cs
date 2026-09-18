// <copyright file="DvRegistry.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Ui.DvEngine
{
    using System;
    using System.Collections.Generic;
    using ImGuiNET;
    using TEHhub.RemoteObjects;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.Utils;

    /// <summary>
    ///     Central static registry of navigation nodes for DV v2.
    ///     Provides instant navigation targets for all core remote objects, components, caches, and tools.
    ///     Node creation is strictly metadata-only and never invokes live remote memory reads.
    /// </summary>
    public static class DvRegistry
    {
        private static readonly List<DvNavNode> Nodes = new();
        private static readonly Dictionary<string, DvNavNode> NodesById = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<DvNavNode>> NodesByCategory = new(StringComparer.OrdinalIgnoreCase);

        static DvRegistry()
        {
            RegisterNodes();
        }

        /// <summary>
        ///     Gets all registered static navigation nodes.
        /// </summary>
        public static IReadOnlyList<DvNavNode> AllNodes => Nodes;

        /// <summary>
        ///     Gets the categories and their associated nodes.
        /// </summary>
        public static IReadOnlyDictionary<string, List<DvNavNode>> Categories => NodesByCategory;

        /// <summary>
        ///     Finds a node by its stable identifier.
        /// </summary>
        public static DvNavNode? FindById(string id)
        {
            return NodesById.TryGetValue(id, out var node) ? node : null;
        }

        private static void Register(DvNavNode node)
        {
            if (NodesById.ContainsKey(node.Id))
            {
                throw new InvalidOperationException($"Duplicate DvNavNode Id: '{node.Id}'");
            }

            Nodes.Add(node);
            NodesById[node.Id] = node;

            if (!NodesByCategory.TryGetValue(node.Category, out var catList))
            {
                catList = new List<DvNavNode>();
                NodesByCategory[node.Category] = catList;
            }

            catList.Add(node);
        }

        private static void RegisterNodes()
        {
            // =========================================================================
            // 1. Core & System
            // =========================================================================
            Register(new DvNavNode
            {
                Id = "core.settings",
                DisplayName = "Overlay Settings",
                Category = "Core & System",
                Path = "Core > Settings",
                Tags = new[] { "config", "settings", "overlay", "state", "window" },
                Kind = DvNodeKind.CustomRenderer,
                CustomRenderer = () =>
                {
                    ImGui.TextDisabled("TEHHub Core Settings Snapshot (live fields):");
                    var fields = Core.GHSettings.GetType().GetFields();
                    for (var i = 0; i < fields.Length; i++)
                    {
                        var field = fields[i];
                        ImGui.Text($"{field.Name}: {field.GetValue(Core.GHSettings)}");
                    }

                    ImGui.Separator();
                    ImGui.Text($"Current Window Size: {Core.Overlay.Size}");
                    ImGui.Text($"Current Window Pos:  {Core.Overlay.Position}");
                },
            });

            Register(new DvNavNode
            {
                Id = "core.ggpk_cache",
                DisplayName = "GGPK Data Cache",
                Category = "Core & System",
                Path = "Core > GGPK Cache",
                Tags = new[] { "ggpk", "cache", "string cache", "object cache", "dat" },
                Kind = DvNodeKind.CustomRenderer,
                CustomRenderer = Core.CacheImGui,
            });

            Register(new DvNavNode
            {
                Id = "core.game_process",
                DisplayName = "Game Process & Static Offsets",
                Category = "Core & System",
                Path = "Core > Game Process",
                Tags = new[] { "process", "static", "address", "aob", "base", "offsets", "pid" },
                Kind = DvNodeKind.CustomRenderer,
                CustomRenderer = () =>
                {
                    if (Core.Process.Address != IntPtr.Zero)
                    {
                        ImGuiHelper.IntPtrToImGui("Base Address", Core.Process.Address);
                        ImGui.Text($"Process:    {Core.Process.Information}");
                        ImGui.Text($"WindowArea: {Core.Process.WindowArea}");
                        ImGui.Text($"Foreground: {Core.Process.Foreground}");
                        if (ImGui.TreeNode("Static Addresses (Discovered AOB Patterns)"))
                        {
                            foreach (var saddr in Core.Process.StaticAddresses)
                            {
                                ImGuiHelper.IntPtrToImGui(saddr.Key, saddr.Value);
                            }

                            ImGui.TreePop();
                        }
                    }
                    else
                    {
                        ImGui.TextDisabled("Game process not found or not attached.");
                    }
                },
            });

            Register(new DvNavNode
            {
                Id = "core.loaded_files",
                DisplayName = "Loaded Files (Preloads)",
                Category = "Core & System",
                Path = "Core > Loaded Files",
                Tags = new[] { "preload", "preloads", "files", "loaded files", "dump", "area files" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.CurrentAreaLoadedFiles,
            });

            Register(new DvNavNode
            {
                Id = "core.area_change",
                DisplayName = "Area Change Counter",
                Category = "Core & System",
                Path = "Core > Area Change Counter",
                Tags = new[] { "area change", "counter", "instance change", "zone change" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.AreaChangeCounter,
            });

            Register(new DvNavNode
            {
                Id = "core.game_scale",
                DisplayName = "Game Window Scale",
                Category = "Core & System",
                Path = "Core > Game Scale",
                Tags = new[] { "scale", "aspect", "ratio", "resolution" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.GameScale,
            });

            Register(new DvNavNode
            {
                Id = "core.game_cull",
                DisplayName = "Game Window Cull Size",
                Category = "Core & System",
                Path = "Core > Game Cull",
                Tags = new[] { "cull", "black bar", "letterbox" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.GameCull,
            });

            Register(new DvNavNode
            {
                Id = "core.rotators",
                DisplayName = "Terrain Rotator Helpers",
                Category = "Core & System",
                Path = "Core > Terrain Rotators",
                Tags = new[] { "rotator", "rotation", "terrain height helper" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.RotatorHelper,
            });

            // =========================================================================
            // 2. States & Environment
            // =========================================================================
            Register(new DvNavNode
            {
                Id = "states.hub",
                DisplayName = "Game States Hub",
                Category = "States & Environment",
                Path = "States > All States",
                Tags = new[] { "states", "gamestates", "current state", "state pointer" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States,
            });

            Register(new DvNavNode
            {
                Id = "states.ingame",
                DisplayName = "InGameState Root",
                Category = "States & Environment",
                Path = "States > InGameState",
                Tags = new[] { "ingame", "ingamestate", "ui root" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject,
            });

            Register(new DvNavNode
            {
                Id = "states.area_loading",
                DisplayName = "Area Loading State",
                Category = "States & Environment",
                Path = "States > Area Loading",
                Tags = new[] { "loading", "loading screen", "load time" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.AreaLoading,
            });

            Register(new DvNavNode
            {
                Id = "area.current",
                DisplayName = "Current Area Instance",
                Category = "States & Environment",
                Path = "States > InGameState > CurrentAreaInstance",
                Tags = new[] { "area", "zone", "instance", "map", "monster level", "terrain metadata", "area hash" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.CurrentAreaInstance,
            });

            Register(new DvNavNode
            {
                Id = "area.modifiers",
                DisplayName = "Area / Map Modifiers",
                Category = "States & Environment",
                Path = "States > InGameState > CurrentAreaInstance > Modifiers",
                Tags = new[] { "mods", "modifiers", "map mods", "area mods", "affixes", "pack size", "quant" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.CurrentAreaInstance,
            });

            Register(new DvNavNode
            {
                Id = "world.data",
                DisplayName = "WorldData & Matrix",
                Category = "States & Environment",
                Path = "States > InGameState > WorldData",
                Tags = new[] { "world", "matrix", "world to screen", "camera", "w2s" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.CurrentWorldInstance,
            });

            Register(new DvNavNode
            {
                Id = "world.area_details",
                DisplayName = "Area Details (WorldAreaDat)",
                Category = "States & Environment",
                Path = "States > InGameState > WorldData > AreaDetails",
                Tags = new[] { "area name", "town", "hideout", "waypoint", "dat", "battleroyale" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.CurrentWorldInstance?.AreaDetails,
            });

            // =========================================================================
            // 3. ServerData & Inventory
            // =========================================================================
            Register(new DvNavNode
            {
                Id = "serverdata.gold",
                DisplayName = "Gold / ServerData",
                Category = "ServerData & Inventory",
                Path = "States > InGameState > CurrentAreaInstance > ServerData",
                Tags = new[] { "gold", "currency", "money", "coins", "serverdata", "native gold", "balance" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.CurrentAreaInstance?.ServerDataObject,
            });

            Register(new DvNavNode
            {
                Id = "serverdata.inventories",
                DisplayName = "Player Inventories & Flasks",
                Category = "ServerData & Inventory",
                Path = "States > InGameState > CurrentAreaInstance > ServerData > Inventories",
                Tags = new[] { "inventory", "flask", "items", "bag", "stash", "flasks", "slots" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.CurrentAreaInstance?.ServerDataObject,
            });

            // =========================================================================
            // 4. Player Components
            // =========================================================================
            Register(new DvNavNode
            {
                Id = "player.root",
                DisplayName = "Player (LocalPlayer)",
                Category = "Player Components",
                Path = "Player > Root",
                Tags = new[] { "player", "character", "localplayer", "me", "hero" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.CurrentAreaInstance?.Player,
            });

            Register(new DvNavNode
            {
                Id = "player.life",
                DisplayName = "Life (Health / ES / Mana)",
                Category = "Player Components",
                Path = "Player > Components > Life",
                Tags = new[] { "life", "hp", "health", "mana", "mp", "es", "energy shield", "ward", "spirit", "divinity", "vitals" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () =>
                {
                    var p = Core.States.InGameStateObject?.CurrentAreaInstance?.Player;
                    return p != null && p.TryGetComponent<Life>(out var comp) ? comp : null;
                },
            });

            Register(new DvNavNode
            {
                Id = "player.buffs",
                DisplayName = "Buffs & Status Effects",
                Category = "Player Components",
                Path = "Player > Components > Buffs",
                Tags = new[] { "buff", "buffs", "debuff", "debuffs", "status", "flasks", "charges", "auras", "effects" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () =>
                {
                    var p = Core.States.InGameStateObject?.CurrentAreaInstance?.Player;
                    return p != null && p.TryGetComponent<Buffs>(out var comp) ? comp : null;
                },
            });

            Register(new DvNavNode
            {
                Id = "player.actor",
                DisplayName = "Actor (Skills & Cooldowns)",
                Category = "Player Components",
                Path = "Player > Components > Actor",
                Tags = new[] { "actor", "skill", "skills", "cooldown", "cooldowns", "gem", "animation", "deployed", "totem", "minion" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () =>
                {
                    var p = Core.States.InGameStateObject?.CurrentAreaInstance?.Player;
                    return p != null && p.TryGetComponent<Actor>(out var comp) ? comp : null;
                },
            });

            Register(new DvNavNode
            {
                Id = "player.stats",
                DisplayName = "Stats (Gear & Passive Stats)",
                Category = "Player Components",
                Path = "Player > Components > Stats",
                Tags = new[] { "stat", "stats", "attributes", "strength", "dexterity", "intelligence", "resists", "damage" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () =>
                {
                    var p = Core.States.InGameStateObject?.CurrentAreaInstance?.Player;
                    return p != null && p.TryGetComponent<Stats>(out var comp) ? comp : null;
                },
            });

            Register(new DvNavNode
            {
                Id = "player.render",
                DisplayName = "Render (Position & Bounds)",
                Category = "Player Components",
                Path = "Player > Components > Render",
                Tags = new[] { "render", "position", "pos", "grid", "world pos", "coords", "terrain height", "bounds", "xyz" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () =>
                {
                    var p = Core.States.InGameStateObject?.CurrentAreaInstance?.Player;
                    return p != null && p.TryGetComponent<Render>(out var comp) ? comp : null;
                },
            });

            Register(new DvNavNode
            {
                Id = "player.positioned",
                DisplayName = "Positioned (Reaction & Flags)",
                Category = "Player Components",
                Path = "Player > Components > Positioned",
                Tags = new[] { "positioned", "reaction", "friendly", "flags" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () =>
                {
                    var p = Core.States.InGameStateObject?.CurrentAreaInstance?.Player;
                    return p != null && p.TryGetComponent<Positioned>(out var comp) ? comp : null;
                },
            });

            // =========================================================================
            // 5. Entities & UI
            // =========================================================================
            Register(new DvNavNode
            {
                Id = "entities.awake",
                DisplayName = "Awake Entities Explorer",
                Category = "Entities & UI",
                Path = "States > InGameState > CurrentAreaInstance > Awake Entities",
                Tags = new[] { "entity", "entities", "awake", "monsters", "npc", "chests", "loot", "items", "rarity filter" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.CurrentAreaInstance,
            });

            Register(new DvNavNode
            {
                Id = "entities.sleeping",
                DisplayName = "Sleeping Entities Scanner",
                Category = "Entities & UI",
                Path = "States > InGameState > CurrentAreaInstance > Sleeping Entities",
                Tags = new[] { "sleeping", "scan sleeping", "dormant", "decorations", "effects" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.CurrentAreaInstance,
            });

            Register(new DvNavNode
            {
                Id = "entities.mouseover",
                DisplayName = "MouseOver Entity",
                Category = "Entities & UI",
                Path = "States > InGameState > MouseOverEntity",
                Tags = new[] { "hover", "mouseover", "cursor entity", "target", "hovered" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.MouseOverEntity,
            });

            Register(new DvNavNode
            {
                Id = "ui.game_ui",
                DisplayName = "GameUi & Atlas Maps",
                Category = "Entities & UI",
                Path = "States > InGameState > GameUi",
                Tags = new[] { "ui", "gameui", "atlas", "atlas maps", "chat", "map ui", "skill tree" },
                Kind = DvNodeKind.RemoteObject,
                ObjectResolver = () => Core.States.InGameStateObject?.GameUi,
            });

            // =========================================================================
            // 6. Legacy View (Complete Fallback)
            // =========================================================================
            Register(new DvNavNode
            {
                Id = "legacy.all",
                DisplayName = "Legacy DV (Complete Hierarchy)",
                Category = "Legacy Fallback",
                Path = "Legacy > Complete Hierarchy",
                Tags = new[] { "legacy", "tree", "all", "old", "compatibility", "root", "full tree" },
                Kind = DvNodeKind.Legacy,
            });
        }
    }
}