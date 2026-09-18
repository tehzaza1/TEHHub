namespace TEHhub.OffsetDoctor.Manifest;

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TEHhub.Offsets.Natives;
using TEHhub.Offsets.Objects;
using TEHhub.Offsets.Objects.Components;
using TEHhub.Offsets.Objects.States;
using TEHhub.Offsets.Objects.States.InGameState;

public static class OffsetManifest
{
    public static List<OffsetNode> CreateFullRepositoryManifest()
    {
        return
        [
            // =========================================================================
            // CATEGORY 1: Static Roots
            // =========================================================================
            new OffsetNode
            {
                Id = "pattern_game_states",
                DisplayName = "Game States Pattern",
                Category = "Static Roots",
                ParentId = null,
                DefaultOffset = 0,
                Kind = ValueKind.StaticPattern,
                StaticPatternName = "Game States",
                ProductionSourceLocation = "TEHhub.Offsets/StaticOffsetsPatterns.cs"
            },
            new OffsetNode
            {
                Id = "pattern_file_root",
                DisplayName = "File Root Pattern",
                Category = "Static Roots",
                ParentId = null,
                DefaultOffset = 0,
                Kind = ValueKind.StaticPattern,
                StaticPatternName = "File Root",
                ProductionSourceLocation = "TEHhub.Offsets/StaticOffsetsPatterns.cs"
            },
            new OffsetNode
            {
                Id = "pattern_area_change",
                DisplayName = "AreaChangeCounter Pattern",
                Category = "Static Roots",
                ParentId = null,
                DefaultOffset = 0,
                Kind = ValueKind.StaticPattern,
                StaticPatternName = "AreaChangeCounter",
                ProductionSourceLocation = "TEHhub.Offsets/StaticOffsetsPatterns.cs"
            },
            new OffsetNode
            {
                Id = "pattern_terrain_rotator",
                DisplayName = "Terrain Rotator Helper Pattern",
                Category = "Static Roots",
                ParentId = null,
                DefaultOffset = 0,
                Kind = ValueKind.StaticPattern,
                StaticPatternName = "Terrain Rotator Helper",
                ProductionSourceLocation = "TEHhub.Offsets/StaticOffsetsPatterns.cs"
            },
            new OffsetNode
            {
                Id = "pattern_terrain_rotation_selector",
                DisplayName = "Terrain Rotation Selector Pattern",
                Category = "Static Roots",
                ParentId = null,
                DefaultOffset = 0,
                Kind = ValueKind.StaticPattern,
                StaticPatternName = "Terrain Rotation Selector",
                ProductionSourceLocation = "TEHhub.Offsets/StaticOffsetsPatterns.cs"
            },
            new OffsetNode
            {
                Id = "pattern_game_cull_size",
                DisplayName = "GameCullSize Pattern",
                Category = "Static Roots",
                ParentId = null,
                DefaultOffset = 0,
                Kind = ValueKind.StaticPattern,
                StaticPatternName = "GameCullSize",
                ProductionSourceLocation = "TEHhub.Offsets/StaticOffsetsPatterns.cs"
            },

            // =========================================================================
            // CATEGORY 2: Game States
            // =========================================================================
            new OffsetNode
            {
                Id = "game_state_root",
                DisplayName = "GameState Static Root",
                Category = "Game States",
                ParentId = "pattern_game_states",
                DefaultOffset = Marshal.OffsetOf<GameStateStaticOffset>(nameof(GameStateStaticOffset.GameState)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(GameStateStaticOffset),
                FieldName = nameof(GameStateStaticOffset.GameState),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/GameStateOffsets.cs"
            },
            new OffsetNode
            {
                Id = "game_state_current_state",
                DisplayName = "CurrentStatePtr StdVector",
                Category = "Game States",
                ParentId = "game_state_root",
                DefaultOffset = Marshal.OffsetOf<GameStateOffset>(nameof(GameStateOffset.CurrentStatePtr)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = 8,
                VectorIsPointerElements = true,
                StructTypeName = nameof(GameStateOffset),
                FieldName = nameof(GameStateOffset.CurrentStatePtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/GameStateOffsets.cs"
            },
            new OffsetNode
            {
                Id = "game_state_area_loading",
                DisplayName = "AreaLoadingState (States[0].X)",
                Category = "Game States",
                ParentId = "game_state_root",
                DefaultOffset = Marshal.OffsetOf<GameStateOffset>(nameof(GameStateOffset.States)).ToInt32() + 0 * Unsafe.SizeOf<StdTuple2D<IntPtr>>(),
                Kind = ValueKind.PointerField,
                IsOptionalStateDependent = true,
                StructTypeName = nameof(GameStateOffset),
                FieldName = "States[0].X",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/GameStateOffsets.cs"
            },
            new OffsetNode
            {
                Id = "game_state_in_game_state",
                DisplayName = "InGameState (States[4].X)",
                Category = "Game States",
                ParentId = "game_state_root",
                DefaultOffset = Marshal.OffsetOf<GameStateOffset>(nameof(GameStateOffset.States)).ToInt32() + 4 * Unsafe.SizeOf<StdTuple2D<IntPtr>>(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(GameStateOffset),
                FieldName = "States[4].X",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/GameStateOffsets.cs"
            },

            // =========================================================================
            // CATEGORY 3: Area & Server Data
            // =========================================================================
            new OffsetNode
            {
                Id = "in_game_area_instance",
                DisplayName = "AreaInstance (AreaInstanceData)",
                Category = "Area / Server Data",
                ParentId = "game_state_in_game_state",
                DefaultOffset = Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.AreaInstanceData)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(InGameStateOffset),
                FieldName = nameof(InGameStateOffset.AreaInstanceData),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "in_game_world_data",
                DisplayName = "WorldData",
                Category = "Area / Server Data",
                ParentId = "game_state_in_game_state",
                DefaultOffset = Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.WorldData)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(InGameStateOffset),
                FieldName = nameof(InGameStateOffset.WorldData),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "in_game_mouse_over_host",
                DisplayName = "MouseOverHostPtr",
                Category = "Area / Server Data",
                ParentId = "game_state_in_game_state",
                DefaultOffset = Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.MouseOverHostPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                IsOptionalStateDependent = true,
                StructTypeName = nameof(InGameStateOffset),
                FieldName = nameof(InGameStateOffset.MouseOverHostPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "area_current_level",
                DisplayName = "CurrentAreaLevel (byte)",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.CurrentAreaLevel)).ToInt32(),
                Kind = ValueKind.NumericField,
                ScalarType = ScalarType.Byte,
                ExpectedMinNumeric = 1,
                ExpectedMaxNumeric = 100,
                StructTypeName = nameof(AreaInstanceOffsets),
                FieldName = nameof(AreaInstanceOffsets.CurrentAreaLevel),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_current_hash",
                DisplayName = "CurrentAreaHash (uint)",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.CurrentAreaHash)).ToInt32(),
                Kind = ValueKind.NumericField,
                ScalarType = ScalarType.UInt,
                StructTypeName = nameof(AreaInstanceOffsets),
                FieldName = nameof(AreaInstanceOffsets.CurrentAreaHash),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_environments",
                DisplayName = "Environments StdVector",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.Environments)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = Unsafe.SizeOf<EnvironmentStruct>(),
                StructTypeName = nameof(AreaInstanceOffsets),
                FieldName = nameof(AreaInstanceOffsets.Environments),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_server_data",
                DisplayName = "ServerData (PlayerInfo.ServerDataPtr)",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.PlayerInfo)).ToInt32() +
                                Marshal.OffsetOf<LocalPlayerStruct>(nameof(LocalPlayerStruct.ServerDataPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(AreaInstanceOffsets),
                FieldName = "PlayerInfo.ServerDataPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_local_players",
                DisplayName = "LocalPlayers StdVector",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.PlayerInfo)).ToInt32() +
                                Marshal.OffsetOf<LocalPlayerStruct>(nameof(LocalPlayerStruct.LocalPlayers)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = 8,
                VectorIsPointerElements = true,
                StructTypeName = nameof(AreaInstanceOffsets),
                FieldName = "PlayerInfo.LocalPlayers",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_local_player_entity",
                DisplayName = "LocalPlayerPtr",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.PlayerInfo)).ToInt32() +
                                Marshal.OffsetOf<LocalPlayerStruct>(nameof(LocalPlayerStruct.LocalPlayerPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(AreaInstanceOffsets),
                FieldName = "PlayerInfo.LocalPlayerPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_awake_entities",
                DisplayName = "AwakeEntities StdMap",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.Entities)).ToInt32() +
                                Marshal.OffsetOf<EntityListStruct>(nameof(EntityListStruct.AwakeEntities)).ToInt32(),
                Kind = ValueKind.StdMapField,
                StructTypeName = nameof(AreaInstanceOffsets),
                FieldName = "Entities.AwakeEntities",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_sleeping_entities",
                DisplayName = "SleepingEntities StdMap",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.Entities)).ToInt32() +
                                Marshal.OffsetOf<EntityListStruct>(nameof(EntityListStruct.SleepingEntities)).ToInt32(),
                Kind = ValueKind.StdMapField,
                StructTypeName = nameof(AreaInstanceOffsets),
                FieldName = "Entities.SleepingEntities",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_terrain_metadata",
                DisplayName = "TerrainMetadata",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = Marshal.OffsetOf<AreaInstanceOffsets>(nameof(AreaInstanceOffsets.TerrainMetadata)).ToInt32(),
                Kind = ValueKind.StructField,
                StructTypeName = nameof(AreaInstanceOffsets),
                FieldName = nameof(AreaInstanceOffsets.TerrainMetadata),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "world_area_details",
                DisplayName = "WorldAreaDetailsPtr",
                Category = "Area / Server Data",
                ParentId = "in_game_world_data",
                DefaultOffset = Marshal.OffsetOf<WorldDataOffset>(nameof(WorldDataOffset.WorldAreaDetailsPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(WorldDataOffset),
                FieldName = nameof(WorldDataOffset.WorldAreaDetailsPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/WorldDataOffset.cs"
            },

            // =========================================================================
            // CATEGORY 4: ServerData & Inventory
            // =========================================================================
            new OffsetNode
            {
                Id = "server_data_psd_vector",
                DisplayName = "PlayerServerData StdVector",
                Category = "ServerData & Inventory",
                ParentId = "area_server_data",
                DefaultOffset = Marshal.OffsetOf<ServerDataOffsets>(nameof(ServerDataOffsets.PlayerServerDataPtr)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = 8,
                VectorIsPointerElements = true,
                StructTypeName = nameof(ServerDataOffsets),
                FieldName = nameof(ServerDataOffsets.PlayerServerDataPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ServerDataOffset.cs"
            },
            new OffsetNode
            {
                Id = "server_data_inventories",
                DisplayName = "PlayerInventories StdVector",
                Category = "ServerData & Inventory",
                ParentId = "area_server_data",
                DefaultOffset = Marshal.OffsetOf<ServerDataStructure>(nameof(ServerDataStructure.PlayerInventories)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = Unsafe.SizeOf<InventoryArrayStruct>(),
                StructTypeName = nameof(ServerDataStructure),
                FieldName = nameof(ServerDataStructure.PlayerInventories),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ServerDataOffset.cs"
            },
            new OffsetNode
            {
                Id = "server_data_world_area_mods",
                DisplayName = "WorldAreaMods StdVector (Unverified Native Owner)",
                Category = "ServerData & Inventory",
                ParentId = "area_server_data",
                DefaultOffset = Marshal.OffsetOf<ServerDataStructure>(nameof(ServerDataStructure.WorldAreaMods)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                ConservativeUnverifiedOnly = true,
                StructTypeName = nameof(ServerDataStructure),
                FieldName = nameof(ServerDataStructure.WorldAreaMods),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ServerDataOffset.cs"
            },
            new OffsetNode
            {
                Id = "psd_gold_record_slot",
                DisplayName = "Gold Record Slot Pointer",
                Category = "ServerData & Inventory",
                ParentId = "server_data_psd_vector",
                DefaultOffset = PlayerServerDataOffsets.GoldRecordPtrSlot,
                Kind = ValueKind.RecordSlotField,
                StructTypeName = nameof(PlayerServerDataOffsets),
                FieldName = nameof(PlayerServerDataOffsets.GoldRecordPtrSlot),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/PlayerServerDataOffsets.cs"
            },
            new OffsetNode
            {
                Id = "psd_gold_field",
                DisplayName = "Gold Amount (Int32)",
                Category = "ServerData & Inventory",
                ParentId = "psd_gold_record_slot",
                DefaultOffset = PlayerServerDataOffsets.GoldFieldOffset,
                Kind = ValueKind.NumericField,
                ScalarType = ScalarType.Int,
                ExpectedMinNumeric = 0,
                ExpectedMaxNumeric = 2_000_000_000,
                StructTypeName = nameof(PlayerServerDataOffsets),
                FieldName = nameof(PlayerServerDataOffsets.GoldFieldOffset),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/PlayerServerDataOffsets.cs"
            },

            // =========================================================================
            // CATEGORY 5: Player & Components
            // =========================================================================
            new OffsetNode
            {
                Id = "player_entity_details",
                DisplayName = "EntityDetailsPtr",
                Category = "Player & Components",
                ParentId = "area_local_player_entity",
                DefaultOffset = Marshal.OffsetOf<ItemStruct>(nameof(ItemStruct.EntityDetailsPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(ItemStruct),
                FieldName = nameof(ItemStruct.EntityDetailsPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/EntityOffsets.cs"
            },
            new OffsetNode
            {
                Id = "player_entity_name",
                DisplayName = "Entity Path / Name StdWString",
                Category = "Player & Components",
                ParentId = "player_entity_details",
                DefaultOffset = Marshal.OffsetOf<EntityDetails>(nameof(EntityDetails.name)).ToInt32(),
                Kind = ValueKind.StdWStringField,
                StructTypeName = nameof(EntityDetails),
                FieldName = nameof(EntityDetails.name),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/EntityOffsets.cs"
            },
            new OffsetNode
            {
                Id = "player_component_lookup",
                DisplayName = "ComponentLookUpPtr",
                Category = "Player & Components",
                ParentId = "player_entity_details",
                DefaultOffset = Marshal.OffsetOf<EntityDetails>(nameof(EntityDetails.ComponentLookUpPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(EntityDetails),
                FieldName = nameof(EntityDetails.ComponentLookUpPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/EntityOffsets.cs"
            },
            new OffsetNode
            {
                Id = "player_component_list",
                DisplayName = "ComponentListPtr StdVector",
                Category = "Player & Components",
                ParentId = "area_local_player_entity",
                DefaultOffset = Marshal.OffsetOf<ItemStruct>(nameof(ItemStruct.ComponentListPtr)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = 8,
                VectorIsPointerElements = true,
                StructTypeName = nameof(ItemStruct),
                FieldName = nameof(ItemStruct.ComponentListPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/EntityOffsets.cs"
            },
            new OffsetNode
            {
                Id = "comp_life",
                DisplayName = "Life Component",
                Category = "Player & Components",
                ParentId = "area_local_player_entity",
                DefaultOffset = 0,
                Kind = ValueKind.ComponentLookup,
                ComponentName = "Life",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Life.cs"
            },
            new OffsetNode
            {
                Id = "comp_life_health",
                DisplayName = "Life.Health VitalStruct",
                Category = "Player & Components",
                ParentId = "comp_life",
                DefaultOffset = Marshal.OffsetOf<LifeOffset>(nameof(LifeOffset.Health)).ToInt32(),
                Kind = ValueKind.VitalStructField,
                StructTypeName = nameof(LifeOffset),
                FieldName = nameof(LifeOffset.Health),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Life.cs"
            },
            new OffsetNode
            {
                Id = "comp_life_mana",
                DisplayName = "Life.Mana VitalStruct",
                Category = "Player & Components",
                ParentId = "comp_life",
                DefaultOffset = Marshal.OffsetOf<LifeOffset>(nameof(LifeOffset.Mana)).ToInt32(),
                Kind = ValueKind.VitalStructField,
                StructTypeName = nameof(LifeOffset),
                FieldName = nameof(LifeOffset.Mana),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Life.cs"
            },
            new OffsetNode
            {
                Id = "comp_life_es",
                DisplayName = "Life.EnergyShield VitalStruct",
                Category = "Player & Components",
                ParentId = "comp_life",
                DefaultOffset = Marshal.OffsetOf<LifeOffset>(nameof(LifeOffset.EnergyShield)).ToInt32(),
                Kind = ValueKind.VitalStructField,
                StructTypeName = nameof(LifeOffset),
                FieldName = nameof(LifeOffset.EnergyShield),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Life.cs"
            },
            new OffsetNode
            {
                Id = "comp_life_spirit",
                DisplayName = "Life.SpiritList StdVector",
                Category = "Player & Components",
                ParentId = "comp_life",
                DefaultOffset = Marshal.OffsetOf<LifeOffset>(nameof(LifeOffset.SpiritList)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = Unsafe.SizeOf<SpiritEntry>(),
                StructTypeName = nameof(LifeOffset),
                FieldName = nameof(LifeOffset.SpiritList),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Life.cs"
            },
            new OffsetNode
            {
                Id = "comp_buffs",
                DisplayName = "Buffs Component",
                Category = "Player & Components",
                ParentId = "area_local_player_entity",
                DefaultOffset = 0,
                Kind = ValueKind.ComponentLookup,
                ComponentName = "Buffs",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Buffs.cs"
            },
            new OffsetNode
            {
                Id = "comp_buffs_status_effects",
                DisplayName = "Buffs.StatusEffectPtr StdVector",
                Category = "Player & Components",
                ParentId = "comp_buffs",
                DefaultOffset = Marshal.OffsetOf<BuffsOffsets>(nameof(BuffsOffsets.StatusEffectPtr)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = Unsafe.SizeOf<StatusEffectStruct>(),
                StructTypeName = nameof(BuffsOffsets),
                FieldName = nameof(BuffsOffsets.StatusEffectPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Buffs.cs"
            },
            new OffsetNode
            {
                Id = "comp_stats",
                DisplayName = "Stats Component",
                Category = "Player & Components",
                ParentId = "area_local_player_entity",
                DefaultOffset = 0,
                Kind = ValueKind.ComponentLookup,
                ComponentName = "Stats",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Stats.cs"
            },
            new OffsetNode
            {
                Id = "comp_stats_changed_by_items",
                DisplayName = "Stats.StatsChangedByItemsPtr",
                Category = "Player & Components",
                ParentId = "comp_stats",
                DefaultOffset = Marshal.OffsetOf<StatsOffsets>(nameof(StatsOffsets.StatsChangedByItemsPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(StatsOffsets),
                FieldName = nameof(StatsOffsets.StatsChangedByItemsPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Stats.cs"
            },
            new OffsetNode
            {
                Id = "comp_stats_weapon_index",
                DisplayName = "Stats.CurrentWeaponIndex",
                Category = "Player & Components",
                ParentId = "comp_stats",
                DefaultOffset = Marshal.OffsetOf<StatsOffsets>(nameof(StatsOffsets.CurrentWeaponIndex)).ToInt32(),
                Kind = ValueKind.NumericField,
                ScalarType = ScalarType.Int,
                ExpectedMinNumeric = 0,
                ExpectedMaxNumeric = 1,
                StructTypeName = nameof(StatsOffsets),
                FieldName = nameof(StatsOffsets.CurrentWeaponIndex),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Stats.cs"
            },
            new OffsetNode
            {
                Id = "comp_stats_changed_by_buffs",
                DisplayName = "Stats.StatsChangedByBuffAndActions",
                Category = "Player & Components",
                ParentId = "comp_stats",
                DefaultOffset = Marshal.OffsetOf<StatsOffsets>(nameof(StatsOffsets.StatsChangedByBuffAndActions)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(StatsOffsets),
                FieldName = nameof(StatsOffsets.StatsChangedByBuffAndActions),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Stats.cs"
            },
            new OffsetNode
            {
                Id = "comp_render",
                DisplayName = "Render Component",
                Category = "Player & Components",
                ParentId = "area_local_player_entity",
                DefaultOffset = 0,
                Kind = ValueKind.ComponentLookup,
                ComponentName = "Render",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Render.cs"
            },
            new OffsetNode
            {
                Id = "comp_render_world_pos",
                DisplayName = "Render.CurrentWorldPosition StdTuple3D",
                Category = "Player & Components",
                ParentId = "comp_render",
                DefaultOffset = Marshal.OffsetOf<RenderOffsets>(nameof(RenderOffsets.CurrentWorldPosition)).ToInt32(),
                Kind = ValueKind.StructField,
                StructTypeName = nameof(RenderOffsets),
                FieldName = nameof(RenderOffsets.CurrentWorldPosition),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Render.cs"
            },
            new OffsetNode
            {
                Id = "comp_render_terrain_height",
                DisplayName = "Render.TerrainHeight (float)",
                Category = "Player & Components",
                ParentId = "comp_render",
                DefaultOffset = Marshal.OffsetOf<RenderOffsets>(nameof(RenderOffsets.TerrainHeight)).ToInt32(),
                Kind = ValueKind.NumericField,
                ScalarType = ScalarType.Float,
                ExpectedMinNumeric = -20000.0,
                ExpectedMaxNumeric = 20000.0,
                StructTypeName = nameof(RenderOffsets),
                FieldName = nameof(RenderOffsets.TerrainHeight),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Render.cs"
            },
            new OffsetNode
            {
                Id = "comp_positioned",
                DisplayName = "Positioned Component",
                Category = "Player & Components",
                ParentId = "area_local_player_entity",
                DefaultOffset = 0,
                Kind = ValueKind.ComponentLookup,
                ComponentName = "Positioned",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Positioned.cs"
            },
            new OffsetNode
            {
                Id = "comp_positioned_reaction",
                DisplayName = "Positioned.Reaction (byte)",
                Category = "Player & Components",
                ParentId = "comp_positioned",
                DefaultOffset = Marshal.OffsetOf<PositionedOffsets>(nameof(PositionedOffsets.Reaction)).ToInt32(),
                Kind = ValueKind.NumericField,
                ScalarType = ScalarType.Byte,
                ExpectedMinNumeric = 0,
                ExpectedMaxNumeric = 2,
                StructTypeName = nameof(PositionedOffsets),
                FieldName = nameof(PositionedOffsets.Reaction),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Positioned.cs"
            },
            new OffsetNode
            {
                Id = "comp_actor",
                DisplayName = "Actor Component",
                Category = "Player & Components",
                ParentId = "area_local_player_entity",
                DefaultOffset = 0,
                Kind = ValueKind.ComponentLookup,
                ComponentName = "Actor",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Actor.cs"
            },
            new OffsetNode
            {
                Id = "comp_actor_animation_id",
                DisplayName = "Actor.AnimationId (int)",
                Category = "Player & Components",
                ParentId = "comp_actor",
                DefaultOffset = Marshal.OffsetOf<ActorOffset>(nameof(ActorOffset.AnimationId)).ToInt32(),
                Kind = ValueKind.NumericField,
                ScalarType = ScalarType.Int,
                ExpectedMinNumeric = 0,
                ExpectedMaxNumeric = 10000,
                StructTypeName = nameof(ActorOffset),
                FieldName = nameof(ActorOffset.AnimationId),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Actor.cs"
            },
            new OffsetNode
            {
                Id = "comp_actor_active_skills",
                DisplayName = "Actor.ActiveSkillsPtr StdVector",
                Category = "Player & Components",
                ParentId = "comp_actor",
                DefaultOffset = Marshal.OffsetOf<ActorOffset>(nameof(ActorOffset.ActiveSkillsPtr)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = Unsafe.SizeOf<ActiveSkillStructure>(),
                StructTypeName = nameof(ActorOffset),
                FieldName = nameof(ActorOffset.ActiveSkillsPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Actor.cs"
            },
            new OffsetNode
            {
                Id = "comp_actor_cooldowns",
                DisplayName = "Actor.CooldownsPtr StdVector",
                Category = "Player & Components",
                ParentId = "comp_actor",
                DefaultOffset = Marshal.OffsetOf<ActorOffset>(nameof(ActorOffset.CooldownsPtr)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = Unsafe.SizeOf<ActiveSkillCooldown>(),
                StructTypeName = nameof(ActorOffset),
                FieldName = nameof(ActorOffset.CooldownsPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Actor.cs"
            },
            new OffsetNode
            {
                Id = "comp_actor_deployed_entities",
                DisplayName = "Actor.DeployedEntityArray StdVector",
                Category = "Player & Components",
                ParentId = "comp_actor",
                DefaultOffset = Marshal.OffsetOf<ActorOffset>(nameof(ActorOffset.DeployedEntityArray)).ToInt32(),
                Kind = ValueKind.StdVectorField,
                VectorElementSize = Unsafe.SizeOf<DeployedEntityStructure>(),
                StructTypeName = nameof(ActorOffset),
                FieldName = nameof(ActorOffset.DeployedEntityArray),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Actor.cs"
            },
            new OffsetNode
            {
                Id = "comp_player",
                DisplayName = "Player Component",
                Category = "Player & Components",
                ParentId = "area_local_player_entity",
                DefaultOffset = 0,
                Kind = ValueKind.ComponentLookup,
                ComponentName = "Player",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Player.cs"
            },
            new OffsetNode
            {
                Id = "comp_player_name",
                DisplayName = "Player.Name StdWString",
                Category = "Player & Components",
                ParentId = "comp_player",
                DefaultOffset = Marshal.OffsetOf<PlayerOffsets>(nameof(PlayerOffsets.Name)).ToInt32(),
                Kind = ValueKind.StdWStringField,
                StructTypeName = nameof(PlayerOffsets),
                FieldName = nameof(PlayerOffsets.Name),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Player.cs"
            },
            new OffsetNode
            {
                Id = "comp_player_level",
                DisplayName = "Player.Level (byte)",
                Category = "Player & Components",
                ParentId = "comp_player",
                DefaultOffset = Marshal.OffsetOf<PlayerOffsets>(nameof(PlayerOffsets.Level)).ToInt32(),
                Kind = ValueKind.NumericField,
                ScalarType = ScalarType.Byte,
                ExpectedMinNumeric = 1,
                ExpectedMaxNumeric = 100,
                StructTypeName = nameof(PlayerOffsets),
                FieldName = nameof(PlayerOffsets.Level),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Player.cs"
            },

            // =========================================================================
            // CATEGORY 6: UI Elements
            // =========================================================================
            new OffsetNode
            {
                Id = "ui_root_struct",
                DisplayName = "UiRootStructPtr",
                Category = "UI Elements",
                ParentId = "game_state_in_game_state",
                DefaultOffset = Marshal.OffsetOf<InGameStateOffset>(nameof(InGameStateOffset.UiRootStructPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(InGameStateOffset),
                FieldName = nameof(InGameStateOffset.UiRootStructPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "ui_game_ui_ptr",
                DisplayName = "GameUiPtr",
                Category = "UI Elements",
                ParentId = "ui_root_struct",
                DefaultOffset = Marshal.OffsetOf<UiRootStruct>(nameof(UiRootStruct.GameUiPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(UiRootStruct),
                FieldName = nameof(UiRootStruct.GameUiPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "ui_chat_parent",
                DisplayName = "ChatParentPtr",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = Marshal.OffsetOf<ImportantUiElementsOffsets>(nameof(ImportantUiElementsOffsets.ChatParentPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                StructTypeName = nameof(ImportantUiElementsOffsets),
                FieldName = nameof(ImportantUiElementsOffsets.ChatParentPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },
            new OffsetNode
            {
                Id = "ui_left_panel",
                DisplayName = "LeftPanelPtr",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = Marshal.OffsetOf<ImportantUiElementsOffsets>(nameof(ImportantUiElementsOffsets.LeftPanelPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                IsOptionalStateDependent = true,
                StructTypeName = nameof(ImportantUiElementsOffsets),
                FieldName = nameof(ImportantUiElementsOffsets.LeftPanelPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },
            new OffsetNode
            {
                Id = "ui_right_panel",
                DisplayName = "RightPanelPtr",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = Marshal.OffsetOf<ImportantUiElementsOffsets>(nameof(ImportantUiElementsOffsets.RightPanelPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                IsOptionalStateDependent = true,
                StructTypeName = nameof(ImportantUiElementsOffsets),
                FieldName = nameof(ImportantUiElementsOffsets.RightPanelPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },
            new OffsetNode
            {
                Id = "ui_passive_tree_panel",
                DisplayName = "PassiveSkillTreePanel",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = Marshal.OffsetOf<ImportantUiElementsOffsets>(nameof(ImportantUiElementsOffsets.PassiveSkillTreePanel)).ToInt32(),
                Kind = ValueKind.PointerField,
                IsOptionalStateDependent = true,
                StructTypeName = nameof(ImportantUiElementsOffsets),
                FieldName = nameof(ImportantUiElementsOffsets.PassiveSkillTreePanel),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },
            new OffsetNode
            {
                Id = "ui_map_parent",
                DisplayName = "MapParentPtr",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = Marshal.OffsetOf<ImportantUiElementsOffsets>(nameof(ImportantUiElementsOffsets.MapParentPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                IsOptionalStateDependent = true,
                StructTypeName = nameof(ImportantUiElementsOffsets),
                FieldName = nameof(ImportantUiElementsOffsets.MapParentPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },
            new OffsetNode
            {
                Id = "ui_world_map_panel",
                DisplayName = "WorldMapPanelPtr",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = Marshal.OffsetOf<ImportantUiElementsOffsets>(nameof(ImportantUiElementsOffsets.WorldMapPanelPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                IsOptionalStateDependent = true,
                StructTypeName = nameof(ImportantUiElementsOffsets),
                FieldName = nameof(ImportantUiElementsOffsets.WorldMapPanelPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },

            // =========================================================================
            // CATEGORY 7: AreaLoading State
            // =========================================================================
            new OffsetNode
            {
                Id = "loading_state_is_loading",
                DisplayName = "AreaLoadingState.IsLoading",
                Category = "Area Loading State",
                ParentId = "game_state_area_loading",
                DefaultOffset = Marshal.OffsetOf<AreaLoadingStateOffset>(nameof(AreaLoadingStateOffset.IsLoading)).ToInt32(),
                Kind = ValueKind.NumericField,
                ScalarType = ScalarType.Int,
                ExpectedMinNumeric = 0,
                ExpectedMaxNumeric = 1,
                StructTypeName = nameof(AreaLoadingStateOffset),
                FieldName = nameof(AreaLoadingStateOffset.IsLoading),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/AreaLoadingStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "loading_state_total_time",
                DisplayName = "AreaLoadingState.TotalLoadingScreenTimeMs",
                Category = "Area Loading State",
                ParentId = "game_state_area_loading",
                DefaultOffset = Marshal.OffsetOf<AreaLoadingStateOffset>(nameof(AreaLoadingStateOffset.TotalLoadingScreenTimeMs)).ToInt32(),
                Kind = ValueKind.NumericField,
                ScalarType = ScalarType.UInt,
                StructTypeName = nameof(AreaLoadingStateOffset),
                FieldName = nameof(AreaLoadingStateOffset.TotalLoadingScreenTimeMs),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/AreaLoadingStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "loading_state_area_details",
                DisplayName = "AreaLoadingState.CurrentAreaDetailsPtr",
                Category = "Area Loading State",
                ParentId = "game_state_area_loading",
                DefaultOffset = Marshal.OffsetOf<AreaLoadingStateOffset>(nameof(AreaLoadingStateOffset.CurrentAreaDetailsPtr)).ToInt32(),
                Kind = ValueKind.PointerField,
                IsOptionalStateDependent = true,
                StructTypeName = nameof(AreaLoadingStateOffset),
                FieldName = nameof(AreaLoadingStateOffset.CurrentAreaDetailsPtr),
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/AreaLoadingStateOffset.cs"
            }
        ];
    }

    public static List<OffsetNode> CreateGoldChainManifest() => CreateFullRepositoryManifest();
}
