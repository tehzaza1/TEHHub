// <copyright file="BotRecorder.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2.Systems
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Numerics;
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    /// Rolling blackbox flight recorder ported from AutoExile.
    /// Captures per-tick snapshots in a 600-tick ring buffer (~10-15s).
    /// Automatically dumps incident timelines on Death, Massive HP Drop, or Severe Stuck.
    /// </summary>
    public class BotRecorder
    {
        private const int BufferSize = 600;
        private const int DumpCooldownMs = 4000;

        private readonly TickSnapshot[] buffer = new TickSnapshot[BufferSize];
        private readonly object bufferLock = new();

        private int writeIndex = 0;
        private int count = 0;
        private long tickNumber = 0;

        private float lastHpPercent = 1f;
        private bool lastIsAlive = true;
        private DateTime lastDumpTime = DateTime.MinValue;

        public long CurrentTick => this.tickNumber;
        public string LastDumpStatus { get; private set; } = string.Empty;

        /// <summary>
        /// Records one tick of bot execution.
        /// </summary>
        public void RecordTick(
            AreaInstance? area,
            string areaName,
            Vector2 playerGrid,
            int playerHp,
            int playerMaxHp,
            int playerMana,
            int playerMaxMana,
            bool isAlive,
            string modeName,
            string currentState,
            string currentAction,
            CombatSystem combatSystem,
            ExplorationMap explorationMap,
            List<Vector2> currentNavPath,
            int currentWaypointIndex,
            Vector2? currentDestination,
            float stuckTimer,
            float deltaTime)
        {
            this.tickNumber++;

            float hpPercent = playerMaxHp > 0 ? (float)playerHp / playerMaxHp : 1f;
            float manaPercent = playerMaxMana > 0 ? (float)playerMana / playerMaxMana : 1f;

            // Collect top nearby threats (alive hostiles within 90 grid units)
            var threats = new List<RecordedThreat>();
            if (area != null)
            {
                try
                {
                    int threatCount = 0;
                    foreach (var entity in area.AwakeEntities.Values)
                    {
                        if (!entity.IsValid) continue;
                        if (entity.EntityType != TEHhub.RemoteEnums.Entity.EntityTypes.Monster) continue;

                        if (entity.TryGetComponent<TEHhub.RemoteObjects.Components.Positioned>(out var pos) && pos.IsFriendly) continue;

                        Vector2 entGrid = Vector2.Zero;
                        if (entity.TryGetComponent<TEHhub.RemoteObjects.Components.Render>(out var r))
                        {
                            entGrid = new Vector2(r.GridPosition.X, r.GridPosition.Y);
                        }

                        float dist = Vector2.Distance(playerGrid, entGrid);
                        if (dist > 90f) continue;

                        int eCurHp = 0;
                        bool eAlive = false;
                        if (entity.TryGetComponent<TEHhub.RemoteObjects.Components.Life>(out var eLife))
                        {
                            eCurHp = eLife.Health.Current;
                            eAlive = eCurHp > 0;
                        }

                        if (!eAlive) continue;

                        string rarity = "Normal";
                        if (entity.TryGetComponent<TEHhub.RemoteObjects.Components.ObjectMagicProperties>(out var omp))
                        {
                            rarity = omp.Rarity.ToString();
                        }

                        string path = entity.Path ?? "";
                        string name = path.Contains('/') ? path.Substring(path.LastIndexOf('/') + 1) : path;

                        threats.Add(new RecordedThreat
                        {
                            Id = entity.Id,
                            Name = name,
                            GridX = Math.Round(entGrid.X, 1),
                            GridY = Math.Round(entGrid.Y, 1),
                            Distance = Math.Round(dist, 1),
                            Rarity = rarity,
                            Hp = eCurHp,
                        });

                        threatCount++;
                        if (threatCount >= 15) break;
                    }
                }
                catch { }
            }

            var snap = new TickSnapshot
            {
                Tick = this.tickNumber,
                Time = DateTime.Now.ToString("HH:mm:ss.fff"),
                DeltaSec = (float)Math.Round(deltaTime, 3),
                PlayerX = Math.Round(playerGrid.X, 1),
                PlayerY = Math.Round(playerGrid.Y, 1),
                HpPercent = (float)Math.Round(hpPercent * 100f, 1),
                ManaPercent = (float)Math.Round(manaPercent * 100f, 1),
                IsAlive = isAlive,
                Mode = modeName,
                State = currentState,
                Action = currentAction,
                InCombat = combatSystem.NearbyHostileCount > 0,
                TargetId = combatSystem.CurrentTargetId,
                NearbyHostiles = combatSystem.NearbyHostileCount,
                ClosestHostileDist = (float)Math.Round(combatSystem.ClosestHostileDistance, 1),
                ExplorationCoverage = (float)Math.Round(explorationMap.Coverage, 1),
                NavWaypointIndex = currentWaypointIndex,
                NavPathLength = currentNavPath.Count,
                DestX = currentDestination.HasValue ? Math.Round(currentDestination.Value.X, 1) : null,
                DestY = currentDestination.HasValue ? Math.Round(currentDestination.Value.Y, 1) : null,
                StuckSec = (float)Math.Round(stuckTimer, 1),
                Threats = threats,
            };

            lock (this.bufferLock)
            {
                this.buffer[this.writeIndex] = snap;
                this.writeIndex = (this.writeIndex + 1) % BufferSize;
                if (this.count < BufferSize) this.count++;
            }

            // Check automated blackbox dump triggers
            this.CheckIncidentTriggers(areaName, hpPercent, isAlive, stuckTimer);

            this.lastHpPercent = hpPercent;
            this.lastIsAlive = isAlive;
        }

        /// <summary>
        /// Checks automated dump triggers (Death, HP Spike, Chronic Stuck).
        /// </summary>
        private void CheckIncidentTriggers(string areaName, float curHpPercent, bool curIsAlive, float stuckTimer)
        {
            if ((DateTime.Now - this.lastDumpTime).TotalMilliseconds < DumpCooldownMs)
            {
                return;
            }

            // 1. Death Trigger: was alive, now dead or 0 HP
            if (this.lastIsAlive && (!curIsAlive || (curHpPercent <= 0.001f && this.lastHpPercent > 0.01f)))
            {
                this.DumpToFile("DEATH", areaName);
                return;
            }

            // 2. Severe HP Spike: lost more than 35% HP in recent frames
            float hpDrop = this.lastHpPercent - curHpPercent;
            if (hpDrop >= 0.38f && curIsAlive)
            {
                this.DumpToFile("HP_SPIKE", areaName);
                return;
            }

            // 3. Chronic Stuck Trigger: stuck in same spot for >= 3.0s
            if (stuckTimer >= 3.0f)
            {
                this.DumpToFile("STUCK", areaName);
                return;
            }
        }

        /// <summary>
        /// Forces a recording dump to file with a custom reason (e.g. from hotkey F6/F7 or manual request).
        /// </summary>
        public string ForceDump(string reason, string areaName)
        {
            return this.DumpToFile(reason, areaName);
        }

        private string DumpToFile(string reason, string areaName)
        {
            this.lastDumpTime = DateTime.Now;

            List<TickSnapshot> ordered;
            lock (this.bufferLock)
            {
                if (this.count == 0) return "No recording data available";

                ordered = new List<TickSnapshot>(this.count);
                int start = this.count < BufferSize ? 0 : this.writeIndex;
                for (int i = 0; i < this.count; i++)
                {
                    int idx = (start + i) % BufferSize;
                    if (this.buffer[idx] != null)
                    {
                        ordered.Add(this.buffer[idx]);
                    }
                }
            }

            try
            {
                string outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Dumps");
                Directory.CreateDirectory(outputDir);

                string fileName;
                if (string.Equals(reason, "MANUAL", StringComparison.OrdinalIgnoreCase))
                {
                    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    string safeArea = Regex.Replace(string.IsNullOrWhiteSpace(areaName) ? "Unknown" : areaName, @"[^a-zA-Z0-9_\-]", "_");
                    fileName = $"Recording_MANUAL_{safeArea}_{timestamp}.json";
                }
                else
                {
                    fileName = "Recording_Latest.json";
                }

                string filePath = Path.Combine(outputDir, fileName);

                var dump = new
                {
                    incident = reason,
                    recordedAt = DateTime.Now.ToString("o"),
                    area = string.Equals(reason, "MANUAL", StringComparison.OrdinalIgnoreCase) ? areaName : null,
                    totalTicks = ordered.Count,
                    durationSeconds = Math.Round(ordered.Sum(s => s.DeltaSec), 2),
                    timeline = ordered,
                };

                string json = JsonSerializer.Serialize(dump, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                });

                Task.Run(() =>
                {
                    try
                    {
                        File.WriteAllText(filePath, json);

                        // If auto dump, remove old historical auto dumps to keep only the latest
                        if (!string.Equals(reason, "MANUAL", StringComparison.OrdinalIgnoreCase))
                        {
                            foreach (var oldFile in Directory.GetFiles(outputDir, "Recording_*.json"))
                            {
                                var name = Path.GetFileName(oldFile);
                                if (!name.Equals("Recording_Latest.json", StringComparison.OrdinalIgnoreCase) &&
                                    !name.StartsWith("Recording_MANUAL_", StringComparison.OrdinalIgnoreCase))
                                {
                                    try { File.Delete(oldFile); } catch { }
                                }
                            }
                        }
                    }
                    catch { }
                });

                this.LastDumpStatus = $"Recording dumped: {fileName} ({ordered.Count} ticks, {reason})";
                Console.WriteLine($"[AutoExile2] {this.LastDumpStatus}");
                return this.LastDumpStatus;
            }
            catch (Exception ex)
            {
                this.LastDumpStatus = $"Recording dump failed: {ex.Message}";
                return this.LastDumpStatus;
            }
        }
    }

    public class TickSnapshot
    {
        public long Tick { get; set; }
        public string Time { get; set; } = "";
        public float DeltaSec { get; set; }
        public double PlayerX { get; set; }
        public double PlayerY { get; set; }
        public float HpPercent { get; set; }
        public float ManaPercent { get; set; }
        public bool IsAlive { get; set; }
        public string Mode { get; set; } = "";
        public string State { get; set; } = "";
        public string Action { get; set; } = "";
        public bool InCombat { get; set; }
        public uint TargetId { get; set; }
        public int NearbyHostiles { get; set; }
        public float ClosestHostileDist { get; set; }
        public float ExplorationCoverage { get; set; }
        public int NavWaypointIndex { get; set; }
        public int NavPathLength { get; set; }
        public double? DestX { get; set; }
        public double? DestY { get; set; }
        public float StuckSec { get; set; }
        public List<RecordedThreat> Threats { get; set; } = new();
    }

    public class RecordedThreat
    {
        public uint Id { get; set; }
        public string Name { get; set; } = "";
        public double GridX { get; set; }
        public double GridY { get; set; }
        public double Distance { get; set; }
        public string Rarity { get; set; } = "";
        public int Hp { get; set; }
    }
}
