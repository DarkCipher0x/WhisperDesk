using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using WhisperDesk.Addons;

namespace WhisperDesk.Services.Addons.BuiltIn;

/// <summary>
/// Local web server for live captions:
///   /overlay  – transparent caption page for an OBS Browser Source (style with URL parameters)
///   /events   – Server-Sent Events stream of transcript lines (JSON), for any app or script
///   /api/lines – the current session's lines as JSON
/// Only listens on localhost.
/// </summary>
public sealed class CaptionServerAddon : IWhisperDeskAddon
{
    private HttpListener? _listener;
    private IAddonHost? _host;
    private readonly ConcurrentDictionary<Guid, HttpListenerResponse> _clients = new();
    private readonly List<TranscriptLineEvent> _session = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Id => "whisperdesk.caption-server";
    public string Name => "Live captions for OBS & browsers";
    public string Description =>
        "Runs a small web server on this PC (localhost only) for OBS Browser Sources. Three looks: a darkcipher0x terminal " +
        "(http://localhost:7890/overlay?theme=terminal), a stacked chat with coloured names (?theme=chat) and classic subtitles (/overlay). " +
        "Open http://localhost:7890/ for previews, copyable URLs and options. Other apps can follow the live transcript at /events or /api/lines.";
    public string Author => "darkcipher0x";

    public IReadOnlyList<AddonOption> Options { get; } = new[]
    {
        new AddonOption("port", "Port", "7890", "Open http://localhost:<port>/ for links and instructions."),
        new AddonOption("fileLines", "Include file transcriptions", "false", "true = also send lines from the Files tab, not only Live."),
    };

    public void Start(IAddonHost host)
    {
        _host = host;
        var port = ParsePort(host.GetOption("port"));
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => AcceptLoop(_cts.Token));
        _ = Task.Run(() => KeepAlive(_cts.Token));
        host.LineAdded += OnLine;
        host.SessionStarted += OnSessionStarted;
        host.Log($"Listening on http://localhost:{port}/  (OBS overlay: http://localhost:{port}/overlay)");
    }

    /// <summary>A port number 1024–65535 (the default 7890 for anything else), so it can't smuggle text into a URL.</summary>
    public static int ParsePort(string? value) =>
        int.TryParse(value?.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is >= 1024 and <= 65535 ? p : 7890;

    public void Stop()
    {
        if (_host != null)
        {
            _host.LineAdded -= OnLine;
            _host.SessionStarted -= OnSessionStarted;
        }
        _cts?.Cancel();
        foreach (var c in _clients.Values) try { c.Abort(); } catch { }
        _clients.Clear();
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
    }

    private void OnSessionStarted(object? sender, SessionEvent e)
    {
        if (!e.IsLive) return;
        lock (_lock) _session.Clear();
        Broadcast("session", JsonSerializer.Serialize(e, Json));
    }

    private void OnLine(object? sender, TranscriptLineEvent e)
    {
        if (!e.IsLive && _host?.GetOption("fileLines") != "true") return;
        lock (_lock)
        {
            _session.Add(e);
            if (_session.Count > 2000) _session.RemoveAt(0);
        }
        Broadcast("line", JsonSerializer.Serialize(e, Json));
    }

    private void Broadcast(string evt, string data)
    {
        var bytes = Encoding.UTF8.GetBytes($"event: {evt}\ndata: {data}\n\n");
        foreach (var (id, res) in _clients)
        {
            try { res.OutputStream.Write(bytes); res.OutputStream.Flush(); }
            catch { _clients.TryRemove(id, out _); }
        }
    }

    private async Task KeepAlive(CancellationToken ct)
    {
        var ping = Encoding.UTF8.GetBytes(": ping\n\n");
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(15000, ct).ContinueWith(_ => { });
            foreach (var (id, res) in _clients)
            {
                try { res.OutputStream.Write(ping); res.OutputStream.Flush(); }
                catch { _clients.TryRemove(id, out _); }
            }
        }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private const int MaxClients = 32;

    /// <summary>
    /// Only this server's own pages (and programs that aren't browsers, which send no Origin) may read the transcript.
    /// A web page on another site open in the user's browser sends its own Origin and is refused, so it can't read live
    /// captions through localhost. The Host check stops DNS-rebinding (a hostile name pointed at 127.0.0.1).
    /// </summary>
    internal static bool IsAllowedRequest(string? host, string? origin, int port)
    {
        bool LocalName(string h) => h.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase) || h.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase);
        if (host == null || !LocalName(host)) return false;
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Scheme == "http" && LocalName($"{o.Host}:{o.Port}");
    }

    private void Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        // No CORS header: browsers block other sites from reading responses. Pages served here are same-origin.
        res.Headers["X-Content-Type-Options"] = "nosniff";
        res.Headers["Referrer-Policy"] = "no-referrer";
        res.Headers["Cache-Control"] = "no-store";
        try
        {
            if (!IsAllowedRequest(ctx.Request.Headers["Host"], ctx.Request.Headers["Origin"], ctx.Request.Url?.Port ?? 0) || ctx.Request.HttpMethod != "GET")
            {
                res.StatusCode = 403;
                Send(res, "text/plain", "Forbidden");
                return;
            }
            var route = ctx.Request.Url?.AbsolutePath.TrimEnd('/').ToLowerInvariant();
            // Locked: the transcript isn't served to anyone (pages still load; they show nothing until unlocked).
            if (Security.AppLock.IsLocked && route is "/events" or "/api/lines")
            {
                res.StatusCode = 423;
                Send(res, "text/plain", "WhisperDesk is locked");
                return;
            }
            switch (route)
            {
                case "/events":
                    if (_clients.Count >= MaxClients) { res.StatusCode = 503; Send(res, "text/plain", "Too many listeners"); return; }
                    res.ContentType = "text/event-stream";
                    res.Headers["Cache-Control"] = "no-cache";
                    res.SendChunked = true;
                    res.OutputStream.Write(Encoding.UTF8.GetBytes("retry: 2000\n\n"));
                    res.OutputStream.Flush();
                    _clients[Guid.NewGuid()] = res;   // kept open; written to by Broadcast
                    return;
                case "/api/lines":
                    string json;
                    lock (_lock) json = JsonSerializer.Serialize(_session, Json);
                    Send(res, "application/json", json);
                    return;
                case "/overlay":
                    var theme = ctx.Request.QueryString["theme"]?.ToLowerInvariant();
                    Send(res, "text/html; charset=utf-8", theme switch { "terminal" => TerminalHtml, "chat" => ChatHtml, _ => OverlayHtml });
                    return;
                case "/terminal":
                    Send(res, "text/html; charset=utf-8", TerminalHtml);
                    return;
                case "/chat":
                    Send(res, "text/html; charset=utf-8", ChatHtml);
                    return;
                case "":
                    Send(res, "text/html; charset=utf-8", IndexHtml(ctx.Request.Url!.Port));
                    return;
                default:
                    res.StatusCode = 404;
                    Send(res, "text/plain", "Not found");
                    return;
            }
        }
        catch (Exception ex)
        {
            _host?.Log("Request failed: " + ex.Message);
            try { res.Abort(); } catch { }
        }
    }

    private static void Send(HttpListenerResponse res, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.ContentType = type;
        res.ContentLength64 = bytes.Length;
        res.OutputStream.Write(bytes);
        res.Close();
    }

    private static string IndexHtml(int port) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>WhisperDesk live captions</title>
        <style>body{font-family:Segoe UI,sans-serif;background:#050909;color:#d2f2ef;max-width:820px;margin:40px auto;padding:0 16px;line-height:1.55}
        h1{font-family:'Cascadia Mono',Consolas,monospace;color:#36d2cb} h2{font-size:17px;margin-top:28px;border-bottom:1px solid #16302f;padding-bottom:4px}
        code{background:#0b1314;border:1px solid #16302f;padding:2px 6px;border-radius:3px;font-family:'Cascadia Mono',Consolas,monospace;font-size:13px}
        a{color:#36d2cb} .muted{color:#5a8986}</style></head><body>
        <h1>WhisperDesk live captions</h1>
        <p class="muted">Served only to this PC (localhost). In OBS: <i>Sources → + → Browser</i>, paste a URL below, and tick nothing else.</p>

        <h2>Terminal (darkcipher0x style)</h2>
        <p><code>http://localhost:{{port}}/overlay?theme=terminal</code> · size 900×420 · <a href="/overlay?theme=terminal&amp;demo=1">preview</a></p>
        <p class="muted">Options: <code>lines=8 size=22 accent=36d2cb bg=050909d9 user=darkcipher0x host=whisperdesk title=live&nbsp;transcript times=1 type=1 speaker=1</code></p>

        <h2>Chat (stacked, coloured names)</h2>
        <p><code>http://localhost:{{port}}/overlay?theme=chat</code> · size 420×800 · <a href="/overlay?theme=chat&amp;demo=1">preview</a></p>
        <p class="muted">Options: <code>lines=14 size=20 bg=00000000 fade=60 times=0</code> (fade = seconds before old lines fade, 0 = never)</p>

        <h2>Subtitles (bottom captions)</h2>
        <p><code>http://localhost:{{port}}/overlay</code> · size 1920×300 · <a href="/overlay?bg=000000cc&amp;demo=1">preview</a></p>
        <p class="muted">Options: <code>lines=2 size=42 speaker=1 color=ffffff bg=00000099 font=Segoe%20UI align=center</code></p>

        <h2>For developers</h2>
        <ul><li><a href="/api/lines">/api/lines</a> – current session as JSON</li>
        <li><code>/events</code> – Server-Sent Events stream (<code>event: line</code>, JSON data)</li></ul>
        <p class="muted">WhisperDesk by darkcipher0x</p></body></html>
        """;

    // Shared by the themes: speaker → stable colour, demo lines for previews, SSE hookup.
    private const string CommonJs = """
          const q=new URLSearchParams(location.search);
          const PALETTE=['#36D2CB','#E8A33D','#E0607E','#4E9BE8','#9B7CF0','#7DD957','#F07CC4','#D9734A'];
          const colourOf=n=>{if(!n)return '#5A8986';let h=0;for(const c of n)h=(h*31+c.charCodeAt(0))>>>0;return PALETTE[h%PALETTE.length];};
          const ts=l=>{if(typeof l.start==='string')return l.start.slice(0,8);const s=Math.floor(l.start||0);return [s/3600|0,(s/60|0)%60,s%60].map(x=>String(x).padStart(2,'0')).join(':');};
          function connect(add,reset){
            if(q.get('demo')){const d=[['Alex','Testing, testing — is the transcription coming through?'],['Sam','Yes, loud and clear on my side.'],
              ['Alex','Nice. Everything runs locally, nothing leaves the PC.'],[null,'(background noise filtered)'],['Sam','That is genuinely impressive.']];
              let i=0;setInterval(()=>{const [s,t]=d[i++%d.length];add({speaker:s,text:t,start:i*4});},1800);return;}
            const es=new EventSource('/events');
            es.addEventListener('line',e=>add(JSON.parse(e.data)));
            es.addEventListener('session',()=>reset());
          }
        """;

    private const string TerminalHtml = """
        <!doctype html><html><head><meta charset="utf-8"><title>Terminal captions</title>
        <style>
          html,body{margin:0;height:100%;background:transparent;overflow:hidden}
          :root{--accent:#36d2cb;--bg:#050909d9;--size:22px}
          #term{position:absolute;inset:0;display:flex;flex-direction:column;background:var(--bg);border:1px solid color-mix(in srgb,var(--accent) 55%,transparent);
                border-radius:8px;font:500 var(--size)/1.45 'Cascadia Mono','Cascadia Code',Consolas,monospace;color:#d2f2ef;box-sizing:border-box}
          #bar{display:flex;align-items:center;gap:8px;padding:6px 12px;border-bottom:1px solid color-mix(in srgb,var(--accent) 30%,transparent);
               font-size:calc(var(--size)*.62);color:#5a8986}
          .dot{width:.75em;height:.75em;border-radius:50%;background:#16302f}.dot.on{background:var(--accent);box-shadow:0 0 6px var(--accent)}
          #bar .t{margin-left:auto;letter-spacing:.08em;text-transform:uppercase}
          #lines{flex:1;padding:10px 14px;display:flex;flex-direction:column;justify-content:flex-end;overflow:hidden}
          .l{white-space:pre-wrap;word-break:break-word;animation:in .18s ease-out}
          .l .tm{color:#5a8986}.l .who{font-weight:700}.l .gt{color:#5a8986}
          #prompt{padding:0 14px 10px;color:var(--accent)}
          .cur{display:inline-block;width:.6em;height:1.05em;background:var(--accent);vertical-align:text-bottom;animation:blink 1.1s steps(1) infinite}
          @keyframes blink{50%{opacity:0}} @keyframes in{from{opacity:0}to{opacity:1}}
        </style></head><body><div id="term">
          <div id="bar"><span class="dot on"></span><span class="dot"></span><span class="dot"></span><span id="title" class="t"></span></div>
          <div id="lines"></div><div id="prompt"><span id="ps"></span> <span class="cur"></span></div></div>
        <script>
        """ + CommonJs + """
          const r=document.documentElement.style;
          r.setProperty('--accent','#'+(q.get('accent')||'36d2cb')); r.setProperty('--bg','#'+(q.get('bg')||'050909d9'));
          r.setProperty('--size',(q.get('size')||'22')+'px');
          const max=+(q.get('lines')||8), times=q.get('times')!=='0', type=q.get('type')!=='0', who=q.get('speaker')!=='0';
          document.getElementById('ps').textContent=`${q.get('user')||'darkcipher0x'}@${q.get('host')||'whisperdesk'}:~$`;
          document.getElementById('title').textContent=q.get('title')||'live transcript';
          const box=document.getElementById('lines'); let queue=Promise.resolve();
          function add(l){queue=queue.then(()=>new Promise(done=>{
            const d=document.createElement('div');d.className='l';
            if(times){const t=document.createElement('span');t.className='tm';t.textContent='['+ts(l)+'] ';d.appendChild(t);}
            if(who&&l.speaker){const w=document.createElement('span');w.className='who';w.style.color=colourOf(l.speaker);w.textContent=l.speaker;d.appendChild(w);
              const g=document.createElement('span');g.className='gt';g.textContent='> ';d.appendChild(g);}
            const body=document.createTextNode('');d.appendChild(body);box.appendChild(d);
            while(box.children.length>max)box.removeChild(box.firstChild);
            if(!type){body.textContent=l.text;return done();}
            let i=0;const step=Math.max(1,Math.ceil(l.text.length/40));
            const iv=setInterval(()=>{i+=step;body.textContent=l.text.slice(0,i);if(i>=l.text.length){clearInterval(iv);done();}},18);
          }));}
          connect(add,()=>{box.innerHTML='';});
        </script></body></html>
        """;

    private const string ChatHtml = """
        <!doctype html><html><head><meta charset="utf-8"><title>Chat captions</title>
        <style>
          html,body{margin:0;height:100%;background:transparent;overflow:hidden}
          :root{--size:20px;--bg:#00000000}
          #chat{position:absolute;inset:0;padding:10px 12px;display:flex;flex-direction:column;justify-content:flex-end;gap:6px;background:var(--bg);
                font:500 var(--size)/1.35 'Segoe UI',system-ui,sans-serif;color:#f4fbfa;text-shadow:0 1px 2px #000,0 0 3px #000}
          .m{animation:in .2s ease-out;transition:opacity 1.2s}.m.old{opacity:0}
          .who{font-weight:700;margin-right:.35em}.tm{opacity:.6;font-size:.75em;margin-right:.4em;font-family:Consolas,monospace}
          @keyframes in{from{opacity:0;transform:translateX(-6px)}to{opacity:1;transform:none}}
        </style></head><body><div id="chat"></div>
        <script>
        """ + CommonJs + """
          const r=document.documentElement.style;
          r.setProperty('--size',(q.get('size')||'20')+'px'); r.setProperty('--bg','#'+(q.get('bg')||'00000000'));
          const max=+(q.get('lines')||14), fade=+(q.get('fade')||60), times=q.get('times')==='1';
          const box=document.getElementById('chat');
          function add(l){
            const d=document.createElement('div');d.className='m';
            if(times){const t=document.createElement('span');t.className='tm';t.textContent=ts(l);d.appendChild(t);}
            if(l.speaker){const w=document.createElement('span');w.className='who';w.style.color=colourOf(l.speaker);w.textContent=l.speaker+':';d.appendChild(w);}
            d.appendChild(document.createTextNode(l.text));box.appendChild(d);
            while(box.children.length>max)box.removeChild(box.firstChild);
            if(fade>0)setTimeout(()=>{d.classList.add('old');setTimeout(()=>d.remove(),1300);},fade*1000);
          }
          connect(add,()=>{box.innerHTML='';});
        </script></body></html>
        """;

    private const string OverlayHtml = """
        <!doctype html><html><head><meta charset="utf-8"><title>Captions</title>
        <style>
          html,body{margin:0;height:100%;background:transparent;overflow:hidden}
          #wrap{position:absolute;left:0;right:0;bottom:0;padding:16px 4%;display:flex;flex-direction:column;gap:6px}
          .line{display:inline-block;align-self:var(--align);padding:6px 14px;border-radius:8px;background:var(--bg);
                color:var(--color);font:600 var(--size)/1.25 var(--font);text-shadow:0 2px 4px #000,0 0 2px #000;
                animation:in .25s ease-out}
          .who{opacity:.75;font-weight:700;margin-right:.4em}
          @keyframes in{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:none}}
        </style></head><body><div id="wrap"></div>
        <script>
          const q=new URLSearchParams(location.search), root=document.documentElement.style;
          const max=+(q.get('lines')||2), showWho=(q.get('speaker')||'1')!=='0';
          root.setProperty('--size',(q.get('size')||'42')+'px');
          root.setProperty('--color','#'+(q.get('color')||'ffffff'));
          root.setProperty('--bg','#'+(q.get('bg')||'00000099'));
          root.setProperty('--font',q.get('font')||'Segoe UI, sans-serif');
          root.setProperty('--align',{left:'flex-start',right:'flex-end'}[q.get('align')]||'center');
          const wrap=document.getElementById('wrap');
          function add(l){
            const d=document.createElement('div'); d.className='line';
            if(showWho&&l.speaker){const w=document.createElement('span');w.className='who';w.textContent=l.speaker+':';d.appendChild(w);}
            d.appendChild(document.createTextNode(l.text)); wrap.appendChild(d);
            while(wrap.children.length>max) wrap.removeChild(wrap.firstChild);
          }
          if(q.get('demo')){const d=['Testing, testing — is this coming through?','Yes, loud and clear.','Everything runs locally on this PC.'];
            let i=0;setInterval(()=>add({speaker:i%2?'Sam':'Alex',text:d[i++%d.length]}),1800);}
          else{const es=new EventSource('/events');
          es.addEventListener('line',e=>add(JSON.parse(e.data)));
          es.addEventListener('session',()=>{wrap.innerHTML='';});}
        </script></body></html>
        """;
}
