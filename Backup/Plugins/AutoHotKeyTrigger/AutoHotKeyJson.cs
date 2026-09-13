namespace AutoHotKeyTrigger
{
    using System.Text.Json;
    using System.Text.Json.Serialization;

    /// <summary>
    /// JSON settings shared by the plugin. The legacy type discriminator is kept so existing
    /// profiles containing a Wait component continue to load without a manual migration.
    /// </summary>
    internal static class AutoHotKeyJson
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
