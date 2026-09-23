// <copyright file="DebugDumpSystem.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text.RegularExpressions;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteEnums.Entity;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;
    using SixLabors.ImageSharp;
    using SixLabors.ImageSharp.PixelFormats;

    /// <summary>
    /// Entity categories matching the original AutoExile dump specification.
    /// </summary>
    public enum EntityCategory
    {
        Monster,
        NPC,
        Player,
        Chest,
        AreaTransition,
        Portal,
        Monolith,
        Stash,
        WorldItem,
        Other,
    }

    /// <summary>
    /// Game state snapshot and debug dump system, fully ported and enhanced from AutoExile.
    /// Dumps complete terrain, exploration grid, entities, loot, combat clusters, and pathfinding to PNG + JSON.
    /// </summary>
    public static class DebugDumpSystem
    {
        private const float NetworkBubbleRadius = 150f;
        private static readonly object DumpLock = new();

        /// <summary>
        /// Captures and dumps the current game state to PNG image and JSON file.
        /// </summary>
        public static (string jsonPath, string? pngPath, string message) DumpCurrentState(
            AreaInstance? area,
            string areaName,
            ExplorationMap explorationMap,
            CombatSystem combatSystem,
            AutoExile2Settings settings,
            string currentState,
            string currentAction,
            List<Vector2> currentNavPath,
            int currentWaypointIndex,
            Vector2? currentDestination,
            PerformanceTracker? perf = null,
            RuntimeTracker? runtime = null)
        {
            lock (DumpLock)
            {
                if (area == null || area.Player == null)
                {
                    return ("", null, "Dump failed: Not in game or player entity is null");
                }

                var player = area.Player;
                if (!player.TryGetComponent<Render>(out var pRender))
                {
                    return ("", null, "Dump failed: Player render component unavailable");
                }

                try
                {
                    string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dumps");
                    Directory.CreateDirectory(outputDir);

                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string safeArea = Regex.Replace(string.IsNullOrWhiteSpace(areaName) ? area.AreaHash : areaName, @"[^a-zA-Z0-9_\-]", "_");
                    string baseName = $"GameState_{safeArea}_{timestamp}";

                    var playerGrid = new Vector2(pRender.GridPosition.X, pRender.GridPosition.Y);
                    var playerWorld = new Vector3(pRender.WorldPosition.X, pRender.WorldPosition.Y, pRender.WorldPosition.Z);

                    int playerHp = 0, playerMaxHp = 0, playerMana = 0, playerMaxMana = 0;
                    if (player.TryGetComponent<Life>(out var pLife))
                    {
                        playerHp = pLife.Health.Current;
                        playerMaxHp = pLife.Health.Total;
                        playerMana = pLife.Mana.Current;
                        playerMaxMana = pLife.Mana.Total;
                    }

                    int rows = area.GridHeightData.Length;
                    int cols = rows > 0 ? area.GridHeightData[0].Length : 0;
                    int bytesPerRow = area.TerrainMetadata.BytesPerRow;
                    var walkableData = area.GridWalkableData;
                    if (rows == 0 && bytesPerRow > 0 && walkableData != null)
                    {
                        rows = walkableData.Length / bytesPerRow;
                        cols = bytesPerRow * 2;
                    }

                    // 1. Collect entities snapshot (Matching AutoExile's GameStateSnapshot)
                    var entitySnapshots = new List<Dictionary<string, object>>();
                    var lootCandidates = new List<Dictionary<string, object>>();
                    var aliveHostiles = new List<(Vector2 grid, float dist, Rarity rarity)>();
                    var monsterCoords = new List<(int x, int y, Rarity rarity, bool isAlive)>();
                    var chestCoords = new List<(int x, int y, bool isOpened)>();
                    var transitionCoords = new List<(int x, int y)>();
                    var npcCoords = new List<(int x, int y)>();
                    var lootCoords = new List<(int x, int y)>();

                    foreach (var entity in area.AwakeEntities.Values)
                    {
                        if (!entity.IsValid) continue;

                        Vector2 entGrid = Vector2.Zero;
                        Vector3 entWorld = Vector3.Zero;
                        if (entity.TryGetComponent<Render>(out var eRender))
                        {
                            entGrid = new Vector2(eRender.GridPosition.X, eRender.GridPosition.Y);
                            entWorld = new Vector3(eRender.WorldPosition.X, eRender.WorldPosition.Y, eRender.WorldPosition.Z);
                        }

                        float dist = Vector2.Distance(playerGrid, entGrid);

                        int curHp = 0, maxHp = 0;
                        bool isAlive = false;
                        if (entity.TryGetComponent<Life>(out var life))
                        {
                            curHp = life.Health.Current;
                            maxHp = life.Health.Total;
                            isAlive = curHp > 0;
                        }

                        bool isTargetable = entity.TryGetComponent<Targetable>(out var targetable) && targetable.IsTargetable;
                        bool isFriendly = entity.TryGetComponent<Positioned>(out var posComp) && posComp.IsFriendly;
                        bool isHostile = !isFriendly;

                        Rarity rarity = Rarity.Normal;
                        var mods = new List<string>();
                        if (entity.TryGetComponent<ObjectMagicProperties>(out var omp))
                        {
                            rarity = omp.Rarity;
                            if (omp.ModNames != null)
                            {
                                mods.AddRange(omp.ModNames);
                            }
                        }

                        string path = entity.Path ?? "";
                        string shortName = path.Contains('/') ? path.Substring(path.LastIndexOf('/') + 1) : path;

                        // Categorize matching AutoExile
                        EntityCategory category = EntityCategory.Other;
                        if (entity.EntityType == EntityTypes.Monster || path.StartsWith("Metadata/Monsters/"))
                        {
                            category = EntityCategory.Monster;
                        }
                        else if (entity.EntityType == EntityTypes.Player || path.StartsWith("Metadata/Characters/"))
                        {
                            category = EntityCategory.Player;
                        }
                        else if (entity.EntityType == EntityTypes.NPC || path.StartsWith("Metadata/NPC/"))
                        {
                            category = EntityCategory.NPC;
                        }
                        else if (entity.EntityType == EntityTypes.Chest || path.StartsWith("Metadata/Chests/"))
                        {
                            category = path.Contains("Stash", StringComparison.OrdinalIgnoreCase)
                                ? EntityCategory.Stash
                                : EntityCategory.Chest;
                        }
                        else if (entity.EntitySubtype == EntitySubtypes.WorldItem || path.Contains("WorldItem") || path.StartsWith("Metadata/Items/"))
                        {
                            category = EntityCategory.WorldItem;
                        }
                        else if (path.Contains("Portal", StringComparison.OrdinalIgnoreCase))
                        {
                            category = EntityCategory.Portal;
                        }
                        else if (path.Contains("AreaTransition", StringComparison.OrdinalIgnoreCase) || entity.TryGetComponent<Transitionable>(out _))
                        {
                            category = EntityCategory.AreaTransition;
                        }
                        else if (path.Contains("Monolith", StringComparison.OrdinalIgnoreCase) || path.Contains("Shrine", StringComparison.OrdinalIgnoreCase))
                        {
                            category = EntityCategory.Monolith;
                        }

                        // StateMachine states
                        Dictionary<string, long>? smStates = null;
                        if (entity.TryGetComponent<StateMachine>(out var sm) && sm.States != null)
                        {
                            smStates = new Dictionary<string, long>();
                            foreach (var st in sm.States)
                            {
                                if (!string.IsNullOrEmpty(st.Name))
                                {
                                    smStates[st.Name] = st.Value;
                                }
                            }
                        }

                        // MinimapIcon
                        string? minimapIconName = null;
                        if (entity.TryGetComponent<MinimapIcon>(out var mIcon))
                        {
                            minimapIconName = mIcon.IconName;
                        }

                        // Player RenderName
                        string renderName = "";
                        if (category == EntityCategory.Player && entity.TryGetComponent<Player>(out var plComp))
                        {
                            renderName = plComp.Name;
                        }

                        // Chest states
                        bool? isOpened = null;
                        bool? isStrongbox = null;
                        if (category == EntityCategory.Chest && entity.TryGetComponent<Chest>(out var chestComp))
                        {
                            isOpened = chestComp.IsOpened;
                            isStrongbox = chestComp.IsStrongbox;
                        }

                        var entObj = new Dictionary<string, object>
                        {
                            ["id"] = entity.Id,
                            ["shortName"] = shortName,
                            ["metadata"] = path,
                            ["path"] = path,
                            ["entityType"] = entity.EntityType.ToString(),
                            ["category"] = category.ToString(),
                            ["gridPos"] = new[] { Math.Round(entGrid.X, 1), Math.Round(entGrid.Y, 1) },
                            ["worldPos"] = new[] { Math.Round(entWorld.X, 1), Math.Round(entWorld.Y, 1), Math.Round(entWorld.Z, 1) },
                            ["distToPlayer"] = Math.Round(dist, 1),
                            ["isAlive"] = isAlive,
                            ["isTargetable"] = isTargetable,
                            ["isHostile"] = isHostile,
                            ["rarity"] = rarity.ToString(),
                            ["hp"] = curHp,
                            ["maxHp"] = maxHp,
                            ["renderName"] = renderName,
                            ["mods"] = mods,
                        };

                        if (!string.IsNullOrEmpty(minimapIconName)) entObj["minimapIconName"] = minimapIconName;
                        if (smStates != null && smStates.Count > 0) entObj["states"] = smStates;
                        if (isOpened.HasValue) entObj["isOpened"] = isOpened.Value;
                        if (isStrongbox.HasValue) entObj["isStrongbox"] = isStrongbox.Value;

                        if (entity.TryGetComponent<Actor>(out var entActor) && entActor.ActiveSkills != null)
                        {
                            var sList = new List<string>();
                            foreach (var (sn, _) in entActor.ActiveSkills)
                            {
                                if (!string.IsNullOrWhiteSpace(sn)) sList.Add(sn);
                            }
                            if (sList.Count > 0) entObj["skills"] = sList;
                        }

                        entitySnapshots.Add(entObj);

                        int ex = (int)entGrid.X;
                        int ey = (int)entGrid.Y;

                        if (category == EntityCategory.Monster)
                        {
                            monsterCoords.Add((ex, ey, rarity, isAlive));
                            if (isAlive && isHostile && isTargetable)
                            {
                                aliveHostiles.Add((entGrid, dist, rarity));
                            }
                        }
                        else if (category == EntityCategory.Chest || category == EntityCategory.Stash)
                        {
                            chestCoords.Add((ex, ey, isOpened == true));
                        }
                        else if (category is EntityCategory.AreaTransition or EntityCategory.Portal)
                        {
                            transitionCoords.Add((ex, ey));
                        }
                        else if (category == EntityCategory.NPC)
                        {
                            npcCoords.Add((ex, ey));
                        }
                        else if (category == EntityCategory.WorldItem)
                        {
                            lootCoords.Add((ex, ey));
                            lootCandidates.Add(new Dictionary<string, object>
                            {
                                ["entityId"] = entity.Id,
                                ["itemName"] = shortName,
                                ["gridPos"] = new[] { Math.Round(entGrid.X, 1), Math.Round(entGrid.Y, 1) },
                                ["distance"] = Math.Round(dist, 1),
                            });
                        }
                    }

                    // 2. Compute Pack Center & Dense Cluster Center (Matching AutoExile CombatSnapshot)
                    Vector2 packCenter = Vector2.Zero;
                    Vector2 denseClusterCenter = Vector2.Zero;
                    Vector2? nearestMonsterGrid = null;

                    if (aliveHostiles.Count > 0)
                    {
                        var nearest = aliveHostiles.OrderBy(m => m.dist).First();
                        nearestMonsterGrid = nearest.grid;

                        var nearby = aliveHostiles.Where(m => m.dist <= settings.CombatRange * 1.5f).ToList();
                        if (nearby.Count == 0) nearby = aliveHostiles.Take(10).ToList();

                        float sumX = 0, sumY = 0;
                        foreach (var h in nearby)
                        {
                            sumX += h.grid.X;
                            sumY += h.grid.Y;
                        }
                        packCenter = new Vector2(sumX / nearby.Count, sumY / nearby.Count);

                        // Dense cluster: monster with highest density of neighbors within 25 units
                        int maxNeighbors = -1;
                        Vector2 bestCenter = packCenter;
                        foreach (var h in nearby)
                        {
                            int count = nearby.Count(other => Vector2.Distance(h.grid, other.grid) <= 25f);
                            if (count > maxNeighbors)
                            {
                                maxNeighbors = count;
                                bestCenter = h.grid;
                            }
                        }
                        denseClusterCenter = bestCenter;
                    }

                    // 3. Build Full JSON Payload (Exact AutoExile specification)
                    var dumpJson = new Dictionary<string, object>
                    {
                        ["metadata"] = new Dictionary<string, object>
                        {
                            ["timestamp"] = DateTime.Now.ToString("o"),
                            ["areaName"] = areaName,
                            ["areaHash"] = area.AreaHash,
                            ["gridRows"] = rows,
                            ["gridCols"] = cols,
                            ["bytesPerRow"] = bytesPerRow,
                            ["worldToGridRatio"] = area.WorldToGridConvertor,
                            ["networkBubbleRadius"] = NetworkBubbleRadius,
                        },
                        ["player"] = new Dictionary<string, object>
                        {
                            ["gridPos"] = new[] { Math.Round(playerGrid.X, 1), Math.Round(playerGrid.Y, 1) },
                            ["worldPos"] = new[] { Math.Round(playerWorld.X, 1), Math.Round(playerWorld.Y, 1), Math.Round(playerWorld.Z, 1) },
                            ["hp"] = playerHp,
                            ["maxHp"] = playerMaxHp,
                            ["mana"] = playerMana,
                            ["maxMana"] = playerMaxMana,
                            ["hpPercent"] = playerMaxHp > 0 ? (float)playerHp / playerMaxHp * 100f : 100f,
                            ["manaPercent"] = playerMaxMana > 0 ? (float)playerMana / playerMaxMana * 100f : 100f,
                            ["skills"] = player != null && player.TryGetComponent<Actor>(out var pAct) && pAct.ActiveSkills != null
                                ? pAct.ActiveSkills.Keys.Where(k => !string.IsNullOrWhiteSpace(k)).ToList()
                                : new List<string>(),
                        },
                        ["botState"] = new Dictionary<string, object>
                        {
                            ["mode"] = settings.Mode.ToString(),
                            ["currentState"] = currentState,
                            ["currentAction"] = currentAction,
                            ["isRunning"] = settings.IsRunning,
                            ["useSprint"] = settings.UseSprint,
                            ["sprintKey"] = settings.SprintKey.ToString(),
                            ["toggleKey"] = settings.ToggleKey.ToString(),
                            ["dumpKey"] = settings.DumpKey.ToString(),
                        },
                        ["combat"] = new Dictionary<string, object>
                        {
                            ["inCombat"] = combatSystem.NearbyHostileCount > 0,
                            ["nearbyMonsterCount"] = combatSystem.NearbyHostileCount,
                            ["closestHostileDistance"] = Math.Round(combatSystem.ClosestHostileDistance, 1),
                            ["currentTargetId"] = combatSystem.CurrentTargetId,
                            ["packCenter"] = new[] { Math.Round(packCenter.X, 1), Math.Round(packCenter.Y, 1) },
                            ["denseClusterCenter"] = new[] { Math.Round(denseClusterCenter.X, 1), Math.Round(denseClusterCenter.Y, 1) },
                            ["nearestMonsterPos"] = nearestMonsterGrid.HasValue ? new[] { Math.Round(nearestMonsterGrid.Value.X, 1), Math.Round(nearestMonsterGrid.Value.Y, 1) } : null!,
                            ["combatRange"] = settings.CombatRange,
                            ["primaryAttackType"] = settings.PrimaryAttackType.ToString(),
                            ["primaryAttackKey"] = settings.PrimaryAttackKey.ToString(),
                            ["useSecondaryAttack"] = settings.UseSecondaryAttack,
                            ["secondaryAttackType"] = settings.SecondaryAttackType.ToString(),
                            ["secondaryAttackKey"] = settings.SecondaryAttackKey.ToString(),
                        },
                        ["loot"] = new Dictionary<string, object>
                        {
                            ["hasLootNearby"] = lootCandidates.Count > 0,
                            ["candidateCount"] = lootCandidates.Count,
                            ["candidates"] = lootCandidates,
                        },
                        ["exploration"] = new Dictionary<string, object>
                        {
                            ["coveragePercent"] = Math.Round(explorationMap.Coverage, 2),
                            ["activeBlobIndex"] = explorationMap.ActiveBlobIndex,
                            ["totalBlobs"] = explorationMap.TotalBlobCount,
                            ["totalWalkableCells"] = explorationMap.TotalWalkableCells,
                            ["currentDestination"] = currentDestination.HasValue
                                ? new[] { Math.Round(currentDestination.Value.X, 1), Math.Round(currentDestination.Value.Y, 1) }
                                : null!,
                            ["waypointIndex"] = currentWaypointIndex,
                            ["waypointCount"] = currentNavPath.Count,
                            ["navPathWaypoints"] = currentNavPath.Select(wp => new[] { Math.Round(wp.X, 1), Math.Round(wp.Y, 1) }).ToList(),
                            ["blobs"] = explorationMap.Blobs.Select(b => new Dictionary<string, object>
                            {
                                ["index"] = b.Index,
                                ["walkableCells"] = b.WalkableCells.Count,
                                ["seenCells"] = b.SeenCells.Count,
                                ["coverage"] = Math.Round(b.Coverage, 4),
                                ["regionCount"] = b.Regions.Count,
                                ["regions"] = b.Regions.Select(r => new Dictionary<string, object>
                                {
                                    ["index"] = r.Index,
                                    ["center"] = new[] { Math.Round(r.Center.X, 1), Math.Round(r.Center.Y, 1) },
                                    ["cellCount"] = r.CellCount,
                                    ["seenCount"] = r.SeenCount,
                                    ["exploredRatio"] = Math.Round(r.ExploredRatio, 4),
                                }).ToList(),
                            }).ToList(),
                        },
                        ["terrain"] = BuildTerrainSection(walkableData, bytesPerRow, rows, cols) ?? new Dictionary<string, object>(),
                        ["entities"] = entitySnapshots,
                        ["performance"] = perf?.GetAllStats() ?? new Dictionary<string, object>(),
                        ["failures"] = perf?.GetAllFailures() ?? new Dictionary<string, Dictionary<string, int>>(),
                        ["runtime"] = runtime != null ? new Dictionary<string, object>
                        {
                            ["sessionStart"] = runtime.SessionStart.ToString("o"),
                            ["activeDuration"] = runtime.FormattedDuration,
                            ["activeSeconds"] = Math.Round(runtime.ActiveDuration.TotalSeconds, 1),
                            ["isPaused"] = runtime.IsPaused,
                        } : null!,
                    };

                    string jsonPath = Path.Combine(outputDir, $"{baseName}.json");
                    File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(dumpJson, AutoExileJson.Options));

                    // 4. Render map image (PNG) with AutoExile bounding-box cropping & layers
                    string? pngPath = null;
                    if (walkableData != null && rows > 0 && cols > 0 && rows <= 6000 && cols <= 6000)
                    {
                        pngPath = Path.Combine(outputDir, $"{baseName}.png");
                        RenderMapImage(
                            pngPath, cols, rows, bytesPerRow, walkableData,
                            playerGrid, currentDestination, currentNavPath,
                            packCenter, denseClusterCenter,
                            monsterCoords, chestCoords, transitionCoords, npcCoords, lootCoords);
                    }

                    string summary = $"Dump saved: {baseName} ({entitySnapshots.Count} entities, {explorationMap.Coverage:F0}% explored)";
                    return (jsonPath, pngPath, summary);
                }
                catch (Exception ex)
                {
                    return ("", null, $"Dump failed: {ex.Message}");
                }
            }
        }

        private static void RenderMapImage(
            string pngPath,
            int cols,
            int rows,
            int bytesPerRow,
            byte[] walkableData,
            Vector2 playerGrid,
            Vector2? destination,
            List<Vector2> navPath,
            Vector2 packCenter,
            Vector2 denseClusterCenter,
            List<(int x, int y, Rarity rarity, bool isAlive)> monsters,
            List<(int x, int y, bool isOpened)> chests,
            List<(int x, int y)> transitions,
            List<(int x, int y)> npcs,
            List<(int x, int y)> loot)
        {
            // AutoExile Bounding-Box Cropping: find min/max walkable coordinates
            int minX = cols, maxX = 0, minY = rows, maxY = 0;
            for (int y = 0; y < rows; y++)
            {
                int byteRowOffset = y * bytesPerRow;
                for (int x = 0; x < cols; x++)
                {
                    int byteIdx = byteRowOffset + (x / 2);
                    if (byteIdx < walkableData.Length)
                    {
                        byte b = walkableData[byteIdx];
                        int nibble = (x % 2 == 0) ? (b & 0x0F) : ((b >> 4) & 0x0F);
                        if (nibble != 0)
                        {
                            if (x < minX) minX = x;
                            if (x > maxX) maxX = x;
                            if (y < minY) minY = y;
                            if (y > maxY) maxY = y;
                        }
                    }
                }
            }

            // Include player position in crop
            int px = (int)playerGrid.X;
            int py = (int)playerGrid.Y;
            if (px < minX) minX = px;
            if (px > maxX) maxX = px;
            if (py < minY) minY = py;
            if (py > maxY) maxY = py;

            if (minX > maxX || minY > maxY)
            {
                minX = 0; maxX = cols - 1; minY = 0; maxY = rows - 1;
            }

            const int pad = 12;
            minX = Math.Max(0, minX - pad);
            minY = Math.Max(0, minY - pad);
            maxX = Math.Min(cols - 1, maxX + pad);
            maxY = Math.Min(rows - 1, maxY + pad);

            int w = maxX - minX + 1;
            int h = maxY - minY + 1;

            using var image = new Image<Rgba32>(w, h);

            var colBg = new Rgba32(18, 20, 26, 255);
            var colWalkable = new Rgba32(58, 64, 76, 255);

            // Layer 1: Walkable Terrain Base
            image.ProcessPixelRows(accessor =>
            {
                for (int cy = 0; cy < h; cy++)
                {
                    int y = minY + cy;
                    var rowSpan = accessor.GetRowSpan(cy);
                    int byteRowOffset = y * bytesPerRow;

                    for (int cx = 0; cx < w; cx++)
                    {
                        int x = minX + cx;
                        int byteIdx = byteRowOffset + (x / 2);
                        if (byteIdx < walkableData.Length)
                        {
                            byte b = walkableData[byteIdx];
                            int nibble = (x % 2 == 0) ? (b & 0x0F) : ((b >> 4) & 0x0F);
                            rowSpan[cx] = nibble != 0 ? colWalkable : colBg;
                        }
                        else
                        {
                            rowSpan[cx] = colBg;
                        }
                    }
                }
            });

            // Layer 2: Network Bubble Radius Circle (AutoExile style: transparent cyan circle outline)
            int pLocalX = px - minX;
            int pLocalY = py - minY;
            var colBubble = new Rgba32(40, 180, 240, 75);
            DrawCircleOutline(image, pLocalX, pLocalY, (int)NetworkBubbleRadius, colBubble);

            // Layer 3: Navigation Path Line (Yellow)
            var colPath = new Rgba32(255, 220, 50, 230);
            if (navPath != null && navPath.Count > 1)
            {
                for (int i = 0; i < navPath.Count - 1; i++)
                {
                    DrawLine(image, (int)navPath[i].X - minX, (int)navPath[i].Y - minY, (int)navPath[i + 1].X - minX, (int)navPath[i + 1].Y - minY, colPath);
                }
            }

            // Layer 4: Combat Centers (AutoExile style: Pack Center + Dense Cluster Center)
            if (packCenter != Vector2.Zero)
            {
                var colPackCenter = new Rgba32(255, 100, 100, 240);
                DrawCross(image, (int)packCenter.X - minX, (int)packCenter.Y - minY, 5, colPackCenter);
            }
            if (denseClusterCenter != Vector2.Zero)
            {
                var colClusterCenter = new Rgba32(255, 200, 50, 240);
                DrawCross(image, (int)denseClusterCenter.X - minX, (int)denseClusterCenter.Y - minY, 4, colClusterCenter);
            }

            // Layer 5: Loot Candidates (Bright Green Diamonds)
            var colLoot = new Rgba32(100, 255, 100, 255);
            foreach (var (lx, ly) in loot)
            {
                DrawDiamond(image, lx - minX, ly - minY, 3, colLoot);
            }

            // Layer 6: Chests, Transitions, NPCs
            var colChestClosed = new Rgba32(255, 200, 0, 255);
            var colChestOpened = new Rgba32(110, 90, 0, 180);
            foreach (var (cx, cy, opened) in chests)
            {
                DrawFilledCircle(image, cx - minX, cy - minY, opened ? 2 : 3, opened ? colChestOpened : colChestClosed);
            }

            var colTrans = new Rgba32(40, 240, 240, 255);
            foreach (var (tx, ty) in transitions)
            {
                DrawFilledCircle(image, tx - minX, ty - minY, 5, colTrans);
            }

            var colNpc = new Rgba32(80, 220, 80, 255);
            foreach (var (nx, ny) in npcs)
            {
                DrawFilledCircle(image, nx - minX, ny - minY, 3, colNpc);
            }

            // Layer 7: Monsters (Unique, Rare, Normal alive, Corpse)
            var colMonsterNormal = new Rgba32(240, 50, 50, 255);
            var colMonsterRare = new Rgba32(255, 150, 20, 255);
            var colMonsterBoss = new Rgba32(230, 70, 240, 255);
            var colMonsterDead = new Rgba32(120, 45, 45, 180);

            foreach (var (mx, my, rarity, isAlive) in monsters)
            {
                int localX = mx - minX;
                int localY = my - minY;
                if (!isAlive)
                {
                    DrawFilledCircle(image, localX, localY, 1, colMonsterDead);
                }
                else if (rarity == Rarity.Unique)
                {
                    DrawFilledCircle(image, localX, localY, 5, colMonsterBoss);
                }
                else if (rarity >= Rarity.Rare)
                {
                    DrawFilledCircle(image, localX, localY, 4, colMonsterRare);
                }
                else
                {
                    DrawFilledCircle(image, localX, localY, 2, colMonsterNormal);
                }
            }

            // Layer 8: Target Destination
            if (destination.HasValue)
            {
                var colDest = new Rgba32(255, 255, 0, 255);
                DrawCross(image, (int)destination.Value.X - minX, (int)destination.Value.Y - minY, 5, colDest);
            }

            // Layer 9: Player Position (Green with white center)
            var colPlayer = new Rgba32(50, 255, 70, 255);
            var colPlayerCenter = new Rgba32(255, 255, 255, 255);
            DrawFilledCircle(image, pLocalX, pLocalY, 5, colPlayer);
            DrawFilledCircle(image, pLocalX, pLocalY, 2, colPlayerCenter);

            image.SaveAsPng(pngPath);
        }

        private static void SetPixelSafe(Image<Rgba32> img, int x, int y, Rgba32 color)
        {
            if (x >= 0 && x < img.Width && y >= 0 && y < img.Height)
            {
                img[x, y] = color;
            }
        }

        private static void DrawFilledCircle(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
        {
            int r2 = radius * radius;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int y = cy + dy;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = cx + dx;
                    if (dx * dx + dy * dy <= r2)
                    {
                        SetPixelSafe(img, x, y, color);
                    }
                }
            }
        }

        private static void DrawCircleOutline(Image<Rgba32> img, int cx, int cy, int radius, Rgba32 color)
        {
            int x = radius, y = 0;
            int err = 0;

            while (x >= y)
            {
                SetPixelSafe(img, cx + x, cy + y, color);
                SetPixelSafe(img, cx + y, cy + x, color);
                SetPixelSafe(img, cx - y, cy + x, color);
                SetPixelSafe(img, cx - x, cy + y, color);
                SetPixelSafe(img, cx - x, cy - y, color);
                SetPixelSafe(img, cx - y, cy - x, color);
                SetPixelSafe(img, cx + y, cy - x, color);
                SetPixelSafe(img, cx + x, cy - y, color);

                if (err <= 0)
                {
                    y += 1;
                    err += 2 * y + 1;
                }
                if (err > 0)
                {
                    x -= 1;
                    err -= 2 * x + 1;
                }
            }
        }

        private static void DrawDiamond(Image<Rgba32> img, int cx, int cy, int size, Rgba32 color)
        {
            for (int d = -size; d <= size; d++)
            {
                int span = size - Math.Abs(d);
                for (int dx = -span; dx <= span; dx++)
                {
                    SetPixelSafe(img, cx + dx, cy + d, color);
                }
            }
        }

        private static void DrawLine(Image<Rgba32> img, int x0, int y0, int x1, int y1, Rgba32 color)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
            int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;

            while (true)
            {
                SetPixelSafe(img, x0, y0, color);
                if (x0 == x1 && y0 == y1) break;

                int e2 = 2 * err;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        private static void DrawCross(Image<Rgba32> img, int cx, int cy, int size, Rgba32 color)
        {
            for (int d = -size; d <= size; d++)
            {
                SetPixelSafe(img, cx + d, cy, color);
                SetPixelSafe(img, cx, cy + d, color);
            }
        }

        private static Dictionary<string, object>? BuildTerrainSection(byte[]? walkableData, int bytesPerRow, int rows, int cols)
        {
            if (walkableData == null || bytesPerRow <= 0 || rows <= 0 || cols <= 0)
            {
                return null;
            }

            int minX = cols, maxX = 0, minY = rows, maxY = 0;
            bool anyWalkable = false;

            for (int y = 0; y < rows; y++)
            {
                for (int x = 0; x < cols; x++)
                {
                    int val = Pathfinding.GetCellValue(walkableData, bytesPerRow, x, y);
                    if (val > 0)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                        anyWalkable = true;
                    }
                }
            }

            if (!anyWalkable || maxX < minX || maxY < minY)
            {
                return null;
            }

            int cropW = maxX - minX + 1;
            int cropH = maxY - minY + 1;
            var croppedBytes = new byte[cropW * cropH];

            for (int y = 0; y < cropH; y++)
            {
                int srcY = minY + y;
                for (int x = 0; x < cropW; x++)
                {
                    int srcX = minX + x;
                    croppedBytes[(y * cropW) + x] = (byte)Pathfinding.GetCellValue(walkableData, bytesPerRow, srcX, srcY);
                }
            }

            return new Dictionary<string, object>
            {
                ["cropOrigin"] = new[] { minX, minY },
                ["cropWidth"] = cropW,
                ["cropHeight"] = cropH,
                ["pathfindingGrid"] = Convert.ToBase64String(croppedBytes),
            };
        }
    }
}
