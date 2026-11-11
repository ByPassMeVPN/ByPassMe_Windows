using System.Windows;
using ByPassMe.Views;

namespace ByPassMe.Services;

/// <summary>Скрытый WebView2 для автоматической капчи VK (аналог Android CaptchaWebViewManager).</summary>
public sealed class CaptchaWebViewService
{
    public static CaptchaWebViewService Instance { get; } = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string> SolveAsync(string redirectUri, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var app = Application.Current ?? throw new InvalidOperationException("Приложение не запущено");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            await app.Dispatcher.InvokeAsync(() =>
            {
                var win = new CaptchaSolverWindow(redirectUri, tcs);
                win.Show();
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            await using (timeout.Token.Register(() =>
                tcs.TrySetException(new TimeoutException("Таймаут капчи (45с)"))))
            {
                return await tcs.Task;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
