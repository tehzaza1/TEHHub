namespace TEHhub.OffsetDoctor.Manifest;

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
                DefaultOffset = 0,
                Kind = ValueKind.PointerField,
                StructTypeName = "GameStateStaticOffset",
                FieldName = "GameState",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/GameStateOffsets.cs"
            },
            new OffsetNode
            {
                Id = "game_state_current_state",
                DisplayName = "CurrentStatePtr StdVector",
                Category = "Game States",
                ParentId = "game_state_root",
                DefaultOffset = 0x10,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "GameStateOffset",
                FieldName = "CurrentStatePtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/GameStateOffsets.cs"
            },
            new OffsetNode
            {
                Id = "game_state_area_loading",
                DisplayName = "AreaLoadingState (States[0].X)",
                Category = "Game States",
                ParentId = "game_state_root",
                DefaultOffset = 0x50,
                Kind = ValueKind.PointerField,
                StructTypeName = "GameStateOffset",
                FieldName = "States[0].X",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/GameStateOffsets.cs"
            },
            new OffsetNode
            {
                Id = "game_state_in_game_state",
                DisplayName = "InGameState (States[4].X)",
                Category = "Game States",
                ParentId = "game_state_root",
                DefaultOffset = 0x90,
                Kind = ValueKind.PointerField,
                StructTypeName = "GameStateOffset",
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
                DefaultOffset = 0x290,
                Kind = ValueKind.PointerField,
                StructTypeName = "InGameStateOffset",
                FieldName = "AreaInstanceData",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "in_game_world_data",
                DisplayName = "WorldData",
                Category = "Area / Server Data",
                ParentId = "game_state_in_game_state",
                DefaultOffset = 0x368,
                Kind = ValueKind.PointerField,
                StructTypeName = "InGameStateOffset",
                FieldName = "WorldData",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "in_game_mouse_over_host",
                DisplayName = "MouseOverHostPtr",
                Category = "Area / Server Data",
                ParentId = "game_state_in_game_state",
                DefaultOffset = 0x300,
                Kind = ValueKind.PointerField,
                StructTypeName = "InGameStateOffset",
                FieldName = "MouseOverHostPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "area_current_level",
                DisplayName = "CurrentAreaLevel (byte)",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = 0x0BC,
                Kind = ValueKind.NumericField,
                StructTypeName = "AreaInstanceOffsets",
                FieldName = "CurrentAreaLevel",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_current_hash",
                DisplayName = "CurrentAreaHash (uint)",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = 0x114,
                Kind = ValueKind.NumericField,
                StructTypeName = "AreaInstanceOffsets",
                FieldName = "CurrentAreaHash",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_environments",
                DisplayName = "Environments StdVector",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = 0x4C0,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "AreaInstanceOffsets",
                FieldName = "Environments",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_server_data",
                DisplayName = "ServerData (PlayerInfo.ServerDataPtr)",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = 0x5B0,
                Kind = ValueKind.PointerField,
                StructTypeName = "AreaInstanceOffsets",
                FieldName = "PlayerInfo.ServerDataPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_local_players",
                DisplayName = "LocalPlayers StdVector",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = 0x5B8,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "AreaInstanceOffsets",
                FieldName = "PlayerInfo.LocalPlayers",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_local_player_entity",
                DisplayName = "LocalPlayerPtr",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = 0x5D0,
                Kind = ValueKind.PointerField,
                StructTypeName = "AreaInstanceOffsets",
                FieldName = "PlayerInfo.LocalPlayerPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_awake_entities",
                DisplayName = "AwakeEntities StdMap",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = 0x6F0,
                Kind = ValueKind.StdMapField,
                StructTypeName = "AreaInstanceOffsets",
                FieldName = "Entities.AwakeEntities",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_sleeping_entities",
                DisplayName = "SleepingEntities StdMap",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = 0x6F8,
                Kind = ValueKind.StdMapField,
                StructTypeName = "AreaInstanceOffsets",
                FieldName = "Entities.SleepingEntities",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "area_terrain_metadata",
                DisplayName = "TerrainMetadata",
                Category = "Area / Server Data",
                ParentId = "in_game_area_instance",
                DefaultOffset = 0x8D0,
                Kind = ValueKind.StructField,
                StructTypeName = "AreaInstanceOffsets",
                FieldName = "TerrainMetadata",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "world_area_details",
                DisplayName = "WorldAreaDetailsPtr",
                Category = "Area / Server Data",
                ParentId = "in_game_world_data",
                DefaultOffset = 0x98,
                Kind = ValueKind.PointerField,
                StructTypeName = "WorldDataOffset",
                FieldName = "WorldAreaDetailsPtr",
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
                DefaultOffset = 0x48,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "ServerDataOffsets",
                FieldName = "PlayerServerDataPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ServerDataOffset.cs"
            },
            new OffsetNode
            {
                Id = "server_data_inventories",
                DisplayName = "PlayerInventories StdVector",
                Category = "ServerData & Inventory",
                ParentId = "area_server_data",
                DefaultOffset = 0x320,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "ServerDataStructure",
                FieldName = "PlayerInventories",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ServerDataOffset.cs"
            },
            new OffsetNode
            {
                Id = "server_data_world_area_mods",
                DisplayName = "WorldAreaMods StdVector (Unverified Native Owner)",
                Category = "ServerData & Inventory",
                ParentId = "area_server_data",
                DefaultOffset = 0x8A8,
                Kind = ValueKind.StdVectorField,
                ConservativeUnverifiedOnly = true,
                StructTypeName = "ServerDataStructure",
                FieldName = "WorldAreaMods",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ServerDataOffset.cs"
            },
            new OffsetNode
            {
                Id = "psd_gold_record_slot",
                DisplayName = "Gold Record Slot Pointer",
                Category = "ServerData & Inventory",
                ParentId = "server_data_psd_vector",
                DefaultOffset = 0x0E28,
                Kind = ValueKind.RecordSlotField,
                StructTypeName = "PlayerServerDataOffsets",
                FieldName = "GoldRecordPtrSlot",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/PlayerServerDataOffsets.cs"
            },
            new OffsetNode
            {
                Id = "psd_gold_field",
                DisplayName = "Gold Amount (Int32)",
                Category = "ServerData & Inventory",
                ParentId = "psd_gold_record_slot",
                DefaultOffset = 0x0618,
                Kind = ValueKind.NumericField,
                StructTypeName = "PlayerServerDataOffsets",
                FieldName = "GoldFieldOffset",
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
                DefaultOffset = 0x08,
                Kind = ValueKind.PointerField,
                StructTypeName = "ItemStruct",
                FieldName = "EntityDetailsPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/EntityOffsets.cs"
            },
            new OffsetNode
            {
                Id = "player_entity_name",
                DisplayName = "Entity Path / Name StdWString",
                Category = "Player & Components",
                ParentId = "player_entity_details",
                DefaultOffset = 0x08,
                Kind = ValueKind.StdWStringField,
                StructTypeName = "EntityDetails",
                FieldName = "name",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/EntityOffsets.cs"
            },
            new OffsetNode
            {
                Id = "player_component_lookup",
                DisplayName = "ComponentLookUpPtr",
                Category = "Player & Components",
                ParentId = "player_entity_details",
                DefaultOffset = 0x28,
                Kind = ValueKind.PointerField,
                StructTypeName = "EntityDetails",
                FieldName = "ComponentLookUpPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/EntityOffsets.cs"
            },
            new OffsetNode
            {
                Id = "player_component_list",
                DisplayName = "ComponentListPtr StdVector",
                Category = "Player & Components",
                ParentId = "area_local_player_entity",
                DefaultOffset = 0x10,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "ItemStruct",
                FieldName = "ComponentListPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/EntityOffsets.cs"
            },
            new OffsetNode
            {
                Id = "comp_life",
                DisplayName = "Life Component",
                Category = "Player & Components",
                ParentId = "player_component_list",
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
                DefaultOffset = 0x1B0,
                Kind = ValueKind.VitalStructField,
                StructTypeName = "LifeOffset",
                FieldName = "Health",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Life.cs"
            },
            new OffsetNode
            {
                Id = "comp_life_mana",
                DisplayName = "Life.Mana VitalStruct",
                Category = "Player & Components",
                ParentId = "comp_life",
                DefaultOffset = 0x208,
                Kind = ValueKind.VitalStructField,
                StructTypeName = "LifeOffset",
                FieldName = "Mana",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Life.cs"
            },
            new OffsetNode
            {
                Id = "comp_life_es",
                DisplayName = "Life.EnergyShield VitalStruct",
                Category = "Player & Components",
                ParentId = "comp_life",
                DefaultOffset = 0x248,
                Kind = ValueKind.VitalStructField,
                StructTypeName = "LifeOffset",
                FieldName = "EnergyShield",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Life.cs"
            },
            new OffsetNode
            {
                Id = "comp_life_spirit",
                DisplayName = "Life.SpiritList StdVector",
                Category = "Player & Components",
                ParentId = "comp_life",
                DefaultOffset = 0x380,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "LifeOffset",
                FieldName = "SpiritList",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Life.cs"
            },
            new OffsetNode
            {
                Id = "comp_buffs",
                DisplayName = "Buffs Component",
                Category = "Player & Components",
                ParentId = "player_component_list",
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
                DefaultOffset = 0x160,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "BuffsOffsets",
                FieldName = "StatusEffectPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Buffs.cs"
            },
            new OffsetNode
            {
                Id = "comp_stats",
                DisplayName = "Stats Component",
                Category = "Player & Components",
                ParentId = "player_component_list",
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
                DefaultOffset = 0x160,
                Kind = ValueKind.PointerField,
                StructTypeName = "StatsOffsets",
                FieldName = "StatsChangedByItemsPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Stats.cs"
            },
            new OffsetNode
            {
                Id = "comp_stats_weapon_index",
                DisplayName = "Stats.CurrentWeaponIndex",
                Category = "Player & Components",
                ParentId = "comp_stats",
                DefaultOffset = 0x168,
                Kind = ValueKind.NumericField,
                StructTypeName = "StatsOffsets",
                FieldName = "CurrentWeaponIndex",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Stats.cs"
            },
            new OffsetNode
            {
                Id = "comp_stats_changed_by_buffs",
                DisplayName = "Stats.StatsChangedByBuffAndActions",
                Category = "Player & Components",
                ParentId = "comp_stats",
                DefaultOffset = 0x1C8,
                Kind = ValueKind.PointerField,
                StructTypeName = "StatsOffsets",
                FieldName = "StatsChangedByBuffAndActions",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Stats.cs"
            },
            new OffsetNode
            {
                Id = "comp_render",
                DisplayName = "Render Component",
                Category = "Player & Components",
                ParentId = "player_component_list",
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
                DefaultOffset = 0x138,
                Kind = ValueKind.StructField,
                StructTypeName = "RenderOffsets",
                FieldName = "CurrentWorldPosition",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Render.cs"
            },
            new OffsetNode
            {
                Id = "comp_render_terrain_height",
                DisplayName = "Render.TerrainHeight (float)",
                Category = "Player & Components",
                ParentId = "comp_render",
                DefaultOffset = 0x1B0,
                Kind = ValueKind.NumericField,
                StructTypeName = "RenderOffsets",
                FieldName = "TerrainHeight",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Render.cs"
            },
            new OffsetNode
            {
                Id = "comp_positioned",
                DisplayName = "Positioned Component",
                Category = "Player & Components",
                ParentId = "player_component_list",
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
                DefaultOffset = 0x1E0,
                Kind = ValueKind.NumericField,
                StructTypeName = "PositionedOffsets",
                FieldName = "Reaction",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Positioned.cs"
            },
            new OffsetNode
            {
                Id = "comp_actor",
                DisplayName = "Actor Component",
                Category = "Player & Components",
                ParentId = "player_component_list",
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
                DefaultOffset = 0x8B0,
                Kind = ValueKind.NumericField,
                StructTypeName = "ActorOffset",
                FieldName = "AnimationId",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Actor.cs"
            },
            new OffsetNode
            {
                Id = "comp_actor_active_skills",
                DisplayName = "Actor.ActiveSkillsPtr StdVector",
                Category = "Player & Components",
                ParentId = "comp_actor",
                DefaultOffset = 0xB08,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "ActorOffset",
                FieldName = "ActiveSkillsPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Actor.cs"
            },
            new OffsetNode
            {
                Id = "comp_actor_cooldowns",
                DisplayName = "Actor.CooldownsPtr StdVector",
                Category = "Player & Components",
                ParentId = "comp_actor",
                DefaultOffset = 0xB20,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "ActorOffset",
                FieldName = "CooldownsPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Actor.cs"
            },
            new OffsetNode
            {
                Id = "comp_actor_deployed_entities",
                DisplayName = "Actor.DeployedEntityArray StdVector",
                Category = "Player & Components",
                ParentId = "comp_actor",
                DefaultOffset = 0xC28,
                Kind = ValueKind.StdVectorField,
                StructTypeName = "ActorOffset",
                FieldName = "DeployedEntityArray",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Actor.cs"
            },
            new OffsetNode
            {
                Id = "comp_player",
                DisplayName = "Player Component",
                Category = "Player & Components",
                ParentId = "player_component_list",
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
                DefaultOffset = 0x1B0,
                Kind = ValueKind.StdWStringField,
                StructTypeName = "PlayerOffsets",
                FieldName = "Name",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/Components/Player.cs"
            },
            new OffsetNode
            {
                Id = "comp_player_level",
                DisplayName = "Player.Level (byte)",
                Category = "Player & Components",
                ParentId = "comp_player",
                DefaultOffset = 0x204,
                Kind = ValueKind.NumericField,
                StructTypeName = "PlayerOffsets",
                FieldName = "Level",
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
                DefaultOffset = 0x2F0,
                Kind = ValueKind.PointerField,
                StructTypeName = "InGameStateOffset",
                FieldName = "UiRootStructPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "ui_game_ui_ptr",
                DisplayName = "GameUiPtr",
                Category = "UI Elements",
                ParentId = "ui_root_struct",
                DefaultOffset = 0xBE0,
                Kind = ValueKind.PointerField,
                StructTypeName = "UiRootStruct",
                FieldName = "GameUiPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "ui_chat_parent",
                DisplayName = "ChatParentPtr",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = 0x640,
                Kind = ValueKind.PointerField,
                StructTypeName = "ImportantUiElementsOffsets",
                FieldName = "ChatParentPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },
            new OffsetNode
            {
                Id = "ui_left_panel",
                DisplayName = "LeftPanelPtr",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = 0x6D0,
                Kind = ValueKind.PointerField,
                StructTypeName = "ImportantUiElementsOffsets",
                FieldName = "LeftPanelPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },
            new OffsetNode
            {
                Id = "ui_right_panel",
                DisplayName = "RightPanelPtr",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = 0x6D8,
                Kind = ValueKind.PointerField,
                StructTypeName = "ImportantUiElementsOffsets",
                FieldName = "RightPanelPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },
            new OffsetNode
            {
                Id = "ui_passive_tree_panel",
                DisplayName = "PassiveSkillTreePanel",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = 0x730,
                Kind = ValueKind.PointerField,
                StructTypeName = "ImportantUiElementsOffsets",
                FieldName = "PassiveSkillTreePanel",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },
            new OffsetNode
            {
                Id = "ui_map_parent",
                DisplayName = "MapParentPtr",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = 0x7C0,
                Kind = ValueKind.PointerField,
                StructTypeName = "ImportantUiElementsOffsets",
                FieldName = "MapParentPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ImportantUiElementsOffsets.cs"
            },
            new OffsetNode
            {
                Id = "ui_world_map_panel",
                DisplayName = "WorldMapPanelPtr",
                Category = "UI Elements",
                ParentId = "ui_game_ui_ptr",
                DefaultOffset = 0x988,
                Kind = ValueKind.PointerField,
                StructTypeName = "ImportantUiElementsOffsets",
                FieldName = "WorldMapPanelPtr",
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
                DefaultOffset = 0x770,
                Kind = ValueKind.NumericField,
                StructTypeName = "AreaLoadingStateOffset",
                FieldName = "IsLoading",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/AreaLoadingStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "loading_state_total_time",
                DisplayName = "AreaLoadingState.TotalLoadingScreenTimeMs",
                Category = "Area Loading State",
                ParentId = "game_state_area_loading",
                DefaultOffset = 0xEC0,
                Kind = ValueKind.NumericField,
                StructTypeName = "AreaLoadingStateOffset",
                FieldName = "TotalLoadingScreenTimeMs",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/AreaLoadingStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "loading_state_area_details",
                DisplayName = "AreaLoadingState.CurrentAreaDetailsPtr",
                Category = "Area Loading State",
                ParentId = "game_state_area_loading",
                DefaultOffset = 0xF40,
                Kind = ValueKind.PointerField,
                StructTypeName = "AreaLoadingStateOffset",
                FieldName = "CurrentAreaDetailsPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/AreaLoadingStateOffset.cs"
            }
        ];
    }

    public static List<OffsetNode> CreateGoldChainManifest() => CreateFullRepositoryManifest();
}
