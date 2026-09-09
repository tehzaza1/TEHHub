namespace Radar
{
    using System.Numerics;
    using GameHelper.Utils;
    using Newtonsoft.Json;

    public class PoiTarget
    {
        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public bool Icon { get; set; } = false;

        public string IconName { get; set; } = string.Empty;

        public float IconSize { get; set; } = 30.0f;

        public bool Enabled { get; set; } = true;

        public string NameColor { get; set; } = "253, 224, 71";

        public string BGColor { get; set; } = "0, 0, 0";

        public int ExpectedCount { get; set; } = 1;

        public bool DrawPath { get; set; } = true;

        public string DrawPathColor { get; set; } = "253, 224, 71";

        public bool AlwaysDrawPath { get; set; } = false;

        [JsonIgnore]
        private uint? parsedNameColor;

        [JsonIgnore]
        public uint ParsedNameColor
        {
            get
            {
                if (!this.parsedNameColor.HasValue)
                {
                    this.parsedNameColor = ParseRgb(this.NameColor, 255);
                }

                return this.parsedNameColor.Value;
            }
        }

        [JsonIgnore]
        private uint? parsedPathColor;

        [JsonIgnore]
        public uint ParsedPathColor
        {
            get
            {
                if (!this.parsedPathColor.HasValue)
                {
                    this.parsedPathColor = ParseRgb(this.DrawPathColor, 255);
                }

                return this.parsedPathColor.Value;
            }
        }

        private static uint ParseRgb(string rgbStr, byte alpha = 255)
        {
            if (string.IsNullOrWhiteSpace(rgbStr))
            {
                return ImGuiHelper.Color(253, 224, 71, alpha);
            }

            var parts = rgbStr.Split(',');
            if (parts.Length == 3 &&
                byte.TryParse(parts[0].Trim(), out var r) &&
                byte.TryParse(parts[1].Trim(), out var g) &&
                byte.TryParse(parts[2].Trim(), out var b))
            {
                return ImGuiHelper.Color(r, g, b, alpha);
            }

            return ImGuiHelper.Color(253, 224, 71, alpha);
        }
    }
}
