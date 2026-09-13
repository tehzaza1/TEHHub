namespace HealthBars
{
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(HealthBarsSettings))]
    internal sealed partial class HealthBarsJsonContext : JsonSerializerContext
    {
    }
}
