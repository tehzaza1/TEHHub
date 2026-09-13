namespace Atlas2
{
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(Atlas2Settings))]
    internal sealed partial class Atlas2JsonContext : JsonSerializerContext
    {
    }
}
