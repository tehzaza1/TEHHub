namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    ///     Diagnostic data model for an evaluated memory candidate offset.
    ///     Contains only JSON-safe primitive and string properties.
    /// </summary>
    public sealed class AreaModCandidate
    {
        public int Offset { get; init; }

        public string OffsetHex => $"0x{this.Offset:X4}";

        public string First { get; init; } = "0x0";

        public string Last { get; init; } = "0x0";

        public string End { get; init; } = "0x0";

        public bool IsStructurallyValid { get; init; }

        public string StructuralFailureReason { get; init; } = string.Empty;

        public long ElementCount { get; init; }

        public long CapacityCount { get; init; }

        public int AttemptedEntries { get; init; }

        public int ReadableEntries { get; init; }

        public int PlausibleModsPtrCount { get; init; }

        public int ReadableNameChainCount { get; init; }

        public int PlausibleRawNameCount { get; init; }

        public List<string> SampleRawNames { get; init; } = new();

        public string SpecialTag { get; init; } = string.Empty;

        public bool IsSpecialOffset => !string.IsNullOrEmpty(this.SpecialTag);
    }
}
