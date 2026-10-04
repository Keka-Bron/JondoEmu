using System;
using System.Diagnostics;
using System.Threading;

namespace Jondo.Unity.Launcher.Packs
{
    /// <summary>What a pack operation is doing right now.</summary>
    internal enum PackPhase
    {
        /// <summary>Fetching the client version's manifest (about 50 MB, once per version).</summary>
        Manifest,

        /// <summary>Hashing the files already on disk.</summary>
        Checking,

        /// <summary>Fetching bundles.</summary>
        Downloading,

        /// <summary>Hashing each rebuilt file before it is moved into place.</summary>
        Verifying,
    }

    /// <summary>A progress report: bytes done out of bytes to do in the current phase.</summary>
    internal readonly record struct PackProgress(PackPhase Phase, long Done, long Total, double BytesPerSecond)
    {
        public double Fraction => Total <= 0 ? 0 : Math.Clamp(Done / (double)Total, 0, 1);
    }

    /// <summary>
    /// Adds up bytes from several downloads at once and reports a few times a second.
    /// </summary>
    /// <remarks>
    /// Four bundles arrive in parallel as chunks of about 40 KB, which is thousands of updates a
    /// second on a good line; forwarding each one would flood the window's thread. A retried
    /// bundle subtracts what it had counted, so the bar never passes 100 %.
    /// </remarks>
    internal sealed class ProgressMeter
    {
        private static readonly long Interval = Stopwatch.Frequency / 5;

        private readonly IProgress<PackProgress>? _sink;
        private readonly object _gate = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private PackPhase _phase;
        private long _done;
        private long _total;
        private long _lastReport = long.MinValue;
        private long _speedMark;
        private long _speedDone;
        private double _speed;

        public ProgressMeter(IProgress<PackProgress>? sink) => _sink = sink;

        public void Start(PackPhase phase, long total, long done = 0)
        {
            lock (_gate)
            {
                _phase = phase;
                _total = total;
                Interlocked.Exchange(ref _done, done);
                _speedMark = _clock.ElapsedTicks;
                _speedDone = done;
                _speed = 0;
            }
            Report(force: true);
        }

        public void Add(long bytes)
        {
            Interlocked.Add(ref _done, bytes);
            Report(force: false);
        }

        public void Report(bool force)
        {
            if (_sink == null) return;

            PackProgress report;
            lock (_gate)
            {
                long now = _clock.ElapsedTicks;
                if (!force && now - _lastReport < Interval) return;
                _lastReport = now;

                long done = Interlocked.Read(ref _done);
                double seconds = (now - _speedMark) / (double)Stopwatch.Frequency;
                if (seconds >= 1)
                {
                    // A plain average over the last second or so: steady enough to read.
                    _speed = Math.Max(0, (done - _speedDone) / seconds);
                    _speedMark = now;
                    _speedDone = done;
                }

                report = new PackProgress(_phase, done, _total, _speed);
            }

            _sink.Report(report);
        }
    }
}
