namespace AutoExile2
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    internal static class AutoExileJson
    {
        internal static readonly JsonSerializerOptions Options = new()
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
    }
}
