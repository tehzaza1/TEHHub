namespace LootValue
{
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(LootValueSettings))]
    internal sealed partial class LootValueJsonContext : JsonSerializerContext
    {
    }
}
