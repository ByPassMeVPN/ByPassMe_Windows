using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace ByPassMe.Views;

/// <summary>Скрытый WebView2 — автоклик по checkbox, без UI (как Android CaptchaWebViewManager).</summary>
public partial class CaptchaSolverWindow : Window
{
    private static readonly string[] ChromeBuilds =
    [
        "146.0.0.0", "145.0.6422.60", "144.0.6367.78", "143.0.6312.99"
    ];

    private readonly string _redirectUri;
    private readonly TaskCompletionSource<string> _tcs;
    private readonly Random _rng = new();
    private bool _finished;

    public CaptchaSolverWindow(string redirectUri, TaskCompletionSource<string> tcs)
    {
        InitializeComponent();
        _redirectUri = redirectUri;
        _tcs = tcs;

        // Полностью скрытое окно — WebView2 должен рендерить, но пользователь не видит
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStyle = WindowStyle.None;
        Left = -12000;
        Top = -12000;
        Width = 380;
        Height = 420;
        Opacity = 0;

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            if (!_finished) FinishWithError("капча отменена");
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await WebView.EnsureCoreWebView2Async();
            var core = WebView.CoreWebView2 ?? throw new InvalidOperationException("WebView2 не инициализирован");

            var chromeBuild = ChromeBuilds[_rng.Next(ChromeBuilds.Length)];
            core.Settings.UserAgent =
                $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{chromeBuild} Safari/537.36";
            core.Settings.IsScriptEnabled = true;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += OnNavigationCompleted;
            core.DOMContentLoaded += async (_, _) => await InjectBridgeAsync();
            core.Navigate(_redirectUri);
        }
        catch (Exception ex)
        {
            FinishWithError(ex.Message);
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_finished || WebView.CoreWebView2 == null || !e.IsSuccess) return;
        var url = WebView.Source?.ToString() ?? "";
        if (!url.Contains("not_robot", StringComparison.OrdinalIgnoreCase)
            && !url.Contains("captcha", StringComparison.OrdinalIgnoreCase))
            return;

        await InjectBridgeAsync();
        await Task.Delay(2500 + _rng.Next(800));
        if (_finished) return;
        await AutoSolveLoopAsync();
    }

    private async Task AutoSolveLoopAsync()
    {
        for (var i = 0; i < 12 && !_finished; i++)
        {
            await TryAutoCheckboxClickAsync();
            await Task.Delay(500 + _rng.Next(400));
        }
    }

    private async Task InjectBridgeAsync()
    {
        if (WebView.CoreWebView2 == null) return;
        const string bridge = """
            (function() {
                if (window.__bypassme_interceptor) return;
                window.__bypassme_interceptor = true;
                function send(obj) { window.chrome.webview.postMessage(obj); }
                window.WdttCaptcha = {
                    onSuccess: function(token) { send({ type: 'success', token: token }); },
                    onError: function(msg) { send({ type: 'error', msg: String(msg) }); }
                };
                function handleCheck(data) {
                    if (!data) return;
                    if (data.response && data.response.success_token) {
                        window.WdttCaptcha.onSuccess(data.response.success_token);
                    } else if (data.error) {
                        window.WdttCaptcha.onError(JSON.stringify(data.error));
                    }
                }
                const origFetch = window.fetch;
                window.fetch = async function() {
                    const args = arguments;
                    const url = args[0] || '';
                    if (typeof url === 'string' && url.includes('captchaNotRobot.check')) {
                        const response = await origFetch.apply(this, args);
                        const clone = response.clone();
                        try { handleCheck(await clone.json()); } catch (e) {}
                        return response;
                    }
                    return origFetch.apply(this, args);
                };
                const origXHROpen = XMLHttpRequest.prototype.open;
                const origXHRSend = XMLHttpRequest.prototype.send;
                XMLHttpRequest.prototype.open = function(method, url) {
                    this._bypassme_url = url;
                    return origXHROpen.apply(this, arguments);
                };
                XMLHttpRequest.prototype.send = function() {
                    const xhr = this;
                    if (xhr._bypassme_url && xhr._bypassme_url.includes('captchaNotRobot.check')) {
                        xhr.addEventListener('load', function() {
                            try { handleCheck(JSON.parse(xhr.responseText)); } catch (e) {}
                        });
                    }
                    return origXHRSend.apply(this, arguments);
                };
            })();
            """;
        await WebView.CoreWebView2.ExecuteScriptAsync(bridge);
    }

    private async Task TryAutoCheckboxClickAsync()
    {
        if (WebView.CoreWebView2 == null || _finished) return;
        const string findLabelJs = """
            (function() {
                var el = document.querySelector('label.vkc__Checkbox-module__Checkbox');
                if (!el) el = document.querySelector('label[for="not-robot-captcha-checkbox"]');
                if (!el) el = document.getElementById('not-robot-captcha-checkbox');
                if (!el) return 'not_found';
                var rect = el.getBoundingClientRect();
                if (rect.width < 5 || rect.height < 5) return 'not_found';
                return rect.left + ',' + rect.top + ',' + rect.width + ',' + rect.height;
            })();
            """;
        var raw = await WebView.CoreWebView2.ExecuteScriptAsync(findLabelJs);
        var result = UnquoteJs(raw);

        if (result == "not_found")
        {
            await WebView.CoreWebView2.ExecuteScriptAsync(
                "(function(){var el=document.querySelector('label.vkc__Checkbox-module__Checkbox')||document.getElementById('not-robot-captcha-checkbox');if(el){el.click();return 'clicked';}return 'nothing';})();");
            return;
        }

        var parts = result.Split(',');
        if (parts.Length < 4) return;
        if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var left)) return;
        if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var top)) return;
        if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var width)) return;
        if (!float.TryParse(parts[3], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var height)) return;

        var x = left + width * (0.15f + (float)_rng.NextDouble() * 0.7f);
        var y = top + height * (0.25f + (float)_rng.NextDouble() * 0.5f);
        await WebView.CoreWebView2.ExecuteScriptAsync(
            $"(function(x,y){{var el=document.elementFromPoint(x,y);if(!el)return;var ev=new MouseEvent('click',{{bubbles:true,cancelable:true,clientX:x,clientY:y}});el.dispatchEvent(ev);}})({x.ToString(System.Globalization.CultureInfo.InvariantCulture)},{y.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = ParseMessage(e);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl)) return;

            switch (typeEl.GetString())
            {
                case "success":
                    var token = root.TryGetProperty("token", out var tok) ? tok.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(token)) FinishWithSuccess(token);
                    break;
                case "error":
                    var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() ?? "captcha error" : "captcha error";
                    FinishWithError(msg);
                    break;
            }
        }
        catch (Exception ex)
        {
            FinishWithError(ex.Message);
        }
    }

    private static JsonDocument ParseMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        // WebView2 может вернуть объект или JSON-строку внутри строки
        try
        {
            var asString = e.TryGetWebMessageAsString();
            if (!string.IsNullOrEmpty(asString))
                return JsonDocument.Parse(asString);
        }
        catch { /* fall through */ }

        var json = e.WebMessageAsJson;
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.String)
        {
            var inner = doc.RootElement.GetString() ?? "{}";
            doc.Dispose();
            return JsonDocument.Parse(inner);
        }
        return doc;
    }

    private static string UnquoteJs(string? raw) =>
        string.IsNullOrEmpty(raw) ? "" : raw.Trim('"').Replace("\\\"", "\"", StringComparison.Ordinal);

    private void FinishWithSuccess(string token)
    {
        if (_finished) return;
        _finished = true;
        _tcs.TrySetResult(token);
        try { Close(); } catch { /* ignore */ }
    }

    private void FinishWithError(string message)
    {
        if (_finished) return;
        _finished = true;
        _tcs.TrySetException(new InvalidOperationException(message));
        try { Close(); } catch { /* ignore */ }
    }
}
