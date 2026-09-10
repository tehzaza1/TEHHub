namespace Radar
{
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(RadarSettings))]
    internal sealed partial class RadarJsonContext : JsonSerializerContext
    {
    }
}
