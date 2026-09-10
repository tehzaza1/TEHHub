namespace PreloadAlert
{
    using System.Collections.Generic;
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(PreloadSettings))]
    [JsonSerializable(typeof(List<KeyValuePair<string, PreloadInfo>>))]
    internal sealed partial class PreloadAlertJsonContext : JsonSerializerContext
    {
    }
}
