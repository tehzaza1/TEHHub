namespace myFarming
{
    using System;
    using System.Collections.Generic;
    using TEHhub;
    using TEHhub.RemoteEnums;
    using TEHhub.RemoteEnums.Entity;
    using TEHhub.RemoteObjects.Components;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    public sealed class KillTracker
    {
        private readonly HashSet<uint> deadEntityIds = new();
        private readonly Dictionary<uint, Rarity> knownAliveMonsters = new();

        public int KillsNormal { get; private set; }
        public int KillsMagic { get; private set; }
        public int KillsRare { get; private set; }
        public int KillsUnique { get; private set; }
        public int KillsTotal => this.KillsNormal + this.KillsMagic + this.KillsRare + this.KillsUnique;

        public void Reset()
        {
            this.deadEntityIds.Clear();
            this.knownAliveMonsters.Clear();
            this.KillsNormal = 0;
            this.KillsMagic = 0;
            this.KillsRare = 0;
            this.KillsUnique = 0;
        }

        public void Update(AreaInstance? area, bool inTownOrHideout)
        {
            if (area?.AwakeEntities == null || inTownOrHideout) return;

            foreach (var entity in area.AwakeEntities.Values)
            {
                if (entity == null || entity.Id == 0) continue;
                if (entity.EntityType != EntityTypes.Monster) continue;

                // Skip friendly minions, summons, pets, and allies
                if (entity.EntityState == EntityStates.MonsterFriendly) continue;
                if (entity.TryGetComponent<Positioned>(out var pos) && pos.IsFriendly) continue;
                if (entity.Path != null && (entity.Path.Contains("/Minions/", StringComparison.OrdinalIgnoreCase) ||
                                            entity.Path.Contains("/Pets/", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // If already counted as dead for this map, ignore
                if (this.deadEntityIds.Contains(entity.Id)) continue;

                if (!entity.TryGetComponent<Life>(out var life) || life == null || life.Health.Total <= 0)
                {
                    continue;
                }

                // Resolve rarity
                var rarity = Rarity.Normal;
                if (entity.TryGetComponent<ObjectMagicProperties>(out var omp) && omp != null)
                {
                    rarity = omp.Rarity;
                }
                else if (entity.TryGetComponent<Mods>(out var mods) && mods != null)
                {
                    rarity = mods.Rarity;
                }

                bool isAlive = life.IsAlive && life.Health.Current > 0;
                bool isDead = !life.IsAlive || life.Health.Current <= 0 || entity.EntityState == EntityStates.Useless;

                if (isAlive)
                {
                    // Record monster as alive
                    this.knownAliveMonsters[entity.Id] = rarity;
                }
                else if (isDead)
                {
                    // Only count as a kill if we witnessed this monster alive in this map run
                    if (this.knownAliveMonsters.TryGetValue(entity.Id, out var trackedRarity))
                    {
                        this.deadEntityIds.Add(entity.Id);
                        this.knownAliveMonsters.Remove(entity.Id);
                        this.AddKill(trackedRarity);
                    }
                }
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

