namespace PickupHelper
{
    using System.Text.Json.Serialization;

    [JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true, WriteIndented = true)]
    [JsonSerializable(typeof(PickupHelperSettings))]
    internal sealed partial class PickupHelperJsonContext : JsonSerializerContext
    {
    }
}
