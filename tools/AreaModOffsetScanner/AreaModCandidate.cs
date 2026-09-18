namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    ///     Diagnostic data model for an evaluated memory candidate offset across all 3 element hypotheses.
    ///     Contains only JSON-safe primitive and string properties.
    /// </summary>
    public sealed class AreaModCandidate
    {
        public int Offset { get; init; }

        public string OffsetHex => $"0x{this.Offset:X4}";

        public string First { get; init; } = "0x0";

        public string Last { get; init; } = "0x0";

        public string End { get; init; } = "0x0";

        public bool IsBasicValid { get; init; }

        public string StructuralFailureReason { get; init; } = string.Empty;

        public long UsedBytes { get; init; }

        public long CapacityBytes { get; init; }

        public ModRecordResolver.HypothesisEvaluation HypothesisA { get; init; } = new();

        public ModRecordResolver.HypothesisEvaluation HypothesisB { get; init; } = new();

        public ModRecordResolver.HypothesisEvaluation HypothesisC { get; init; } = new();

        public string BestHypothesisCode { get; init; } = "None";

        public int BestHypothesisAsciiCount { get; init; }

        public string SpecialTag { get; init; } = string.Empty;

        public bool IsSpecialOffset => !string.IsNullOrEmpty(this.SpecialTag);
    }
}
