using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace WhisperDesk.Services.Security;

/// <summary>The features that are allowed to use the network. Every outbound request is attributed to one of them.</summary>
public static class NetFeature
{
    public const string ModelDownload = "Model download";
    public const string LocalAi = "Local AI (Ollama on this PC)";
    public const string RemoteAi = "AI server on another computer";
    public const string Webhook = "Webhook add-on";
}

/// <summary>One outbound request, as shown in Settings → Security &amp; privacy → Network activity. No URL query, no body.</summary>
public sealed record NetworkEvent(DateTimeOffset At, string Feature, string Method, string Host, string Path, int? Status, long? Bytes, string? Note)
{
    public string AtText => At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string Summary => $"{Method} {Host}{Path}" + (Status is { } s ? $" → {s}" : "") + (Bytes is > 0 ? $" · {ModelManager.FormatSize(Bytes.Value)}" : "") + (Note != null ? $" · {Note}" : "");
}

/// <summary>Raised when a request is refused by the user's network settings.</summary>
public sealed class NetworkBlockedException(string message) : InvalidOperationException(message);

/// <summary>
/// The single way WhisperDesk talks to the network. Every <see cref="HttpClient"/> comes from <see cref="Create"/>, so each
/// request is checked against the user's policy for its feature, attributed to that feature and recorded in the activity
/// log. Nothing in WhisperDesk's core path needs the network; nothing is requested at start-up.
/// </summary>
public static class NetworkGate
{
    private const int MaxLog = 500;
    private static readonly object Lock = new();
    private static readonly List<NetworkEvent> Log = LoadLog();
    private static readonly HashSet<string> ApprovedThisSession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised (on any thread) after a request was recorded.</summary>
    public static event Action<NetworkEvent>? Recorded;

    private static string LogPath => Path.Combine(WhisperDesk.Services.AppPaths.LocalData, "network-log.json");

    public static IReadOnlyList<NetworkEvent> Activity { get { lock (Lock) return Log.ToList(); } }

    /// <summary>An HttpClient whose requests are policy-checked and logged as <paramref name="feature"/>.</summary>
    public static HttpClient Create(string feature, TimeSpan timeout)
    {
        var inner = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true, MaxAutomaticRedirections = 5,
            UseCookies = false,                                   // no cookies kept between requests
        };
        var client = new HttpClient(new GateHandler(feature) { InnerHandler = inner }) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("WhisperDesk");   // generic: no version, OS or machine details
        return client;
    }

    // ---------------- policy ----------------

    /// <summary>Loopback only: 127.0.0.0/8, ::1, "localhost".</summary>
    public static bool IsLoopback(string host)
    {
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>Private LAN addresses (RFC 1918, link-local, IPv6 ULA) and *.local names: not the internet.</summary>
    public static bool IsPrivateNetwork(string host)
    {
        host = host.Trim('[', ']');
        if (IsLoopback(host) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6) return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
    }

    /// <summary>
    /// Why a request may not be made, or null when it may. Plain HTTP is only allowed to this PC and the local network;
    /// model downloads need the user's consent (this session or "always"); other computers' AI servers need the switch.
    /// </summary>
    public static string? Refusal(string feature, Uri uri)
    {
        var s = AppSettings.Current;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return $"Only http(s) is allowed ({uri.Scheme}).";
        if (uri.Scheme == Uri.UriSchemeHttp && !IsPrivateNetwork(uri.Host))
            return $"Unencrypted http:// to an internet address ({uri.Host}) is not allowed. Use https://.";
        switch (feature)
        {
            case NetFeature.ModelDownload:
                if (s.ModelDownloadPolicy == "Block") return "Model downloads are turned off (Settings → Security & privacy → Network).";
                if (s.ModelDownloadPolicy != "Allow" && !IsApproved(feature, uri.Host)) return "This model download was not approved.";
                return null;
            case NetFeature.LocalAi:
                return IsLoopback(uri.Host) ? null : "The local AI feature may only contact this PC.";
            case NetFeature.RemoteAi:
                return s.AllowRemoteAi ? null : "Sending transcripts to an AI server on another computer is turned off (Settings → Summary & AI).";
            default:
                return null;   // add-ons: enabling the add-on is the consent; its host is shown in the activity log
        }
    }

    private static bool IsApproved(string feature, string host)
    {
        lock (Lock) return ApprovedThisSession.Contains(feature + "|" + host);
    }

    /// <summary>
    /// Asks before a model download (policy "Ask"), saying what is fetched, from where, how big it is and what the server
    /// can see. Returns false if the user says no or downloads are blocked. "Always allow" switches the policy to Allow.
    /// </summary>
    public static async Task<bool> ConfirmModelDownloadAsync(string what, string host, string size)
    {
        var s = AppSettings.Current;
        if (s.ModelDownloadPolicy == "Block") return false;
        if (s.ModelDownloadPolicy == "Allow" || IsApproved(NetFeature.ModelDownload, host)) return true;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return false;
        var answer = await dispatcher.InvokeAsync(() => AskModelDownload(what, host, size));
        if (answer == ModelAnswer.No) return false;
        lock (Lock) ApprovedThisSession.Add(NetFeature.ModelDownload + "|" + host);
        if (answer == ModelAnswer.Always) { s.ModelDownloadPolicy = "Allow"; s.Save(); }
        return true;
    }

    /// <summary>Approves several hosts at once (e.g. the speaker models come from Hugging Face and GitHub).</summary>
    public static async Task<bool> ConfirmModelDownloadAsync(string what, IReadOnlyList<string> hosts, string size)
    {
        if (hosts.Count == 0) return true;
        if (!await ConfirmModelDownloadAsync(what, string.Join(" and ", hosts), size)) return false;
        lock (Lock) foreach (var h in hosts) ApprovedThisSession.Add(NetFeature.ModelDownload + "|" + h);
        return true;
    }

    private enum ModelAnswer { No, Once, Always }

    private static ModelAnswer AskModelDownload(string what, string host, string size)
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8),
            Text = $"WhisperDesk needs to download {what} ({size})." });
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = $"From: {host} (HTTPS). Every file is checked against a pinned SHA-256 fingerprint before it is used." });
        var detail = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 12),
            Text = "What the server sees: a request for this public model file, your IP address and the word “WhisperDesk” as the program name. " +
                   "Nothing about you, your recordings or transcripts is sent. After the download, transcription works offline." };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(detail);
        var always = new CheckBox { Content = "Don't ask again for model downloads", Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(always);
        if (owner == null) return ModelAnswer.No;
        var win = SpeakerDialogs.MakeWindow(owner, "Download a model?", panel, out var ok, "Download");
        win.Width = 520;
        ok.Click += (_, _) => win.DialogResult = true;
        if (win.ShowDialog() != true) return ModelAnswer.No;
        return always.IsChecked == true ? ModelAnswer.Always : ModelAnswer.Once;
    }

    // ---------------- activity log ----------------

    public static void Record(NetworkEvent e)
    {
        lock (Lock)
        {
            Log.Add(e);
            if (Log.Count > MaxLog) Log.RemoveRange(0, Log.Count - MaxLog);
        }
        SaveLog();
        try { Recorded?.Invoke(e); } catch { }
    }

    public static void ClearLog()
    {
        lock (Lock) Log.Clear();
        SaveLog();
    }

    private static List<NetworkEvent> LoadLog()
    {
        try
        {
            if (SecureFile.Exists(LogPath) && SecureFile.CanRead(LogPath))
                return JsonSerializer.Deserialize<List<NetworkEvent>>(SecureFile.ReadAllText(LogPath)) ?? new();
        }
        catch { }
        return new();
    }

    /// <summary>Reloads the log after the vault is unlocked (it is encrypted when the vault is on).</summary>
    public static void Reload()
    {
        var loaded = LoadLog();
        lock (Lock) { Log.Clear(); Log.AddRange(loaded); }
    }

    private static void SaveLog()
    {
        try
        {
            string json;
            lock (Lock) json = JsonSerializer.Serialize(Log);
            if (SecureFile.CanWrite) SecureFile.WriteAllText(LogPath, json);
        }
        catch { }
    }

    /// <summary>Checks policy before the request and records it afterwards.</summary>
    private sealed class GateHandler(string feature) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("No address.");
            if (Refusal(feature, uri) is { } why)
            {
                Record(new NetworkEvent(DateTimeOffset.Now, feature, request.Method.Method, uri.Host, feature == NetFeature.ModelDownload ? ShortPath(uri) : "/…", null, null, "blocked: " + why));
                throw new NetworkBlockedException(why);
            }
            HttpResponseMessage response;
            try { response = await base.SendAsync(request, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Record(new NetworkEvent(DateTimeOffset.Now, feature, request.Method.Method, uri.Host, feature == NetFeature.ModelDownload ? ShortPath(uri) : "/…", null, null, "failed"));
                throw;
            }
            // A redirect to another host (e.g. Hugging Face's CDN) is logged under the final host. (.NET never follows an
            // https → http redirect, so a download can't be downgraded to plain HTTP this way.)
            var final = response.RequestMessage?.RequestUri ?? uri;
            long? sent = request.Content?.Headers.ContentLength;
            Record(new NetworkEvent(DateTimeOffset.Now, feature, request.Method.Method, final.Host, feature == NetFeature.ModelDownload ? ShortPath(final) : "/…", (int)response.StatusCode,
                response.Content.Headers.ContentLength ?? sent, sent is > 0 ? $"sent {ModelManager.FormatSize(sent.Value)}" : null));
            return response;
        }

        /// <summary>The path without query; long CDN paths are shortened (they can carry signed tokens).</summary>
        private static string ShortPath(Uri u)
        {
            var p = u.AbsolutePath;
            return p.Length > 80 ? p[..40] + "…" + p[^30..] : p;
        }
    }
}
