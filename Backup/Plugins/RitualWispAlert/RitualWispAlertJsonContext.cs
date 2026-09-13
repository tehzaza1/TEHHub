namespace RitualWispAlert
{
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(RitualWispAlertSettings))]
    internal sealed partial class RitualWispAlertJsonContext : JsonSerializerContext
    {
    }
}
