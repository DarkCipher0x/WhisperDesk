using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace WhisperDesk.Services;

/// <summary>
/// Shareable sessions:
///   .wdsession — zip with metadata.json, transcript.json (incl. edit log) and audio.m4a; opens in WhisperDesk's player.
///   .html      — a self-contained web player (audio embedded) that opens in any browser.
/// Voiceprints are never included in shared files (they are biometric data).
/// </summary>
public static class SessionPackage
{
    public const string Extension = ".wdsession";
    private const string Format = "whisperdesk-session";

    /// <summary>Compresses the recording's audio to AAC (mono, 64 kb/s ≈ 0.5 MB per minute). Returns null if there is no audio.</summary>
    private static async Task<string?> CompressAudioAsync(RecordingMeta meta, string workDir, CancellationToken ct)
    {
        if (!meta.HasMedia) return null;
        var ffmpeg = AudioDecoder.FindFfmpeg() ?? throw new InvalidOperationException("FFmpeg not found (needed to compress the audio).");
        var output = Path.Combine(workDir, "audio.m4a");
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", meta.MediaPath,
                                  "-vn", "-ac", "1", "-ar", "24000", "-c:a", "aac", "-b:a", "64k", "-movflags", "+faststart", output })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0) throw new InvalidOperationException("Could not compress the audio: " + (await err).Trim());
        return output;
    }

    /// <summary>Copy of the metadata safe to share: no voiceprints, no local file paths.</summary>
    private static RecordingMeta ShareableMeta(RecordingMeta meta, bool hasAudio)
    {
        var copy = JsonSerializer.Deserialize<RecordingMeta>(JsonSerializer.Serialize(meta))!;
        foreach (var s in copy.Speakers) s.Voiceprint = null;
        copy.ExternalMedia = null;
        copy.MediaFile = hasAudio ? "audio.m4a" : null;
        if (copy.Source.StartsWith("File · ")) copy.Source = "File · " + Path.GetFileName(copy.Source[7..]);
        return copy;
    }

    // ---------------- .wdsession ----------------

    public static async Task ExportAsync(RecordingMeta meta, string path, CancellationToken ct = default)
    {
        var work = Security.WorkDir.NewFolder("export");
        try
        {
            var audio = await CompressAudioAsync(meta, work, ct);
            var shared = ShareableMeta(meta, audio != null);
            if (File.Exists(path)) File.Delete(path);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            void AddText(string name, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
            AddText("manifest.json", JsonSerializer.Serialize(new
            {
                format = Format, version = 1, app = "WhisperDesk by darkcipher0x",
                appVersion = AboutWindow.Version,
                exported = DateTimeOffset.Now,
            }));
            AddText("metadata.json", JsonSerializer.Serialize(shared, new JsonSerializerOptions { WriteIndented = true }));
            AddText("transcript.json", RecordingLibrary.ReadTranscriptJson(meta));   // decrypted: the export is a deliberate, readable copy
            if (audio != null) zip.CreateEntryFromFile(audio, "audio.m4a", CompressionLevel.NoCompression);
        }
        finally { Security.WorkDir.Release(work); }
    }

    /// <summary>Unpacks a .wdsession into a temporary folder and loads it (read-only until imported).</summary>
    /// <summary>The only files a .wdsession may contain, with the most each may unpack to (zip-bomb protection).</summary>
    private static readonly Dictionary<string, long> Allowed = new(StringComparer.Ordinal)
    {
        ["manifest.json"] = 64 << 10, ["metadata.json"] = 16 << 20, ["transcript.json"] = 256 << 20, ["audio.m4a"] = 4L << 30,
    };

    /// <summary>
    /// Unpacks a .wdsession (an untrusted file someone sent) into a private working folder and loads it read-only until
    /// imported. Only the four known file names are extracted, each with a size limit counted while unpacking.
    /// </summary>
    public static (RecordingMeta Meta, TranscriptResult Result) Open(string path)
    {
        var dir = Security.WorkDir.NewFolder("session");
        try
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                if (zip.Entries.Count > 32) throw new InvalidDataException("Not a WhisperDesk session file (too many entries).");
                var manifest = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("Not a WhisperDesk session file.");
                if (manifest.Length > Allowed["manifest.json"]) throw new InvalidDataException("Not a WhisperDesk session file.");
                using (var r = new StreamReader(manifest.Open()))
                    if (!r.ReadToEnd().Contains(Format)) throw new InvalidDataException("Not a WhisperDesk session file.");
                foreach (var e in zip.Entries)
                {
                    // Plain known names only: never write outside the folder, never unexpected files.
                    if (!Allowed.TryGetValue(e.FullName, out var limit)) continue;
                    if (e.Length > limit) throw new InvalidDataException($"{e.FullName} in this session file is too large.");
                    using var src = e.Open();
                    using var dst = new FileStream(Path.Combine(dir, e.FullName), FileMode.CreateNew, FileAccess.Write);
                    var buf = new byte[1 << 20];
                    long total = 0;
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        total += n;
                        if (total > limit) throw new InvalidDataException($"{e.FullName} in this session file unpacks to more than its size limit.");
                        dst.Write(buf, 0, n);
                    }
                }
            }
            var meta = RecordingLibrary.ReadMeta(dir);
            // Paths inside a shared file are never trusted: media must be the unpacked audio.m4a in this folder.
            meta.ExternalMedia = null;
            if (meta.MediaFile != null && meta.MediaFile != "audio.m4a") meta.MediaFile = null;
            var result = RecordingLibrary.Load(meta);
            result.Library = null;          // not in the library until the user imports it
            return (meta, result);
        }
        catch
        {
            Security.WorkDir.Release(dir);
            throw;
        }
    }

    /// <summary>Copies an opened session into the recordings library.</summary>
    public static RecordingMeta Import(RecordingMeta opened, TranscriptResult result, string folder)
    {
        var meta = JsonSerializer.Deserialize<RecordingMeta>(JsonSerializer.Serialize(opened))!;
        meta.Id = Guid.NewGuid().ToString("N");
        var audio = opened.MediaFile != null ? Path.Combine(opened.Dir, opened.MediaFile) : null;
        meta.MediaFile = null;
        return RecordingLibrary.Save(result, meta, folder, mediaToMove: null, mediaToCopy: audio, mediaToLink: null);
    }

    // ---------------- self-contained web player ----------------

    public static async Task ExportHtmlAsync(RecordingMeta meta, TranscriptResult result, string path, CancellationToken ct = default)
    {
        var work = Security.WorkDir.NewFolder("export");
        try
        {
            var audio = await CompressAudioAsync(meta, work, ct);
            var audioUri = audio != null ? "data:audio/mp4;base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(audio, ct)) : "";
            var data = new
            {
                title = meta.Title,
                type = meta.Type,
                created = meta.Created.ToString("yyyy-MM-dd HH:mm"),
                duration = meta.Duration,
                source = ShareableMeta(meta, audio != null).Source,
                tags = meta.Tags,
                notes = meta.Notes,
                speakers = result.Speakers.Select(s => new
                {
                    id = s.Id, name = s.Label, sex = s.Sex, talk = s.TalkTime.ToString(@"mm\:ss"),
                    color = s.Color is System.Windows.Media.SolidColorBrush b ? $"#{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : "#36D2CB",
                }),
                lines = result.Lines.Where(l => l.Text.Trim().Length > 0).Select(l => new
                {
                    start = Math.Round(l.Start.TotalSeconds, 2),
                    end = Math.Round(l.End.TotalSeconds, 2),
                    time = l.Time,
                    speaker = l.Speaker?.Id,
                    text = l.Text,
                    edited = l.IsEdited,
                    edits = l.Edits.Select(e => new { at = e.AtText, user = e.User, reason = e.Reason, before = e.OldText, after = e.NewText }),
                }),
            };
            var summary = await CallSummary.BuildAsync(result, meta.Title, meta.Created, null, ct);
            // Safe inside <script>: no "<" at all (stops </script>, <!-- and similar parser tricks in titles or text).
            var json = ScriptSafe(JsonSerializer.Serialize(data));
            var summaryJson = ScriptSafe(summary.ToJson());
            var html = PlayerHtml.Replace("__SUMMARY__", summaryJson).Replace("__DATA__", json).Replace("__AUDIO__", audioUri)
                                 .Replace("__TITLE__", System.Net.WebUtility.HtmlEncode(meta.Title));
            await File.WriteAllTextAsync(path, html, new UTF8Encoding(false), ct);
        }
        finally { Security.WorkDir.Release(work); }
    }

    /// <summary>JSON made safe to embed in an HTML script element: &lt; &gt; &amp; and the JS line separators as \u escapes.</summary>
    internal static string ScriptSafe(string json) =>
        json.Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026").Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");

    private const string PlayerHtml = """
<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>__TITLE__ · WhisperDesk</title>
<style>
:root{--bg:#050909;--surface:#0B1314;--line:#16302F;--fg:#D2F2EF;--muted:#5A8986;--accent:#36D2CB;--hl:#0F2A2A}
@media (prefers-color-scheme:light){:root{--bg:#F1F6F6;--surface:#E3EDED;--line:#BCD2D1;--fg:#08191A;--muted:#4E6E6C;--accent:#147A76;--hl:#D2ECEA}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 "Segoe UI",system-ui,sans-serif}
header{background:#000;border-bottom:1px solid var(--accent);padding:14px 20px;position:sticky;top:0;z-index:2}
.brand{font:700 13px Consolas,"Cascadia Mono",monospace;color:#36D2CB}
h1{margin:4px 0 2px;font:700 22px Consolas,"Cascadia Mono",monospace;color:#E2F4F2}
.meta{color:#7FA9A6;font-size:13px}
.player{display:flex;gap:12px;align-items:center;margin-top:10px}
.player button{background:#36D2CB;color:#000;border:0;border-radius:2px;font:700 14px Consolas,monospace;padding:8px 16px;cursor:pointer}
.player button.sm{background:transparent;color:#36D2CB;border:1px solid #36D2CB;padding:7px 11px}
.player input{flex:1;accent-color:#36D2CB}
.player .t{font:13px Consolas,monospace;color:#D2F2EF;min-width:120px;text-align:right}
main{max-width:1100px;margin:0 auto;padding:16px 20px 60px}
.speakers{display:flex;flex-wrap:wrap;gap:8px;margin:6px 0 16px}
.chip{border:1px solid var(--line);background:var(--surface);border-radius:2px;padding:4px 10px;font-size:13px}
.dot{display:inline-block;width:9px;height:9px;border-radius:50%;margin-right:6px}
.line{display:grid;grid-template-columns:76px 1fr;gap:10px;padding:7px 10px;border-radius:2px;cursor:pointer;border-left:3px solid transparent}
.line:hover{background:var(--surface)}.line.now{background:var(--hl);border-left-color:var(--accent)}
.time{font:12.5px Consolas,monospace;color:var(--muted);padding-top:2px}
.who{font-weight:700;margin-right:6px}
.tag{font:11px Consolas,monospace;color:#E8A33D;border:1px solid #E8A33D;border-radius:2px;padding:0 5px;margin-left:6px;cursor:help}
.notes{background:var(--surface);border:1px solid var(--line);padding:8px 12px;border-radius:2px;margin-bottom:14px;white-space:pre-wrap}
h2{font:700 15px Consolas,monospace;color:var(--accent);margin:28px 0 8px}
.edit{border-bottom:1px solid var(--line);padding:6px 0;font-size:13.5px}.edit b{font-family:Consolas,monospace}
footer{color:var(--muted);font-size:12px;text-align:center;margin-top:30px}
details{background:var(--surface);border:1px solid var(--line);border-radius:2px;padding:8px 14px;margin:4px 0 8px}summary{font:700 15px Consolas,monospace;color:var(--accent);cursor:pointer}h3{font:700 13px Consolas,monospace;margin:12px 0 4px}.si{padding:3px 0;cursor:pointer;font-size:14px}.si:hover{color:var(--accent)}.muted{color:var(--muted);font-size:13px}
.noaudio{color:#E8A33D;font-size:13px}
</style></head><body>
<header><div class="brand">darkcipher0x@whisperdesk:~$ play</div><h1 id="title"></h1><div class="meta" id="meta"></div>
<div class="player" id="player"><button id="prev" class="sm" title="Previous line">⏮</button><button id="pp">▶ Play</button><button id="next" class="sm" title="Next line">⏭</button><button id="stop" class="sm" title="Stop">■</button><input id="seek" type="range" min="0" max="1000" value="0"><span class="t" id="t">00:00 / 00:00</span></div>
<audio id="audio" preload="auto" src="__AUDIO__"></audio></header>
<main><div class="speakers" id="speakers"></div><div id="notes"></div><details id="sum" open><summary>Summary</summary><div id="sumbody"></div></details><h2>Transcript</h2><div id="lines"></div><div id="edits"></div>
<footer>Transcribed locally with WhisperDesk by darkcipher0x · click any line to play from there</footer></main>
<script>
const D=__DATA__,S=__SUMMARY__,A=document.getElementById('audio'),$=id=>document.getElementById(id);
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const fmt=s=>{s=Math.max(0,Math.floor(s||0));const h=Math.floor(s/3600),m=Math.floor(s%3600/60),x=s%60;return (h?h+':':'')+String(m).padStart(2,'0')+':'+String(x).padStart(2,'0')};
const sp=Object.fromEntries(D.speakers.map(s=>[s.id,s]));
$('title').textContent=D.title;
$('meta').textContent=[D.type,D.created,D.duration,D.source,(D.tags||[]).join(', ')].filter(Boolean).join('  ·  ');
$('speakers').innerHTML=D.speakers.map(s=>`<span class="chip"><span class="dot" style="background:${s.color}"></span>${esc(s.name)}${s.sex&&s.sex!=='Unspecified'?' ('+esc(s.sex)+')':''} · ${s.talk}</span>`).join('');
if(D.notes)$('notes').innerHTML=`<div class="notes">${esc(D.notes)}</div>`;
$('lines').innerHTML=D.lines.map((l,i)=>{const s=sp[l.speaker];const tip=l.edited?l.edits.map(e=>`${e.at} by ${e.user}\nReason: ${e.reason}\nWas: ${e.before}`).join('\n\n'):'';
 return `<div class="line" data-i="${i}"><span class="time">${l.time}</span><span>${s?`<span class="who" style="color:${s.color}">${esc(s.name)}:</span>`:''}${esc(l.text)}${l.edited?`<span class="tag" title="${esc(tip)}">✎ edited</span>`:''}</span></div>`}).join('');
const ed=D.lines.flatMap(l=>l.edits.map(e=>({...e,time:l.time,who:sp[l.speaker]?.name})));
if(ed.length)$('edits').innerHTML='<h2>Edit log</h2>'+ed.map(e=>`<div class="edit"><b>${esc(e.at)} · ${esc(e.user)} · line [${e.time}]${e.who?' · '+esc(e.who):''}</b><br>Reason: ${esc(e.reason)}<br><span style="color:var(--muted)">Before: ${esc(e.before)}</span><br>After: ${esc(e.after)}</div>`).join('');
const sec=(h,items,empty)=>`<h3>${h}</h3>`+(items.length?items.map(i=>`<div class="si" data-t="${i.time}"><span class="time">${i.time}</span> ${i.speaker?`<b>${esc(i.speaker)}:</b> `:''}${esc(i.text)}</div>`).join(''):`<div class="muted">${empty}</div>`);
const rep=(S.report||'').split('\n').map(l=>l.trim().replace(/\*\*/g,'')).filter(Boolean).map(l=>l.startsWith('#')?`<h3>${esc(l.replace(/^#+\s*/,''))}</h3>`:`<p style="margin:4px 0 8px">${esc(l)}</p>`).join('');
$('sumbody').innerHTML=`<div class="muted">${esc(S.duration)} · ${S.speakers.length} speakers · ${S.words} words${S.edits?` · ${S.edits} edits`:''}</div>`+rep+
 sec('Key points',S.keyPoints,'—')+sec('Action items',S.actionItems,'No action items detected.')+sec('Decisions',S.decisions,'No decisions detected.')+sec('Open questions',S.questions,'No questions detected.');
const toSec=t=>t.split(':').reduce((a,b)=>a*60+(+b),0);
document.querySelectorAll('.si').forEach(e=>e.onclick=()=>{if(!A.getAttribute('src'))return;A.currentTime=toSec(e.dataset.t);A.play()});
const rows=[...document.querySelectorAll('.line')];
if(!A.getAttribute('src')){$('player').innerHTML='<span class="noaudio">This session was shared without audio.</span>'}
rows.forEach(r=>r.onclick=()=>{const l=D.lines[r.dataset.i];if(!A.src)return;A.currentTime=Math.max(0,l.start-0.1);A.play()});
$('pp')&&($('pp').onclick=()=>A.paused?A.play():A.pause());
const lineAt=t=>{let i=-1;for(let k=0;k<D.lines.length;k++){if(D.lines[k].start<=t+0.15)i=k;else break}return i};
const go=i=>{if(!D.lines.length)return;i=Math.max(0,Math.min(D.lines.length-1,i));A.currentTime=Math.max(0,D.lines[i].start-0.1)};
$('prev')&&($('prev').onclick=()=>{const i=lineAt(A.currentTime);go(i<0?0:(A.currentTime-D.lines[i].start<1.5?i-1:i))});
$('next')&&($('next').onclick=()=>{const i=lineAt(A.currentTime);if(i<D.lines.length-1)go(i+1)});
$('stop')&&($('stop').onclick=()=>{A.pause();const i=lineAt(A.currentTime);if(i>=0)go(i);else A.currentTime=0});
document.addEventListener('keydown',e=>{if(!A.getAttribute('src'))return;if(e.code==='Space'){e.preventDefault();A.paused?A.play():A.pause()}else if(e.key==='Escape')$('stop').click();else if(e.ctrlKey&&e.key==='ArrowLeft')$('prev').click();else if(e.ctrlKey&&e.key==='ArrowRight')$('next').click()});
A.onplay=()=>$('pp').textContent='❚❚ Pause';A.onpause=()=>$('pp').textContent='▶ Play';
$('seek')&&($('seek').oninput=e=>{if(A.duration)A.currentTime=e.target.value/1000*A.duration});
let cur=-1;A.ontimeupdate=()=>{const t=A.currentTime;$('t').textContent=fmt(t)+' / '+fmt(A.duration);if(A.duration)$('seek').value=t/A.duration*1000;
 let i=-1;for(let k=0;k<D.lines.length;k++){if(D.lines[k].start<=t+0.05)i=k;else break}
 if(i!==cur){if(cur>=0)rows[cur].classList.remove('now');cur=i;if(i>=0){rows[i].classList.add('now');rows[i].scrollIntoView({block:'center',behavior:'smooth'})}}};
</script></body></html>
""";
}
