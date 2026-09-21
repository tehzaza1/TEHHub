// <copyright file="AutoExile2Settings.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace AutoExile2
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using ClickableTransparentOverlay.Win32;
    using TEHhub.Plugin;

    /// <summary>
    /// Operating modes for AutoExile 2 bot.
    /// </summary>
    public enum AutoExileMode
    {
        MapFarm = 0,
        WaveFarm = 0,
        Follower = 1,
        Boss = 2,
        Idle = 3,
    }

    /// <summary>
    /// Type of input to use for attack.
    /// </summary>
    public enum AttackInputType
    {
        MouseRight = 0,
        MouseLeft = 1,
        KeyboardKey = 2,
        MouseMiddle = 3,
    }

    /// <summary>
    /// Combat positioning style.
    /// </summary>
    public enum CombatStyle
    {
        Melee = 0,
        Ranged = 1,
    }

    /// <summary>
    /// Relative formation position for Player 2 (Follower) relative to Player 1 (Leader).
    /// </summary>
    public enum CoopFollowerPosition
    {
        Back = 0,
        Forward = 1,
        BackLeft = 2,
        BackRight = 3,
        ForwardLeft = 4,
        ForwardRight = 5,
        BackHalf = 6,
        ForwardHalf = 7,
        LeftWing = 8,
        RightWing = 9,
        BackLeftQuad = 10,
        BackRightQuad = 11,
    }

    /// <summary>
    /// Settings for the AutoExile 2 bot plugin.
    /// </summary>
    public sealed class AutoExile2Settings : IPSettings
    {
        /// <summary>Runtime-only master switch to enable the bot. Never persisted across sessions.</summary>
        [JsonIgnore]
        public bool IsRunning = false;

        /// <summary>Active operating mode (default: MapFarm).</summary>
        public AutoExileMode Mode = AutoExileMode.MapFarm;

        /// <summary>Hotkey to toggle the bot on/off.</summary>
        public VK ToggleKey = VK.INSERT;

        /// <summary>Hotkey to dump game state (terrain, monsters, exploration) to PNG and JSON.</summary>
        public VK DumpKey = VK.F6;

        /// <summary>Movement key W (Up).</summary>
        public VK MoveUp = VK.KEY_W;

        /// <summary>Movement key A (Left).</summary>
        public VK MoveLeft = VK.KEY_A;

        /// <summary>Movement key S (Down).</summary>
        public VK MoveDown = VK.KEY_S;

        /// <summary>Movement key D (Right).</summary>
        public VK MoveRight = VK.KEY_D;

        /// <summary>Enable sprint / dodge roll while exploring to travel faster.</summary>
        public bool UseSprint = true;

        /// <summary>Sprint key (Spacebar in PoE 2).</summary>
        public VK SprintKey = VK.SPACE;

        /// <summary>Minimum distance to target waypoint before engaging sprint.</summary>
        public float SprintMinDistance = 40f;

        /// <summary>Hotkey to open town portal (Default: B in PoE 2).</summary>
        public VK PortalKey = VK.KEY_B;

        /// <summary>Name of the stash tab AutoExile2 will use for Waystones.</summary>
        public string WaystoneTab = string.Empty;

        /// <summary>Name of the stash tab AutoExile2 will use for crafting currency.</summary>
        public string CurrencyTab = string.Empty;

        /// <summary>Lowest Waystone Tier AutoExile2 may select when opening a map.</summary>
        public int MinTier = 1;

        /// <summary>Highest Waystone Tier AutoExile2 may select when opening a map.</summary>
        public int MaxTier = 16;

        /// <summary>Minimum Item Rarity percentage. Null disables the filter; equality passes.</summary>
        public int? MinWaystoneItemRarity;

        /// <summary>Minimum Pack Size percentage. Null disables the filter; equality passes.</summary>
        public int? MinWaystonePackSize;

        /// <summary>Minimum Monster Rarity percentage. Null disables the filter; equality passes.</summary>
        public int? MinWaystoneMonsterRarity;

        /// <summary>Minimum Monster Effectiveness percentage. Null disables the filter; equality passes.</summary>
        public int? MinWaystoneMonsterEffectiveness;

        /// <summary>Minimum Waystone Drop Chance percentage. Null disables the filter; equality passes.</summary>
        public int? MinWaystoneDropChance;

        /// <summary>Target explicit modifier count after crafting (4-6). Defaults to the PoE2 cap of six.</summary>
        public int MaxWaystoneMods = 6;

        /// <summary>Blocked Waystone modifier family keys. Every unlisted family remains allowed.</summary>
        public List<string> BlockedWaystoneMods = new();

        /// <summary>Name of the stash tab AutoExile2 will use for farmed-item dumping.</summary>
        public string DumpTab = string.Empty;

        /// <summary>Primary attack input type.</summary>
        public AttackInputType PrimaryAttackType = AttackInputType.MouseRight;

        /// <summary>Primary attack keyboard key if KeyboardKey selected.</summary>
        public VK PrimaryAttackKey = VK.KEY_Q;

        /// <summary>Attack hold duration in milliseconds.</summary>
        public int AttackHoldDurationMs = 150;

        /// <summary>Cooldown between attack bursts in milliseconds.</summary>
        public int AttackCooldownMs = 250;

        /// <summary>Distance in grid units to engage hostiles.</summary>
        public float CombatRange = 65f;

        /// <summary>Combat positioning style (Melee or Ranged).</summary>
        public CombatStyle CombatStyle = CombatStyle.Ranged;

        /// <summary>Preferred fighting distance from monsters in grid units.</summary>
        public float FightRange = 45f;

        /// <summary>Minimum monster density to slow down exploration and engage pack.</summary>
        public int MinPackDensity = 3;

        /// <summary>Enable secondary skill for Rares and Uniques.</summary>
        public bool UseSecondaryAttack = false;

        /// <summary>Secondary attack input type.</summary>
        public AttackInputType SecondaryAttackType = AttackInputType.KeyboardKey;

        /// <summary>Secondary attack key.</summary>
        public VK SecondaryAttackKey = VK.KEY_E;

        /// <summary>
        /// Individually configured skill slots (AutoExile System).
        /// Each slot has its own role, key, priority, target filter, cooldown, and conditions.
        /// </summary>
        public List<SkillSlotConfig> Skills { get; set; } = SkillSlotConfig.GetDefaultSlots();

        /// <summary>Enable automatic life flask usage when low on HP.</summary>
        public bool AutoLifeFlask = true;

        /// <summary>Life flask hotkey (Default: 1 in PoE 2).</summary>
        public VK LifeFlaskKey = VK.KEY_1;

        /// <summary>Life flask HP percentage trigger threshold.</summary>
        public float LifeFlaskThresholdPercent = 50f;

        /// <summary>Debounce cooldown between life flask presses in milliseconds (Default: 200ms).</summary>
        public int LifeFlaskCooldownMs = 200;

        /// <summary>Enable automatic mana flask usage when low on mana.</summary>
        public bool AutoManaFlask = true;

        /// <summary>Mana flask hotkey (Default: 2 in PoE 2).</summary>
        public VK ManaFlaskKey = VK.KEY_2;

        /// <summary>Mana flask percentage trigger threshold.</summary>
        public float ManaFlaskThresholdPercent = 30f;

        /// <summary>Debounce cooldown between mana flask presses in milliseconds (Default: 200ms).</summary>
        public int ManaFlaskCooldownMs = 200;

        /// <summary>Check if flask effect is currently active on the player (do not waste flask if already drinking).</summary>
        public bool CheckFlaskActiveEffect = true;

        /// <summary>Check if flask has enough charges before attempting to drink.</summary>
        public bool CheckFlaskCharges = true;

        /// <summary>Leader character name in couch co-op. Player 2 follows Player 1 only when this name matches Player 1.</summary>
        public string FollowerCharacterName = string.Empty;

        /// <summary>Party leader character name to follow in Follower mode (alias for FollowerCharacterName).</summary>
        public string FollowerLeaderName
        {
            get => this.FollowerCharacterName;
            set => this.FollowerCharacterName = value;
        }

        /// <summary>Distance to leader before following in Follower mode.</summary>
        public float FollowDistance = 25f;

        /// <summary>Stop follow distance in Follower mode.</summary>
        public float FollowStopDistance = 15f;

        /// <summary>Enable combat support in Follower mode.</summary>
        public bool FollowerEnableCombat = true;

        // ═════════════════════════════════════════════════════════════════════════════
        // Couch Co-op Dual Gamepad System Settings
        // ═════════════════════════════════════════════════════════════════════════════

        /// <summary>Physical Gamepad XInput Slot index (Default: 0).</summary>
        public int CoopPhysicalPadIndex = 0;

        /// <summary>Player 1 (Leader): Auto trigger Life Flask (LB) on virtual controller #1.</summary>
        public bool P1AutoLifeFlask = true;

        /// <summary>Player 1 (Leader): Life Flask trigger threshold percent.</summary>
        public float P1LifeFlaskThresholdPercent = 50f;

        /// <summary>Player 1 (Leader): Auto trigger Mana Flask (LT) on virtual controller #1.</summary>
        public bool P1AutoManaFlask = true;

        /// <summary>Player 1 (Leader): Mana Flask trigger threshold percent.</summary>
        public float P1ManaFlaskThresholdPercent = 30f;

        /// <summary>Player 1 (Leader): Auto Buff &amp; Guard skills injected into controller #1.</summary>
        public List<SkillSlotConfig> P1Skills { get; set; } = SkillSlotConfig.GetDefaultP1Skills();

        /// <summary>Player 2 (Follower): Follow Trigger Distance (starts moving towards leader when distance exceeds this).</summary>
        public float CoopFollowDistance = 20f;

        /// <summary>Player 2 (Follower): Relative formation position (Position Helper).</summary>
        public CoopFollowerPosition FollowerPosition { get; set; } = CoopFollowerPosition.Back;

        /// <summary>Player 2 (Follower): Safe Distance (stops moving and stays peacefully/attacks when within this distance).</summary>
        public float CoopStopDistance = 10f;

        /// <summary>Player 2 (Follower): Distance before pressing B (Sprint / Dash).</summary>
        public float CoopSprintDistance = 50f;

        /// <summary>Position relative to leader's rotation (true = rotates with leader heading, false = fixed world direction).</summary>
        public bool PositionRelativeToLeaderRotation { get; set; } = false;

        /// <summary>Reduce body blocking by repelling follower away from leader if too close.</summary>
        public bool ReduceBodyBlocking { get; set; } = true;

        /// <summary>Body blocking repulsion threshold in Grid units (default 14g ~ 150w).</summary>
        public float BodyBlockingRepulsion { get; set; } = 14f;

        /// <summary>Heading smoothing frame count (default 6).</summary>
        public int HeadingSmoothing { get; set; } = 6;

        /// <summary>Player 2 (Follower): Auto trigger Life Flask (LB) on virtual controller #2.</summary>
        public bool P2AutoLifeFlask = true;

        /// <summary>Player 2 (Follower): Life Flask trigger threshold percent.</summary>
        public float P2LifeFlaskThresholdPercent = 50f;

        /// <summary>Player 2 (Follower): Auto trigger Mana Flask (LT) on virtual controller #2.</summary>
        public bool P2AutoManaFlask = true;

        /// <summary>Player 2 (Follower): Mana Flask trigger threshold percent.</summary>
        public float P2ManaFlaskThresholdPercent = 30f;

        /// <summary>Player 2 (Follower): Enable autonomous combat (Right stick aim + skills).</summary>
        public bool P2EnableCombat = true;

        /// <summary>
        /// Player 2 (Follower): Autonomous skills on virtual controller #2.
        /// Normal follower combat and Culling are configured per skill via SkillRole.
        /// </summary>
        public List<SkillSlotConfig> P2Skills { get; set; } = SkillSlotConfig.GetDefaultP2Skills();

        /// <summary>Draw path and status overlay on screen.</summary>
        public bool ShowOverlay = true;

        /// <summary>Draw distance / range circles around character in-game.</summary>
        public bool ShowDistanceCircles = true;

        /// <summary>Enable embedded web dashboard.</summary>
        public bool EnableWebServer = true;

        /// <summary>Web server port.</summary>
        public int WebServerPort = 9876;

        /// <summary>Allow external devices on local network to connect.</summary>
        public bool WebServerNetworkAccess = true;
    }
}
