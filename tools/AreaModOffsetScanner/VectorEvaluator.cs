namespace AreaModOffsetScanner
{
    using System;
    using System.Collections.Generic;
    using TEHhub.Offsets.Natives;

    /// <summary>
    ///     Evaluates StdVector structural invariants and detects memory corruption or false positives.
    /// </summary>
    public static class VectorEvaluator
    {
        public const int ModArrayStride = 0x40; // 64 bytes

        public readonly record struct EvaluationResult(
            bool IsStructurallyValid,
            long ElementCount,
            long CapacityCount,
            string FailureReasons);

        public static EvaluationResult Evaluate(StdVector vector, int stride = ModArrayStride)
        {
            var first = vector.First.ToInt64();
            var last = vector.Last.ToInt64();
            var end = vector.End.ToInt64();

            long elementCount = 0;
            long capacityCount = 0;
            var reasons = new List<string>();

            // All zeroes is a valid empty uninitialized vector
            if (first == 0 && last == 0 && end == 0)
            {
                return new EvaluationResult(true, 0, 0, string.Empty);
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

            var diffLastFirst = last - first;
            if (diffLastFirst < 0)
            {
                reasons.Add("Last - First < 0");
            }
            else if (diffLastFirst % stride != 0)
            {
                reasons.Add($"(Last - First) [{diffLastFirst} bytes] not divisible by stride 0x{stride:X2}");
            }
            else
            {
                elementCount = diffLastFirst / stride;
            }

            var diffEndFirst = end - first;
            if (diffEndFirst < 0)
            {
                reasons.Add("End - First < 0");
            }
            else if (diffEndFirst % stride != 0)
            {
                reasons.Add($"(End - First) [{diffEndFirst} bytes] not divisible by stride 0x{stride:X2}");
            }
            else
            {
                capacityCount = diffEndFirst / stride;
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
                return new EvaluationResult(false, elementCount, capacityCount, string.Join("; ", reasons));
            }

            return new EvaluationResult(true, elementCount, capacityCount, string.Empty);
        }
    }
}
