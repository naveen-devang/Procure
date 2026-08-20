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
                    if (Shell.Current != null)
                    {
                        await Shell.Current.DisplayAlertAsync("Error", ex.Message, "OK");
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