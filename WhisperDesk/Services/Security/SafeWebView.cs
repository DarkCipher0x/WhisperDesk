using System.Diagnostics;
using System.IO;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace WhisperDesk.Services.Security;

/// <summary>
/// The built-in web views (user guide, privacy page, analysis preview) only ever show local content. They run in
/// InPrivate mode (nothing is kept on disk), with Edge's background networking, component updates and reporting switched
/// off, no dev tools, and every navigation outside the page shown is cancelled. Links to http(s) sites open in the user's
/// own browser; any other scheme (file:, ms-settings:, custom protocol handlers…) is ignored.
/// </summary>
public static class SafeWebView
{
    private const string Arguments =
        "--disable-background-networking --disable-component-update --disable-domain-reliability --no-pings " +
        "--disable-sync --disable-features=msSmartScreenProtection,AutofillServerCommunication,OptimizationHints,MediaRouter " +
        "--metrics-recording-only";

    private static CoreWebView2Environment? _env;

    public static async Task<CoreWebView2Environment> EnvironmentAsync()
    {
        if (_env != null) return _env;
        var options = new CoreWebView2EnvironmentOptions(Arguments) { AllowSingleSignOnUsingOSPrimaryAccount = false };
        _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Vault.LocalData, "webview"), options);
        return _env;
    }

    /// <param name="allowed">The one document this view may show (file URI of the guide), or null for NavigateToString content.</param>
    public static async Task InitAsync(WebView2 view, bool scripts, Uri? allowed)
    {
        var env = await EnvironmentAsync();
        var controller = env.CreateCoreWebView2ControllerOptions();
        controller.IsInPrivateModeEnabled = true;
        await view.EnsureCoreWebView2Async(env, controller);
        var core = view.CoreWebView2;
        var s = core.Settings;
        s.AreDevToolsEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsScriptEnabled = scripts;
        s.IsWebMessageEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = true;
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;
            if (allowed != null && Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.IsFile &&
                string.Equals(Path.GetFullPath(u.LocalPath), Path.GetFullPath(allowed.LocalPath), StringComparison.OrdinalIgnoreCase)) return;
            e.Cancel = true;
            OpenExternal(e.Uri);
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
    }

    /// <summary>Opens http(s) links in the default browser; ignores every other scheme.</summary>
    public static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }
}
