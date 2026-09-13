using System;
using System.Threading;
using System.Threading.Tasks;

namespace Procure.Utilities
{
    public enum UpdateDownloadStatus { Idle, Running, Done, Failed }

    /// <summary>
    /// The one place that owns "is an update download in progress, and how far along". Both the
    /// startup auto-download and the Settings "Download &amp; Install" button run through here, so a
    /// second request while one is already going latches onto the same download instead of starting
    /// a parallel Velopack fetch.
    ///
    /// Deliberately passive: no timer, no background thread of its own. Progress notifications are
    /// throttled (a chatty download can't flood the UI dispatcher) and the whole object is a
    /// semaphore-free lock plus a handful of fields, so it costs nothing at startup.
    /// </summary>
    public sealed class UpdateDownloadCoordinator
    {
        private sealed class InlineProgress : IProgress<double>
        {
            private readonly Action<double> _cb;
            public InlineProgress(Action<double> cb) => _cb = cb;
            public void Report(double value) => _cb(value);
        }

        private readonly object _gate = new();
        private Task? _active;
        private string? _activeTag;

        private long _lastRaiseTick;
        private double _lastRaisedProgress = -1;

        public UpdateDownloadStatus Status { get; private set; } = UpdateDownloadStatus.Idle;
        public double Progress { get; private set; }
        public string? VersionTag { get; private set; }
        public string? Error { get; private set; }

        public bool IsRunning => Status == UpdateDownloadStatus.Running;

        /// <summary>Fires on every status change and on progress at most ~4x/second.</summary>
        public event EventHandler? Changed;

        /// <summary>Runs <paramref name="work"/> once for <paramref name="versionTag"/>. A concurrent
        /// call for the same tag awaits the in-flight download rather than starting another; a call
        /// after a successful download for that tag returns immediately.</summary>
        public Task RunAsync(string versionTag, Func<IProgress<double>, CancellationToken, Task> work, CancellationToken ct = default)
        {
            lock (_gate)
            {
                if (_active is { IsCompleted: false } && string.Equals(_activeTag, versionTag, StringComparison.Ordinal))
                    return _active;

                if (Status == UpdateDownloadStatus.Done && string.Equals(VersionTag, versionTag, StringComparison.Ordinal))
                    return Task.CompletedTask;

                _activeTag = versionTag;
                _active = RunCoreAsync(versionTag, work, ct);
                return _active;
            }
        }

        private async Task RunCoreAsync(string versionTag, Func<IProgress<double>, CancellationToken, Task> work, CancellationToken ct)
        {
            VersionTag = versionTag;
            Error = null;
            Progress = 0;
            _lastRaisedProgress = -1;
            SetStatus(UpdateDownloadStatus.Running);

            var progress = new InlineProgress(p =>
            {
                Progress = p < 0 ? 0 : (p > 1 ? 1 : p);
                MaybeRaiseProgress();
            });

            try
            {
                await work(progress, ct).ConfigureAwait(false);
                Progress = 1;
                SetStatus(UpdateDownloadStatus.Done);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                SetStatus(UpdateDownloadStatus.Failed);
                throw;
            }
        }

        /// <summary>Clears a Failed / Done state back to Idle so a fresh attempt can start. No-op
        /// while a download is running.</summary>
        public void Reset()
        {
            lock (_gate)
            {
                if (_active is { IsCompleted: false }) return;
                _active = null;
                _activeTag = null;
                Progress = 0;
                Error = null;
                SetStatus(UpdateDownloadStatus.Idle);
            }
        }

        private void SetStatus(UpdateDownloadStatus s)
        {
            Status = s;
            _lastRaiseTick = Environment.TickCount64;
            _lastRaisedProgress = Progress;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void MaybeRaiseProgress()
        {
            var now = Environment.TickCount64;
            if (now - _lastRaiseTick < 250 && Progress - _lastRaisedProgress < 0.01 && Progress < 1)
                return;
            _lastRaiseTick = now;
            _lastRaisedProgress = Progress;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
