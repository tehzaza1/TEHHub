namespace myFarming
{
    using System;
    using System.Collections.Generic;
    using System.Numerics;
    using TEHhub;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteEnums.Entity;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public sealed class KillTracker
    {
        private readonly HashSet<uint> deadEntityIds = new();
        private readonly Dictionary<uint, MonsterState> previousMonsters = new();

        public int KillsNormal { get; private set; }
        public int KillsMagic { get; private set; }
        public int KillsRare { get; private set; }
        public int KillsUnique { get; private set; }
        public int KillsTotal => this.KillsNormal + this.KillsMagic + this.KillsRare + this.KillsUnique;

        private struct MonsterState
        {
            public Rarity Rarity;
            public Vector2 Position;
        }

        public void Reset()
        {
            this.deadEntityIds.Clear();
            this.previousMonsters.Clear();
            this.KillsNormal = 0;
            this.KillsMagic = 0;
            this.KillsRare = 0;
            this.KillsUnique = 0;
        }

        public void Update(AreaInstance? area, bool inTownOrHideout)
        {
            if (area?.AwakeEntities == null || inTownOrHideout) return;

            var player = area.Player;
            Vector2 playerPos = Vector2.Zero;
            if (player != null && player.TryGetComponent<Render>(out var pR) && pR != null)
            {
                playerPos = new Vector2(pR.GridPosition.X, pR.GridPosition.Y);
            }

            var currentIds = new HashSet<uint>();

            foreach (var entity in area.AwakeEntities.Values)
            {
                if (entity == null || entity.Id == 0) continue;
                if (entity.EntityType != EntityTypes.Monster) continue;

                currentIds.Add(entity.Id);

                if (this.deadEntityIds.Contains(entity.Id)) continue;

                var rarity = Rarity.Normal;
                if (entity.TryGetComponent<ObjectMagicProperties>(out var omp) && omp != null)
                {
                    rarity = omp.Rarity;
                }
                else if (entity.TryGetComponent<Mods>(out var mods) && mods != null)
                {
                    rarity = mods.Rarity;
                }

                Vector2 monsterPos = Vector2.Zero;
                if (entity.TryGetComponent<Render>(out var render) && render != null)
                {
                    monsterPos = new Vector2(render.GridPosition.X, render.GridPosition.Y);
                }

                // Check if monster is dead via Life component
                if (entity.TryGetComponent<Life>(out var life) && life != null)
                {
                    if (life.Health.Current <= 0 && life.Health.Total > 0)
                    {
                        this.deadEntityIds.Add(entity.Id);
                        this.AddKill(rarity);
                        continue;
                    }
                }

                this.previousMonsters[entity.Id] = new MonsterState
                {
                    Rarity = rarity,
                    Position = monsterPos,
                };
            }

            // Check monsters that disappeared close to player (died and despawned)
            var toRemove = new List<uint>();
            foreach (var kvp in this.previousMonsters)
            {
                if (!currentIds.Contains(kvp.Key))
                {
                    if (!this.deadEntityIds.Contains(kvp.Key))
                    {
                        float dist = Vector2.Distance(kvp.Value.Position, playerPos);
                        if (dist < 120f)
                        {
                            this.AddKill(kvp.Value.Rarity);
                            this.deadEntityIds.Add(kvp.Key);
                        }
                    }
                    toRemove.Add(kvp.Key);
                }
            }

            foreach (var id in toRemove)
            {
                this.previousMonsters.Remove(id);
            }
        }

        private void AddKill(Rarity rarity)
        {
            switch (rarity)
            {
                case Rarity.Magic:
                    this.KillsMagic++;
                    break;
                case Rarity.Rare:
                    this.KillsRare++;
                    break;
                case Rarity.Unique:
                    this.KillsUnique++;
                    break;
                default:
                    this.KillsNormal++;
                    break;
            }
        }
    }
}
