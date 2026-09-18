namespace TEHhub.OffsetDoctor.Reporting;

using System.Text.Json;
using System.Text.Json.Serialization;
using TEHhub.OffsetDoctor.Recovery;

public static class JsonReportExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new IntPtrJsonConverter() }
    };

    public static void ExportToFile(OffsetDoctorReport report, string filePath)
    {
        var json = JsonSerializer.Serialize(report, Options);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, json);
    }

    public static string ToJsonString(OffsetDoctorReport report)
    {
        return JsonSerializer.Serialize(report, Options);
    }

    private sealed class IntPtrJsonConverter : JsonConverter<IntPtr>
    {
        public override IntPtr Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return new IntPtr(reader.GetInt64());
        }

        public override void Write(Utf8JsonWriter writer, IntPtr value, JsonSerializerOptions options)
        {
            writer.WriteStringValue($"0x{value.ToInt64():X}");
        }
    }
}
