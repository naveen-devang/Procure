using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Procure.Services
{
    public class MemoryOptimizerService : IMemoryOptimizerService, IDisposable
    {
        private readonly Timer _periodicMonitor;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(4);
        private readonly TimeSpan _idleThreshold = TimeSpan.FromSeconds(4);
        private long _lastActivityTimestamp;
        private int _hasTrimmedForCurrentIdle;
        private int _isTrimming;
        private bool _disposed;

#if WINDOWS
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);
#endif

        public MemoryOptimizerService()
        {
            _lastActivityTimestamp = Stopwatch.GetTimestamp();
            _hasTrimmedForCurrentIdle = 0;

            // Continuous background timer checking idle state every 4s
            _periodicMonitor = new Timer(OnPeriodicMonitorCallback, null, _checkInterval, _checkInterval);
        }

        public void RecordActivity()
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _lastActivityTimestamp, Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _hasTrimmedForCurrentIdle, 0);
        }

        private void OnPeriodicMonitorCallback(object? state)
        {
            if (_disposed) return;

            var lastActivity = Interlocked.Read(ref _lastActivityTimestamp);
            var elapsed = Stopwatch.GetElapsedTime(lastActivity);

            if (elapsed >= _idleThreshold)
            {
                // Only trim once per idle period to avoid continuous CPU usage
                if (Interlocked.CompareExchange(ref _hasTrimmedForCurrentIdle, 1, 0) == 0)
                {
                    TrimMemory();
                }
            }
        }

        public void TrimMemory()
        {
            if (_disposed) return;

            // Ensure only one trim runs concurrently
            if (Interlocked.CompareExchange(ref _isTrimming, 1, 0) != 0) return;

            Task.Run(() =>
            {
                try
                {
                    // Aggressive Gen 2 compacting garbage collection
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

#if WINDOWS
                    try
                    {
                        var process = Process.GetCurrentProcess();
                        if (!process.HasExited)
                        {
                            EmptyWorkingSet(process.Handle);
                            SetProcessWorkingSetSize(process.Handle, (IntPtr)(-1), (IntPtr)(-1));
                        }
                    }
                    catch
                    {
                        // Ignore any OS permission or process exit edge cases
                    }
#endif
                }
                finally
                {
                    Interlocked.Exchange(ref _isTrimming, 0);
                }
            });
        }

        public async Task TrimMemoryAsync()
        {
            if (_disposed) return;

            await Task.Run(() =>
            {
                TrimMemory();
            });
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _periodicMonitor.Dispose();
        }
    }
}
