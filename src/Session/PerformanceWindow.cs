using System;

namespace FreeWorldMultiplayer
{
    // A bounded, allocation-free-on-record histogram. Percentiles are approximate upper
    // bucket bounds, not raw samples or frame-time benchmark claims.
    internal sealed class PerformanceWindow
    {
        private static readonly double[] BoundsMs =
            { 0.25, 0.5, 1, 2, 4, 8, 16, 33, 50, 100, 250, 500, 1000 };
        private readonly long[] counts = new long[BoundsMs.Length + 1];
        internal long Samples { get; private set; }
        internal long TotalBytes { get; private set; }
        internal long MaxBytes { get; private set; }
        internal double MaxMs { get; private set; }

        internal void Record(double elapsedMs, long bytes)
        {
            if (Double.IsNaN(elapsedMs) || Double.IsInfinity(elapsedMs) || elapsedMs < 0)
                throw new ArgumentOutOfRangeException(nameof(elapsedMs));
            int index = 0;
            while (index < BoundsMs.Length && elapsedMs > BoundsMs[index]) index++;
            counts[index]++;
            Samples++;
            if (elapsedMs > MaxMs) MaxMs = elapsedMs;
            if (bytes > 0)
            {
                TotalBytes += bytes;
                if (bytes > MaxBytes) MaxBytes = bytes;
            }
        }

        internal double UpperPercentile(int percentile)
        {
            if (percentile < 1 || percentile > 100) throw new ArgumentOutOfRangeException(nameof(percentile));
            if (Samples == 0) return 0;
            long atLeast = (Samples / 100) * percentile + ((Samples % 100) * percentile + 99) / 100;
            long seen = 0;
            for (int i = 0; i < counts.Length; i++)
            {
                seen += counts[i];
                if (seen >= atLeast) return i < BoundsMs.Length ? BoundsMs[i] : MaxMs;
            }
            throw new InvalidOperationException("Performance histogram is inconsistent.");
        }

        internal void Clear()
        {
            Array.Clear(counts, 0, counts.Length);
            Samples = TotalBytes = MaxBytes = 0;
            MaxMs = 0;
        }
    }

}
