using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Soda;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using QRCoder;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace Klang_WinUI.Views;

/// <summary>Isolated Passport login. Only the validated current login is committed by SodaService.</summary>
public sealed class SodaLoginWindow : Window
{
    private const string LoginDocument = "https://api.qishui.com/klang-login";
    private const string Origin = "https://api.qishui.com";
    private static SodaLoginWindow? _active;
    private readonly Window _owner;
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { Text = "正在准备汽水登录…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 12, 20, 12) };
    private readonly ProgressBar _progress = new() { IsIndeterminate = true, Height = 2 };
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<(Window Window, WebView2 Web)> _popups = new();
    private readonly string _temporaryProfile;
    private CoreWebView2Environment? _environment;
    private string _html = "";
    private string _lastQr = "";
    private bool _closed;
    private bool _committing;

    private SodaLoginWindow(Window owner)
    {
        _owner = owner;
        _temporaryProfile = Path.Combine(StateService.ResolveDataDirectory(), "soda", "login-temp-" + Guid.NewGuid().ToString("N"));
        Title = "登录汽水音乐";
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(_status);
        Grid.SetRow(_web, 1); root.Children.Add(_web);
        Grid.SetRow(_progress, 2); root.Children.Add(_progress);
        Content = root;
        ResizeAndOwn(this, owner, 500, 710);
        Closed += OnClosed;
        owner.Closed += OwnerClosed;
    }

    /// <summary>Called on the UI thread. The caller retains its previous account on cancel/failure.</summary>
    public static Task<bool> ShowAsync(Window owner)
    {
        if (_active != null)
        {
            _active.Activate();
            return _active._finished.Task;
        }
        var window = _active = new SodaLoginWindow(owner);
        window.Activate();
        _ = window.InitializeAsync();
        return window._finished.Task;
    }

    private async Task InitializeAsync()
    {
        try
        {
            var device = await SodaService.Instance.PrepareAsync(_cancel.Token);
            if (_closed) return;
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "Assets", "SodaLogin", "login.js");
            var script = await File.ReadAllTextAsync(scriptPath, _cancel.Token);
            var config = JsonSerializer.Serialize(new { deviceId = device.DeviceId, installId = device.InstallId,
                versionCode = device.VersionCode, computerName = Environment.MachineName });
            _html = "<!doctype html><html lang='zh-CN'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>" +
                "<style>:root{color-scheme:light dark;font-family:Segoe UI,Microsoft YaHei,sans-serif}body{margin:0;background:light-dark(#fff,#181818);color:light-dark(#171717,#eee)}main{padding:24px;text-align:center}h1{font-size:24px}p{font-size:14px;line-height:1.7}#qr{display:block;width:264px;height:264px;object-fit:contain;margin:24px auto;background:#fff;border-radius:12px}#qr:not([src]){visibility:hidden}small{opacity:.65}</style></head><body><main>" +
                "<h1>登录汽水音乐</h1><p id='status'>正在连接汽水音乐…</p><img id='qr' alt='汽水音乐登录二维码'><small>请保持窗口打开，完成扫码及安全验证。<br>关闭窗口将取消本次登录。</small></main>" +
                "<script>window.klangLoginConfig=" + config + ";</script><script>" + script + "</script></body></html>";
            Directory.CreateDirectory(_temporaryProfile);
            _environment = await CoreWebView2Environment.CreateWithOptionsAsync(null, _temporaryProfile, new CoreWebView2EnvironmentOptions());
            if (_closed) return;
            await _web.EnsureCoreWebView2Async(_environment);
            if (_closed) return;
            Configure(_web.CoreWebView2);
            _web.CoreWebView2.AddWebResourceRequestedFilter(LoginDocument, CoreWebView2WebResourceContext.Document);
            _web.CoreWebView2.WebResourceRequested += ServeDocument;
            _web.CoreWebView2.Navigate(LoginDocument);
            await ObserveAsync();
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!_closed)
            {
                _progress.IsIndeterminate = false;
                _status.Text = "汽水登录窗口未能初始化。请确认网络及 WebView2 Runtime 可用，关闭后重试。";
            }
        }
    }

    private async void ServeDocument(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!string.Equals(args.Request.Uri, LoginDocument, StringComparison.Ordinal)) return;
        var deferral = args.GetDeferral();
        try
        {
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteString(_html);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            args.Response = _environment!.CreateWebResourceResponse(stream, 200, "OK",
                "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff");
        }
        catch { if (!_closed) _status.Text = "无法加载汽水登录页面，请关闭后重试。"; }
        finally { deferral.Complete(); }
    }

    private void Configure(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.NavigationStarting += (_, e) =>
        {
            if (!AllowedNavigation(e.Uri)) e.Cancel = true;
        };
        core.NewWindowRequested += OpenVerificationPopup;
    }

    private static bool AllowedNavigation(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort) return false;
        var host = uri.Host;
        return host == "api.qishui.com" || host == "bff-pc.qishui.com" || host == "auth.zijieapi.com" ||
            host == "verify.snssdk.com" || host == "verify.zijieapi.com";
    }

    private async void OpenVerificationPopup(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        args.Handled = true;
        if (_closed || _environment == null || !AllowedNavigation(args.Uri) || _popups.Count >= 2) return;
        var deferral = args.GetDeferral();
        Window? popup = null;
        WebView2? web = null;
        try
        {
            popup = new Window { Title = "汽水音乐安全验证" };
            web = new WebView2(); popup.Content = web;
            ResizeAndOwn(popup, this, 500, 710);
            var pair = (popup, web);
            _popups.Add(pair);
            popup.Closed += (_, _) => { _popups.Remove(pair); web.Close(); };
            popup.Activate();
            await web.EnsureCoreWebView2Async(_environment);
            if (_closed) { popup.Close(); return; }
            Configure(web.CoreWebView2);
            args.NewWindow = web.CoreWebView2;
        }
        catch { try { popup?.Close(); } catch { } }
        finally { deferral.Complete(); }
    }

    private async Task ObserveAsync()
    {
        var expires = DateTime.UtcNow.AddMinutes(6);
        while (!_closed && DateTime.UtcNow < expires)
        {
            await Task.Delay(500, _cancel.Token);
            if (_closed || _web.CoreWebView2 == null || !IsLoginDocument(_web.CoreWebView2.Source)) continue;
            JsonDocument? data = null;
            try
            {
                var result = await _web.ExecuteScriptAsync("(()=>{if(location.origin!=='https://api.qishui.com'||location.pathname!=='/klang-login')return null;const s=window.klangLoginState;if(!s)return null;return {status:s.status,code:s.code,qr:typeof s.qr==='string'?s.qr.slice(0,8192):'',session:typeof s.session==='string'?s.session.slice(0,65536):''}})()");
                if (_closed || result.Length > 100000) continue;
                data = JsonDocument.Parse(result);
                var root = data.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var state)) continue;
                var status = state.GetString();
                _progress.IsIndeterminate = status is "loading" or "complete";
                _status.Text = status switch { "waiting" => "请用汽水音乐 App 扫码并确认。", "verifying" => "请在下方完成官方安全验证。",
                    "complete" => "正在校验汽水账号…", "error" => "本次登录未能完成。请查看下方提示，关闭后重试。", _ => "正在连接汽水音乐…" };
                if (status == "waiting" && root.TryGetProperty("qr", out var qrValue))
                {
                    var qr = qrValue.GetString() ?? "";
                    if (qr != _lastQr && IsValidQr(qr))
                    {
                        using var generator = new QRCodeGenerator();
                        using var code = generator.CreateQrCode(qr, QRCodeGenerator.ECCLevel.M);
                        var png = new PngByteQRCode(code).GetGraphic(8);
                        if (_closed) return;
                        await _web.ExecuteScriptAsync("window.klangSetQr?.(" + JsonSerializer.Serialize(qr) + "," + JsonSerializer.Serialize("data:image/png;base64," + Convert.ToBase64String(png)) + ")");
                        _lastQr = qr;
                    }
                }
                if (status == "complete" && !_committing)
                {
                    var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(Origin);
                    if (_closed) return;
                    var parts = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var cookie in cookies)
                        if (cookie.Name.Length <= 128 && ValidCookiePart(cookie.Name, true) && ValidCookiePart(cookie.Value, false)) parts[cookie.Name] = cookie.Value;
                    if (root.TryGetProperty("session", out var fallback) && fallback.GetString() is string raw)
                        foreach (var part in raw.Split(';'))
                        {
                            var split = part.Split('=', 2); if (split.Length != 2) continue;
                            var name = split[0].Trim();
                            if (name is "sessionid" or "sessionid_ss" && ValidCookiePart(split[1], false)) parts[name] = split[1];
                        }
                    if (!parts.ContainsKey("sessionid") && !parts.ContainsKey("sessionid_ss")) continue;
                    var header = string.Join("; ", parts.Select(c => c.Key + "=" + c.Value));
                    if (header.Length > 65536) throw new InvalidDataException();
                    _committing = true;
                    var accepted = await SodaService.Instance.AcceptLoginAsync(header, _cancel.Token);
                    if (_closed) return;
                    if (accepted)
                    {
                        _finished.TrySetResult(true);
                        Close();
                        return;
                    }
                    _status.Text = "汽水账号校验未通过，原账号已保留。请关闭后重新登录。";
                    _progress.IsIndeterminate = false;
                    return;
                }
                if (status == "error") return;
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                if (_committing)
                {
                    if (!_closed) { _status.Text = "汽水账号校验未完成，原账号已保留。请关闭后重试。"; _progress.IsIndeterminate = false; }
                    return;
                }
            }
            finally { data?.Dispose(); }
        }
        if (!_closed) { _progress.IsIndeterminate = false; _status.Text = "本次登录已超时，请关闭窗口后重新登录。"; }
    }

    internal static bool IsLoginDocument(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "api.qishui.com" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/klang-login" && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    internal static bool IsValidQr(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "bff-pc.qishui.com" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.AbsolutePath == "/light/invoke/scan_login" && uri.Fragment.Length == 0 && address.Length <= 8192 &&
        uri.Query.TrimStart('?').Split('&').Count(p => p.StartsWith("token=", StringComparison.Ordinal)) == 1 &&
        uri.Query.TrimStart('?').Split('&').Any(p => p.StartsWith("token=", StringComparison.Ordinal) && p.Length > 6);

    private static bool ValidCookiePart(string value, bool name) => value.Length > 0 && value.Length <= 16384 &&
        !value.Any(c => c <= 32 || c >= 127 || c == ';' || c == '\r' || c == '\n' || name && c == '=');

    private void OwnerClosed(object sender, WindowEventArgs args) { try { Close(); } catch { } }
    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_closed) return;
        _closed = true; _cancel.Cancel();
        _owner.Closed -= OwnerClosed;
        if (ReferenceEquals(_active, this)) _active = null;
        foreach (var popup in _popups.ToArray()) { try { popup.Window.Close(); } catch { } }
        _popups.Clear();
        try { _web.Close(); } catch { }
        _html = "";
        _finished.TrySetResult(false);
        try { _owner.Activate(); } catch { }
        _ = CleanupAsync(_temporaryProfile);
    }

    private static async Task CleanupAsync(string directory)
    {
        // Only this window's generated temporary profile is ever removed.
        var parent = Path.GetFullPath(Path.Combine(StateService.ResolveDataDirectory(), "soda")) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(target).StartsWith("login-temp-", StringComparison.Ordinal)) return;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(1000);
            try { if (Directory.Exists(target)) Directory.Delete(target, true); return; } catch (IOException) { } catch (UnauthorizedAccessException) { return; }
        }
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    private static void ResizeAndOwn(Window window, Window owner, int width, int height)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        SetWindowLongPtr(hwnd, -8, WindowNative.GetWindowHandle(owner));
        var scale = Math.Max(1, GetDpiForWindow(hwnd) / 96.0);
        window.AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
        if (window.AppWindow.Presenter is OverlappedPresenter presenter) { presenter.IsMaximizable = false; presenter.IsResizable = true; }
    }
}
