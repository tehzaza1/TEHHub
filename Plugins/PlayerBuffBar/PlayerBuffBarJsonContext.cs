namespace PlayerBuffBar
{
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(PlayerBuffBarSettings))]
    internal sealed partial class PlayerBuffBarJsonContext : JsonSerializerContext
    {
    }
}
