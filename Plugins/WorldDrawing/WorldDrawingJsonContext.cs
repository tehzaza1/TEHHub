namespace WorldDrawing
{
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(WorldDrawingSettings))]
    internal sealed partial class WorldDrawingJsonContext : JsonSerializerContext
    {
    }
}
