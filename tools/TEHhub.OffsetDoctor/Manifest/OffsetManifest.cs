namespace TEHhub.OffsetDoctor.Manifest;

public static class OffsetManifest
{
    public static List<OffsetNode> CreateGoldChainManifest()
    {
        return
        [
            new OffsetNode
            {
                Id = "game_state_root",
                DisplayName = "Game States Static Root",
                ParentId = null,
                DefaultOffset = 0,
                Kind = ValueKind.StaticPattern,
                StaticPatternName = "Game States",
                ProductionSourceLocation = "TEHhub.Offsets/StaticOffsetsPatterns.cs"
            },
            new OffsetNode
            {
                Id = "in_game_state",
                DisplayName = "InGameState (States[4].X)",
                ParentId = "game_state_root",
                DefaultOffset = 0x90,
                Kind = ValueKind.PointerField,
                Alignment = 8,
                SearchRadius = 0x100,
                StructTypeName = "GameStateOffset",
                FieldName = "States[4].X",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/GameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "area_instance",
                DisplayName = "AreaInstance (AreaInstanceData)",
                ParentId = "in_game_state",
                DefaultOffset = 0x290,
                Kind = ValueKind.PointerField,
                Alignment = 8,
                SearchRadius = 0x200,
                StructTypeName = "InGameStateOffset",
                FieldName = "AreaInstanceData",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/InGameStateOffset.cs"
            },
            new OffsetNode
            {
                Id = "server_data",
                DisplayName = "ServerData (PlayerInfo.ServerDataPtr)",
                ParentId = "area_instance",
                DefaultOffset = 0x5B0,
                Kind = ValueKind.PointerField,
                Alignment = 8,
                SearchRadius = 0x200,
                StructTypeName = "AreaInstanceOffsets",
                FieldName = "PlayerInfo.ServerDataPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/AreaInstanceOffsets.cs"
            },
            new OffsetNode
            {
                Id = "player_server_data_vector",
                DisplayName = "PlayerServerData StdVector",
                ParentId = "server_data",
                DefaultOffset = 0x48,
                Kind = ValueKind.StdVectorField,
                Alignment = 8,
                SearchRadius = 0x100,
                StructTypeName = "ServerDataOffset",
                FieldName = "PlayerServerDataPtr",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/ServerDataOffset.cs"
            },
            new OffsetNode
            {
                Id = "gold_record_slot",
                DisplayName = "Gold Record Slot Pointer",
                ParentId = "player_server_data_vector",
                DefaultOffset = 0x0E28,
                Kind = ValueKind.RecordSlotField,
                Alignment = 8,
                SearchRadius = 0x200,
                StructTypeName = "PlayerServerDataOffsets",
                FieldName = "GoldRecordPtrSlot",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/PlayerServerDataOffsets.cs"
            },
            new OffsetNode
            {
                Id = "gold_field",
                DisplayName = "Gold Amount (Int32)",
                ParentId = "gold_record_slot",
                DefaultOffset = 0x0618,
                Kind = ValueKind.NumericField,
                Alignment = 4,
                SearchRadius = 0x200,
                StructTypeName = "PlayerServerDataOffsets",
                FieldName = "GoldFieldOffset",
                ProductionSourceLocation = "TEHhub.Offsets/Objects/States/InGameState/PlayerServerDataOffsets.cs"
            }
        ];
    }
}
