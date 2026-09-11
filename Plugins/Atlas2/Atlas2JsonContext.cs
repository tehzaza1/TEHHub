namespace Atlas2
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(Atlas2Settings))]
    [JsonSerializable(typeof(Dictionary<string, BiomeInfo>))]
    [JsonSerializable(typeof(Dictionary<string, ContentInfo>))]
    internal sealed partial class Atlas2JsonContext : JsonSerializerContext
    {
    }
}
