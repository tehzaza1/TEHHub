namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using TEHhub.Offsets.Natives;

    /// <summary>
    ///     Evaluates StdVector structural invariants and detects memory corruption or false positives.
    ///     Separates basic vector structure validity (bounds and ordering) from element stride assumptions.
    /// </summary>
    public static class VectorEvaluator
    {
        public const int ModArrayStride = 0x40; // 64 bytes
        public const int PointerStride = 0x08;  // 8 bytes

        public readonly record struct BasicEvaluationResult(
            bool IsBasicValid,
            long UsedBytes,
            long CapacityBytes,
            string FailureReasons);

        public readonly record struct EvaluationResult(
            bool IsStructurallyValid,
            long ElementCount,
            long CapacityCount,
            long UsedBytes,
            long CapacityBytes,
            string FailureReasons);

        /// <summary>
        ///     Evaluates standard vector ordering and pointer validity independent of element size.
        /// </summary>
        public static BasicEvaluationResult EvaluateBasic(StdVector vector)
        {
            var first = vector.First.ToInt64();
            var last = vector.Last.ToInt64();
            var end = vector.End.ToInt64();

            var reasons = new List<string>();

            // All zeroes is a valid empty uninitialized vector
            if (first == 0 && last == 0 && end == 0)
            {
                return new BasicEvaluationResult(true, 0, 0, string.Empty);
            }

            if (first == 0)
            {
                reasons.Add("First is NULL (0x0)");
            }

            if (last == 0 && first != 0)
            {
                reasons.Add("Last is NULL while First is non-null");
            }

            if (end == 0 && first != 0)
            {
                reasons.Add("End is NULL while First is non-null");
            }

            if (first > last)
            {
                reasons.Add("First > Last (ordering violation)");
            }

            if (last > end)
            {
                reasons.Add("End < Last (capacity ordering violation)");
            }

            if (first > end)
            {
                reasons.Add("End < First (negative capacity)");
            }

            var usedBytes = last - first;
            var capBytes = end - first;

            if (usedBytes < 0)
            {
                reasons.Add($"UsedBytes ({usedBytes}) < 0");
            }

            if (capBytes < 0)
            {
                reasons.Add($"CapacityBytes ({capBytes}) < 0");
            }

            if (first != 0 && !NativeMemoryReader.IsValidAddress(vector.First))
            {
                reasons.Add($"First address 0x{first:X} is outside user-mode address space");
            }

            if (last != 0 && !NativeMemoryReader.IsValidAddress(vector.Last))
            {
                reasons.Add($"Last address 0x{last:X} is outside user-mode address space");
            }

            if (end != 0 && !NativeMemoryReader.IsValidAddress(vector.End))
            {
                reasons.Add($"End address 0x{end:X} is outside user-mode address space");
            }

            if (reasons.Count > 0)
            {
                return new BasicEvaluationResult(false, Math.Max(0, usedBytes), Math.Max(0, capBytes), string.Join("; ", reasons));
            }

            return new BasicEvaluationResult(true, usedBytes, capBytes, string.Empty);
        }

        /// <summary>
        ///     Evaluates basic validity and checks divisibility by the specified element stride.
        /// </summary>
        public static EvaluationResult Evaluate(StdVector vector, int stride = ModArrayStride)
        {
            var basic = EvaluateBasic(vector);
            if (!basic.IsBasicValid)
            {
                return new EvaluationResult(false, 0, 0, basic.UsedBytes, basic.CapacityBytes, basic.FailureReasons);
            }

            if (stride <= 0)
            {
                return new EvaluationResult(false, 0, 0, basic.UsedBytes, basic.CapacityBytes, $"Invalid stride: {stride}");
            }

            var reasons = new List<string>();
            long elementCount = 0;
            long capacityCount = 0;

            if (basic.UsedBytes % stride != 0)
            {
                reasons.Add($"UsedBytes ({basic.UsedBytes}) not divisible by stride 0x{stride:X2}");
            }
            else
            {
                elementCount = basic.UsedBytes / stride;
            }

            if (basic.CapacityBytes % stride != 0)
            {
                reasons.Add($"CapacityBytes ({basic.CapacityBytes}) not divisible by stride 0x{stride:X2}");
            }
            else
            {
                capacityCount = basic.CapacityBytes / stride;
            }

            if (reasons.Count > 0)
            {
                return new EvaluationResult(false, elementCount, capacityCount, basic.UsedBytes, basic.CapacityBytes, string.Join("; ", reasons));
            }

            return new EvaluationResult(true, elementCount, capacityCount, basic.UsedBytes, basic.CapacityBytes, string.Empty);
        }
    }
}
