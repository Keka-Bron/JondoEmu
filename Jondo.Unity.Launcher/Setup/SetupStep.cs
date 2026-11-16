using System;
using System.Diagnostics;

namespace Jondo.Unity.Launcher.Setup
{
    /// <summary>Where a step of an installation or an update stands.</summary>
    internal enum StepState
    {
        Pending,
        Running,
        Done,
        Skipped,
        Failed,
    }

    /// <summary>
    /// One step of an installation or an update as the window shows it: its name, how far it has
    /// gone, a line of detail (bytes, speed, the file in hand) and the time it has left.
    /// </summary>
    /// <remarks>
    /// The steps are reported from worker threads; the window reads them on its own timer, so
    /// every field is written whole and read whole and nothing here touches the interface.
    /// </remarks>
    internal sealed class SetupStep
    {
        public SetupStep(string key, string title, double weight, TimeSpan expected)
        {
            Key = key;
            Title = title;
            Weight = weight;
            Expected = expected;
            _eta = new EtaEstimator(expected);
        }

        /// <summary>A stable name for logs and tests: «emulator», «melonloader», «generation»…</summary>
        public string Key { get; }

        /// <summary>The name the player reads, in the launcher's language.</summary>
        public string Title { get; set; }

        /// <summary>Its share of the whole bar, against the other steps' weights.</summary>
        public double Weight { get; }

        /// <summary>How long it usually takes, for the estimate before it has measured anything.</summary>
        public TimeSpan Expected { get; }

        public StepState State { get; private set; } = StepState.Pending;

        /// <summary>From 0 to 1.</summary>
        public double Fraction { get; private set; }

        /// <summary>«312 MB of 540 MB · 8.2 MB/s», the file being written, the phase of the generator…</summary>
        public string Detail { get; private set; } = "";

        /// <summary>Why it failed, when it did.</summary>
        public string Error { get; private set; } = "";

        private readonly EtaEstimator _eta;
        private readonly Stopwatch _clock = new();

        /// <summary>The time it has been running.</summary>
        public TimeSpan Elapsed => _clock.Elapsed;

        /// <summary>The time it has left: measured once it moves, expected until then.</summary>
        public TimeSpan? Remaining => State switch
        {
            StepState.Running => _eta.Remaining(Fraction, _clock.Elapsed),
            StepState.Pending => Expected,
            _ => TimeSpan.Zero,
        };

        public void Start(string detail = "")
        {
            State = StepState.Running;
            Fraction = 0;
            Detail = detail;
            Error = "";
            _clock.Restart();
        }

        /// <summary>Moves it on. A fraction lower than the one it had is kept as it was: the bar never goes back.</summary>
        public void Report(double fraction, string? detail = null)
        {
            if (State != StepState.Running) return;
            Fraction = Math.Max(Fraction, Math.Clamp(fraction, 0, 1));
            if (detail != null) Detail = detail;
        }

        /// <summary>A new line of detail without moving the bar.</summary>
        public void Say(string detail)
        {
            if (State == StepState.Running) Detail = detail;
        }

        public void Finish(string detail = "")
        {
            State = StepState.Done;
            Fraction = 1;
            Detail = detail;
            _clock.Stop();
        }

        public void Skip(string detail = "")
        {
            State = StepState.Skipped;
            Fraction = 1;
            Detail = detail;
        }

        public void Fail(string error)
        {
            State = StepState.Failed;
            Error = error;
            _clock.Stop();
        }
    }

    /// <summary>
    /// The time left of something that moves from 0 to 1: at the pace measured so far, smoothed so
    /// the figure does not jump with every report, and the expected duration until there is a pace.
    /// </summary>
    internal sealed class EtaEstimator
    {
        private readonly TimeSpan _expected;
        private double _smoothedRate = -1;   // fraction per second
        private double _lastFraction;
        private TimeSpan _lastTime;

        public EtaEstimator(TimeSpan expected) => _expected = expected;

        public TimeSpan? Remaining(double fraction, TimeSpan elapsed)
        {
            if (fraction >= 1) return TimeSpan.Zero;
            double dt = (elapsed - _lastTime).TotalSeconds;
            if (dt >= 0.5)
            {
                double rate = (fraction - _lastFraction) / dt;
                if (rate > 0)
                    _smoothedRate = _smoothedRate < 0 ? rate : _smoothedRate * 0.8 + rate * 0.2;
                _lastFraction = fraction;
                _lastTime = elapsed;
            }

            // Before anything has been measured, or barely: what the step is expected to take,
            // minus what has gone by.
            if (_smoothedRate <= 0 || fraction < 0.02)
            {
                var left = _expected - elapsed;
                return left > TimeSpan.FromSeconds(5) ? left : null;
            }

            // The average since the start weighs in too: a burst or a stall does not swing it.
            double overall = elapsed.TotalSeconds > 0 ? fraction / elapsed.TotalSeconds : _smoothedRate;
            double pace = (_smoothedRate + overall) / 2;
            double seconds = (1 - fraction) / pace;
            return seconds is > 0 and < 86400 * 2 ? TimeSpan.FromSeconds(seconds) : null;
        }
    }
}
