namespace AmanamuVoidAlert
{
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(AmanamuVoidAlertSettings))]
    internal sealed partial class AmanamuVoidAlertJsonContext : JsonSerializerContext
    {
    }
}
