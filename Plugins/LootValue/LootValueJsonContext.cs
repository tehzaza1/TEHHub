namespace LootValue
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(LootValueSettings))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, List<string>>))]
    [JsonSerializable(typeof(PriceCacheSnapshot))]
    internal sealed partial class LootValueJsonContext : JsonSerializerContext
    {
    }
}
