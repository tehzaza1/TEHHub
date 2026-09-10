// <copyright file="BotVision.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Brain
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using GameHelper.RemoteEnums;
    using GameHelper.RemoteObjects.Components;
    using GameHelper.RemoteObjects.States.InGameStateObjects;
    using AutoExile2.Modes;
    using AutoExile2.Modes.Shared;
    using AutoExile2.Systems;

    /// <summary>
    /// Represents the "Eyes &amp; Senses" (ระบบดวงตาและประสาทสัมผัส) of the bot.
    /// Aggregates spatial hazard maps, exploration fog-of-war, entity recognition, and line-of-sight.
    /// </summary>
    public class BotVision
    {
        public HazardVision Hazard { get; } = new();

        public ExplorationVision Exploration { get; } = new();

        public EntityVision Entities { get; } = new();

        public SpatialVision Spatial { get; } = new();

        public LootVision Loot { get; } = new();

        public TelegraphVision Telegraph { get; } = new();

        /// <summary>
        /// Scans and populates all vision subsystems from the current BotContext and game state.
        /// </summary>
        public void Scan(
            BotContext ctx,
            Entity? leader,
            Vector2 leaderGrid,
            Entity? follower,
            Vector2 followerGrid,
            Vector2 leaderHeading,
            int followerAnimId,
            Vector2 formationTarget,
            int nearbyEnemies,
            float closestDist,
            Entity? bestTarget)
        {
            // 1. Scan Spatial LOS and distances
            this.Spatial.Scan(ctx, followerGrid, leaderGrid, formationTarget);

            // 2. Scan Entities (Leader, Follower, Hostiles, Environment)
            this.Entities.Scan(ctx, leader, leaderGrid, follower, followerGrid, leaderHeading, followerAnimId, nearbyEnemies, closestDist, bestTarget);

            // 3. Scan Hazards via ThreatMap
            this.Hazard.Scan(ctx, followerGrid);

            // 4. Scan Exploration via ExplorationMap
            this.Exploration.Scan(ctx, followerGrid);

            // 5. Scan Loot for fly-by pickup and attack-move
            this.Loot.Scan(ctx, followerGrid);

            // 6. Scan Telegraphs for incoming monster slams/charges
            this.Telegraph.Scan(ctx, followerGrid);
        }
    }

    /// <summary>
    /// Hazard &amp; Threat Vision: Detects danger zones, monster density, and incoming lethal threats.
    /// </summary>
    public class HazardVision
    {
        public float ThreatInProximity { get; private set; }

        public bool IsHighDangerArea { get; private set; }

        public Vector2? DensestMonsterPack { get; private set; }

        public Vector2? NearestMonsterCluster { get; private set; }

        public int TotalAliveTrackedMonsters { get; private set; }

        public const float HighDangerThreshold = 60f;

        public void Scan(BotContext ctx, Vector2 playerGrid)
        {
            var tm = ctx.ThreatMap;
            if (tm == null || !tm.IsInitialized)
            {
                this.ThreatInProximity = 0f;
                this.IsHighDangerArea = false;
                this.DensestMonsterPack = null;
                this.NearestMonsterCluster = null;
                this.TotalAliveTrackedMonsters = 0;
                return;
            }

            // Proximity threat within 25 grid units (~270 world units)
            this.ThreatInProximity = tm.GetThreatInRadius(playerGrid, 25f);
            this.IsHighDangerArea = this.ThreatInProximity >= HighDangerThreshold;
            this.DensestMonsterPack = tm.GetDensestAliveChunk(playerGrid, 15f);
            this.NearestMonsterCluster = tm.GetNearestAliveChunk(playerGrid, 15f);
            this.TotalAliveTrackedMonsters = tm.TotalAlive;
        }
    }

    /// <summary>
    /// Exploration Vision: Tracks fog-of-war coverage, unvisited frontiers, and map exits/transitions.
    /// </summary>
    public class ExplorationVision
    {
        public float MapCoverage { get; private set; }

        public Vector2? NextUnexploredTarget { get; private set; }

        public int DiscoveredTransitionsCount { get; private set; }

        public bool IsMapFullyExplored => this.MapCoverage >= 90f;

        public void Scan(BotContext ctx, Vector2 playerGrid)
        {
            var em = ctx.Exploration;
            if (em == null || !em.IsInitialized)
            {
                this.MapCoverage = 0f;
                this.NextUnexploredTarget = null;
                this.DiscoveredTransitionsCount = 0;
                return;
            }

            this.MapCoverage = em.Coverage;
            this.NextUnexploredTarget = em.GetNextExplorationTarget(playerGrid);
            this.DiscoveredTransitionsCount = em.KnownTransitions.Count;
        }
    }

    /// <summary>
    /// Entity Vision: Detailed observation of leader, self, nearby hostiles, and interactive objects.
    /// </summary>
    public class EntityVision
    {
        public Entity? LeaderEntity { get; private set; }

        public Vector2 LeaderGrid { get; private set; }

        public Vector2 LeaderHeading { get; private set; } = new Vector2(0, -1);

        public bool IsLeaderDead { get; private set; }

        public float LeaderHpPercent { get; private set; } = 100f;

        public bool IsLeaderSprinting { get; private set; }

        public Entity? FollowerEntity { get; private set; }

        public Vector2 FollowerGrid { get; private set; }

        public bool IsFollowerDead { get; private set; }

        public float FollowerHpPercent { get; private set; } = 100f;

        public float FollowerManaPercent { get; private set; } = 100f;

        public int FollowerAnimId { get; private set; }

        public int NearbyEnemyCount { get; private set; }

        public float ClosestEnemyDistance { get; private set; } = float.MaxValue;

        public Entity? BestCombatTarget { get; private set; }

        public bool HasDangerousRareOrBoss { get; private set; }

        public Entity? NearestPortal { get; private set; }

        public void Scan(
            BotContext ctx,
            Entity? leader,
            Vector2 leaderGrid,
            Entity? follower,
            Vector2 followerGrid,
            Vector2 leaderHeading,
            int followerAnimId,
            int nearbyEnemies,
            float closestDist,
            Entity? bestTarget)
        {
            this.LeaderEntity = leader;
            this.LeaderGrid = leaderGrid;
            this.LeaderHeading = leaderHeading;
            this.IsLeaderSprinting = ctx.CoopGamepad.IsLeaderSprinting;

            if (leader != null && leader.TryGetComponent<Life>(out var lLife) && lLife.Health.Total > 0)
            {
                this.IsLeaderDead = lLife.Health.Current <= 0;
                this.LeaderHpPercent = (float)lLife.Health.Current / lLife.Health.Total * 100f;
            }
            else
            {
                this.IsLeaderDead = false;
                this.LeaderHpPercent = 100f;
            }

            this.FollowerEntity = follower;
            this.FollowerGrid = followerGrid;
            this.FollowerAnimId = followerAnimId;

            if (follower != null && follower.TryGetComponent<Life>(out var fLife) && fLife.Health.Total > 0)
            {
                this.IsFollowerDead = fLife.Health.Current <= 0;
                this.FollowerHpPercent = (float)fLife.Health.Current / fLife.Health.Total * 100f;
                this.FollowerManaPercent = fLife.Mana.Total > 0 ? ((float)fLife.Mana.Current / fLife.Mana.Total * 100f) : 100f;
            }
            else
            {
                this.IsFollowerDead = false;
                this.FollowerHpPercent = 100f;
                this.FollowerManaPercent = 100f;
            }

            this.NearbyEnemyCount = nearbyEnemies;
            this.ClosestEnemyDistance = closestDist;
            this.BestCombatTarget = bestTarget;

            // Check for high-threat entities (Rare, Unique, Boss)
            this.HasDangerousRareOrBoss = false;
            if (bestTarget != null && bestTarget.TryGetComponent<ObjectMagicProperties>(out var omp))
            {
                this.HasDangerousRareOrBoss = omp.Rarity is Rarity.Rare or Rarity.Unique;
            }

            // Scan for exit portal within 60 units
            this.NearestPortal = ModeHelpers.FindNearestPortal(ctx.Area, followerGrid, 60f);
        }
    }

    /// <summary>
    /// Spatial &amp; Line-of-Sight Vision: Raycast collision checks and distance metrics.
    /// </summary>
    public class SpatialVision
    {
        public float DistanceToLeader { get; private set; }

        public float DistanceToFormation { get; private set; }

        public bool HasLosToLeader { get; private set; }

        public bool HasLosToFormation { get; private set; }

        public bool HasBlockingMonstersInPath { get; private set; }

        public void Scan(BotContext ctx, Vector2 followerGrid, Vector2 leaderGrid, Vector2 formationTarget)
        {
            this.DistanceToLeader = Vector2.Distance(followerGrid, leaderGrid);
            this.DistanceToFormation = Vector2.Distance(followerGrid, formationTarget);

            var area = ctx.Area;
            var walkable = area?.GridWalkableData;
            int bpr = area?.TerrainMetadata.BytesPerRow ?? 0;

            if (walkable != null && bpr > 0)
            {
                int rows = walkable.Length / bpr;
                int cols = bpr * 2;

                this.HasLosToFormation = Pathfinding.HasLineOfSight(walkable, bpr, followerGrid, formationTarget, rows, cols, 3);
                this.HasLosToLeader = Pathfinding.HasLineOfSight(walkable, bpr, followerGrid, leaderGrid, rows, cols, 3);
            }
            else
            {
                this.HasLosToFormation = true;
                this.HasLosToLeader = true;
            }

            // Corridor Monster Blocker Check (For Phasing Dodge Roll)
            this.HasBlockingMonstersInPath = false;
            Vector2 toTarget = formationTarget - followerGrid;
            float targetDist = toTarget.Length();
            if (targetDist > 4.5f && area?.AwakeEntities != null)
            {
                Vector2 moveDir = toTarget / targetDist;
                float checkDist = Math.Min(targetDist, 22f);

                foreach (var kvp in area.AwakeEntities)
                {
                    var ent = kvp.Value;
                    if (!ent.IsValid || ent.Address == ctx.Player.Address) continue;
                    if (!CombatSystem.IsHostileMonster(ent, ctx.Player.Address)) continue;
                    if (!ent.TryGetComponent<Render>(out var mRender)) continue;

                    var mPos = new Vector2(mRender.GridPosition.X, mRender.GridPosition.Y);
                    Vector2 toMonster = mPos - followerGrid;

                    // Projection along movement ray
                    float proj = Vector2.Dot(toMonster, moveDir);
                    if (proj >= 3.5f && proj <= checkDist)
                    {
                        // Perpendicular distance to movement line
                        Vector2 projPoint = followerGrid + (moveDir * proj);
                        float lateralDist = Vector2.Distance(mPos, projPoint);
                        if (lateralDist <= 4.5f)
                        {
                            this.HasBlockingMonstersInPath = true;
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Economic valuation tiers for items on the ground.
    /// Determines whether an item is worth picking up immediately or if it can wait until combat is cleared.
    /// </summary>
    public enum LootTier
    {
        /// <summary>Scrolls, scraps, common items - always wait during combat.</summary>
        Trash = 0,

        /// <summary>Minor currency (Transmutes, Augments) - can wait during combat.</summary>
        Low = 1,

        /// <summary>Mid-value (Alchemy, Regals, Vaal, good Rares) - grab if close &amp; safe.</summary>
        Medium = 2,

        /// <summary>High value (Chaos, Exalted, Greater Gems, Tier 15+ Maps, Uniques) - attack-move to grab.</summary>
        High = 3,

        /// <summary>Top value (Divine, Mirror, Tier 0/1 Uniques) - urgent priority.</summary>
        Godly = 4,
    }

    /// <summary>
    /// Loot Vision: Detects valuable dropped items on the ground within player proximity.
    /// Performs economic cost-benefit tradeoff to prevent rushing after low-value items during combat.
    /// </summary>
    public class LootVision
    {
        public Entity? NearestLootEntity { get; private set; }

        public Vector2? NearestLootPosition { get; private set; }

        public float NearestLootDistance { get; private set; } = float.MaxValue;

        public string NearestLootName { get; private set; } = string.Empty;

        public LootTier NearestLootTier { get; private set; } = LootTier.Trash;

        public double NearestLootEstimatedValue { get; private set; } = 0.0;

        public bool HasValuableLootNearby => this.NearestLootEntity != null && this.NearestLootDistance <= 45f;

        public bool CanPickupNow => this.NearestLootEntity != null && this.NearestLootDistance <= 7.5f;

        public int TotalLootCount { get; private set; }

        /// <summary>
        /// Evaluates whether the bot should rush to pick up the item now or wait:
        /// "สู้อยู่แต่ของไม่มีราคาหรือ ราคา แทบไม่มี นี้ก็ไม่ควร รีบไปเก็บมัน รอได้"
        /// If in active combat, items of Trash or Low value are deferred until threats are neutralized.
        /// </summary>
        public bool ShouldLootNow(bool inActiveCombat, float threatLevel)
        {
            if (this.NearestLootEntity == null)
            {
                return false;
            }

            // Not in active combat -> Safe to pick up anything
            if (!inActiveCombat)
            {
                return true;
            }

            // In active combat: evaluate cost-benefit tradeoff
            return this.NearestLootTier switch
            {
                LootTier.Trash or LootTier.Low => false, // ของไม่มีราคาหรือราคาแทบไม่มี ไม่ควรรีบไปเก็บ รอได้
                LootTier.Medium => this.NearestLootDistance <= 8f && threatLevel < 40f, // เก็บเฉพาะเมื่ออยู่ใต้ฝ่าเท้าและปลอดภัยพอ
                LootTier.High => threatLevel < 75f, // ของมีราคา (Chaos/Exalt) เดินยิงไปเก็บได้ แต่ไม่วิ่งเข้าดงระเบิดรุนแรง
                LootTier.Godly => true, // Divine / Mirror -> สำคัญสูงสุด
                _ => false,
            };
        }

        public static (LootTier Tier, double EstimatedValue) EvaluateItemValue(string path, string name, Rarity rarity)
        {
            string p = path.ToLowerInvariant();
            string n = name.ToLowerInvariant();

            // Godly Tier (Divine, Mirror, top tier)
            if (p.Contains("divine") || p.Contains("mirror") || n.Contains("divine") || n.Contains("mirror") || n.Contains("hinekora"))
            {
                return (LootTier.Godly, 160.0);
            }

            // High Tier (Chaos, Exalted, Greater Gems, valuable Maps, Uniques)
            if (p.Contains("chaos") || p.Contains("exalt") || p.Contains("greaterjeweller") ||
                p.Contains("perfectjeweller") || p.Contains("logbook") || rarity == Rarity.Unique ||
                n.Contains("chaos orb") || n.Contains("exalted orb"))
            {
                return (LootTier.High, 20.0);
            }

            // Medium Tier (Alchemy, Regal, Vaal, Chance, good Rares, Waystones)
            if (p.Contains("alchemy") || p.Contains("regal") || p.Contains("vaal") ||
                p.Contains("chance") || p.Contains("artificer") || p.Contains("waystone") ||
                rarity == Rarity.Rare || n.Contains("orb of alchemy") || n.Contains("regal orb"))
            {
                return (LootTier.Medium, 2.0);
            }

            // Low Tier (Transmutes, Augments, Alterations)
            if (p.Contains("transmut") || p.Contains("augment") || p.Contains("alteration") ||
                n.Contains("transmutation") || n.Contains("augmentation"))
            {
                return (LootTier.Low, 0.1);
            }

            // Trash Tier (Scrolls, Whetstones, Scraps, common items)
            return (LootTier.Trash, 0.01);
        }

        public void Scan(BotContext ctx, Vector2 playerGrid)
        {
            this.NearestLootEntity = null;
            this.NearestLootPosition = null;
            this.NearestLootDistance = float.MaxValue;
            this.NearestLootName = string.Empty;
            this.NearestLootTier = LootTier.Trash;
            this.NearestLootEstimatedValue = 0.0;
            this.TotalLootCount = 0;

            var area = ctx.Area;
            if (area?.AwakeEntities == null)
            {
                return;
            }

            foreach (var kvp in area.AwakeEntities)
            {
                var ent = kvp.Value;
                if (!ent.IsValid) continue;

                // Check if ground item
                if (!ent.TryGetComponent<WorldItem>(out var worldItem))
                {
                    continue;
                }

                if (!ent.TryGetComponent<Render>(out var render))
                {
                    continue;
                }

                var itemPos = new Vector2(render.GridPosition.X, render.GridPosition.Y);
                float dist = Vector2.Distance(playerGrid, itemPos);
                if (dist > 55f)
                {
                    continue;
                }

                string path = worldItem.ItemPath ?? ent.Path ?? string.Empty;
                string name = worldItem.ItemName;
                if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path))
                {
                    int lastSlash = path.LastIndexOf('/');
                    name = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
                }

                Rarity rarity = Rarity.Normal;
                if (worldItem.Item != null && worldItem.Item.TryGetComponent<Mods>(out var mods))
                {
                    rarity = mods.Rarity;
                }

                var (tier, estVal) = EvaluateItemValue(path, name, rarity);

                // Filter for eligible ground items
                bool isValuable = tier >= LootTier.Low ||
                    path.Contains("Currency") ||
                    path.Contains("DivinationCards") ||
                    path.Contains("Quest") ||
                    path.Contains("Maps") ||
                    path.Contains("Waystones") ||
                    path.Contains("Gems") ||
                    path.Contains("Jewels") ||
                    dist <= 12f;

                if (!isValuable)
                {
                    continue;
                }

                this.TotalLootCount++;
                if (dist < this.NearestLootDistance)
                {
                    this.NearestLootDistance = dist;
                    this.NearestLootEntity = ent;
                    this.NearestLootPosition = itemPos;
                    this.NearestLootName = name;
                    this.NearestLootTier = tier;
                    this.NearestLootEstimatedValue = estVal;
                }
            }
        }
    }

    /// <summary>
    /// Telegraph Vision: Observes enemy animation states to detect incoming heavy slams, charges,
    /// and telegraphed AoE attacks before they hit, enabling tactical micro-dodging while maintaining combat.
    /// </summary>
    public class TelegraphVision
    {
        public bool HasIncomingSlam { get; private set; }

        public Entity? SlamSourceEntity { get; private set; }

        public Vector2? SlamEpicenter { get; private set; }

        public Vector2 EvadeVector { get; private set; } = Vector2.Zero;

        public float DistanceToSlamSource { get; private set; } = float.MaxValue;

        public Animation SlamAnimation { get; private set; } = Animation.Idle;

        public void Scan(BotContext ctx, Vector2 playerGrid)
        {
            this.HasIncomingSlam = false;
            this.SlamSourceEntity = null;
            this.SlamEpicenter = null;
            this.EvadeVector = Vector2.Zero;
            this.DistanceToSlamSource = float.MaxValue;
            this.SlamAnimation = Animation.Idle;

            var area = ctx.Area;
            if (area?.AwakeEntities == null)
            {
                return;
            }

            foreach (var kvp in area.AwakeEntities)
            {
                var ent = kvp.Value;
                if (!ent.IsValid || ent.Address == ctx.Player.Address) continue;
                if (!CombatSystem.IsHostileMonster(ent, ctx.Player.Address)) continue;
                if (!ent.TryGetComponent<Render>(out var render)) continue;
                if (!ent.TryGetComponent<Actor>(out var actor)) continue;

                var mPos = new Vector2(render.GridPosition.X, render.GridPosition.Y);
                float dist = Vector2.Distance(playerGrid, mPos);
                if (dist > 30f) continue;

                var anim = actor.Animation;
                bool isSlamAnim = anim is Animation.GroundSlam
                    or Animation.LeapSlam
                    or Animation.LeapSlamNear
                    or Animation.MoltenCrash
                    or Animation.MoltenCrashNear
                    or Animation.MoltenCrashMoving
                    or Animation.MoltenCrashMovingNear
                    or Animation.ShapeshiftMoltenCrash
                    or Animation.ShapeshiftMoltenCrashNear
                    or Animation.Charge
                    or Animation.Cleave
                    or Animation.Stomp
                    or Animation.SpellAreaOfEffect
                    or Animation.SpellAreaOfEffectFire
                    or Animation.SpellAreaOfEffectCold
                    or Animation.SpellAreaOfEffectLightning
                    or Animation.SpellAreaOfEffectChaos
                    or Animation.Sweep;

                if (isSlamAnim)
                {
                    this.HasIncomingSlam = true;
                    this.SlamSourceEntity = ent;
                    this.SlamEpicenter = mPos;
                    this.DistanceToSlamSource = dist;
                    this.SlamAnimation = anim;

                    // Calculate evade vector:
                    // Primary: outward away from monster
                    Vector2 away = playerGrid - mPos;
                    if (away.LengthSquared() < 0.1f)
                    {
                        away = new Vector2(0, 1);
                    }

                    // Also add slight perpendicular angle (sidestep) so we don't roll in a straight line back into a linear beam/charge
                    var normAway = Vector2.Normalize(away);
                    var perpendicular = new Vector2(-normAway.Y, normAway.X);
                    this.EvadeVector = Vector2.Normalize(normAway + (perpendicular * 0.7f));
                    break;
                }
            }
        }
    }
}
