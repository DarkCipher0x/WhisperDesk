using System.Net.Http;
using System.Net.Http.Json;
using WhisperDesk.Addons;

namespace WhisperDesk.Services.Addons.BuiltIn;

/// <summary>POSTs transcript events as JSON to a URL — connect Zapier, n8n, Home Assistant, Discord bots, your own server…</summary>
public sealed class WebhookAddon : IWhisperDeskAddon
{
    private static readonly HttpClient Http = Security.NetworkGate.Create(Security.NetFeature.Webhook, TimeSpan.FromSeconds(10));
    private IAddonHost? _host;

    public const string AddonId = "whisperdesk.webhook";
    public string Id => AddonId;
    public string Name => "Webhook";
    public string Description =>
        "Sends each transcript line (and finished transcripts) as JSON in an HTTP POST to the URL you choose. " +
        "This sends your transcripts off this PC to that address: use it only with a service you trust. Internet addresses must use https://. " +
        "Body: {\"type\":\"line\"|\"saved\"|\"started\"|\"stopped\", \"data\":{…}}.";
    public string Author => "darkcipher0x";

    public IReadOnlyList<AddonOption> Options { get; } = new[]
    {
        new AddonOption("url", "Webhook URL", "", "e.g. https://example.com/hooks/whisperdesk. Stored in Windows Credential Manager (it often contains a token).") { IsSecret = true },
        new AddonOption("lines", "Send each line (true / false)", "true"),
        new AddonOption("saved", "Send finished transcripts (true / false)", "true"),
        new AddonOption("liveOnly", "Lines from live sessions only (true / false)", "true"),
    };

    public void Start(IAddonHost host)
    {
        _host = host;
        if (string.IsNullOrWhiteSpace(host.GetOption("url"))) host.Log("No URL set yet; nothing will be sent.");
        host.LineAdded += OnLine;
        host.TranscriptSaved += OnSaved;
        host.SessionStarted += OnStarted;
        host.SessionStopped += OnStopped;
    }

    public void Stop()
    {
        if (_host == null) return;
        _host.LineAdded -= OnLine;
        _host.TranscriptSaved -= OnSaved;
        _host.SessionStarted -= OnStarted;
        _host.SessionStopped -= OnStopped;
    }

    private void OnLine(object? s, TranscriptLineEvent e)
    {
        if (_host!.GetOption("lines") == "false") return;
        if (!e.IsLive && _host.GetOption("liveOnly") != "false") return;
        Post("line", e);
    }

    private void OnSaved(object? s, TranscriptSavedEvent e) { if (_host!.GetOption("saved") != "false") Post("saved", e); }
    private void OnStarted(object? s, SessionEvent e) => Post("started", e);
    private void OnStopped(object? s, SessionEvent e) => Post("stopped", e);

    private void Post(string type, object data)
    {
        var url = _host?.GetOption("url");
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            using var res = Http.PostAsJsonAsync(url, new { type, data, app = "WhisperDesk" }).GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) _host!.Log($"{type}: HTTP {(int)res.StatusCode}");
        }
        catch (Exception ex)
        {
            _host!.Log($"{type}: {ex.Message}");
        }
    }
}
