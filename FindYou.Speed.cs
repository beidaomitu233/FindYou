using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace FindYou
{
    sealed class TransferSpeed
    {
        struct Sample { public double Time; public long Bytes; }
        readonly object gate = new object();
        readonly Func<double> clock;
        readonly List<Sample> samples = new List<Sample>(12);
        long total;
        double lastWrite;

        public TransferSpeed() : this(() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency) { }
        internal TransferSpeed(Func<double> clock)
        {
            this.clock = clock;
            samples.Add(new Sample { Time = clock() });
        }
        void Trim(double now)
        {
            double cutoff = now - 1;
            // Keep the sample just before the one-second window (at most 100 ms older).
            while (samples.Count > 1 && samples[1].Time <= cutoff) samples.RemoveAt(0);
            if (samples[samples.Count - 1].Time < cutoff)
            {
                samples.Clear();
                samples.Add(new Sample { Time = cutoff, Bytes = total });
            }
        }
        void SampleAt(double now)
        {
            if (now - samples[samples.Count - 1].Time >= .1) samples.Add(new Sample { Time = now, Bytes = total });
        }
        public void Add(int bytes)
        {
            lock (gate)
            {
                double now = clock(); Trim(now);
                if (total == 0) { samples.Clear(); samples.Add(new Sample { Time = now }); }
                total += bytes; lastWrite = now; SampleAt(now);
            }
        }
        public long BytesPerSecond
        {
            get
            {
                lock (gate)
                {
                    double now = clock(); Trim(now); SampleAt(now);
                    if (now - lastWrite >= 1) return 0;
                    return (long)((total - samples[0].Bytes) / Math.Max(.25, now - samples[0].Time));
                }
            }
        }
    }
}
