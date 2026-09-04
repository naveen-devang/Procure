using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;

namespace Procure.Services
{
    public class ModalErrorHandler : IErrorHandler
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public void HandleError(Exception ex)
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try
                {
                    await _semaphore.WaitAsync();

                    // Shell.Current can still be null this early in startup - exactly when a load
                    // race is most likely. Used to just drop the error on the floor there: no
                    // dialog, no log, nothing to see. A few short retries covers the shell finishing
                    // construction; logging covers the rest so a failure is never fully invisible.
                    for (var attempt = 0; Shell.Current is null && attempt < 10; attempt++)
                        await Task.Delay(200);

                    if (Shell.Current != null)
                    {
                        await Shell.Current.DisplayAlertAsync("Error", ex.Message, "OK");
                    }
                    else
                    {
                        Procure.Utilities.CrashLog.Write("ModalErrorHandler: no Shell to show error on", ex);
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            });
        }
    }
}