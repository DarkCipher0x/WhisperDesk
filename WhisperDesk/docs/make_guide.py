"""Builds the WhisperDesk User & Administrator Guide (PDF).

    python docs/make_guide.py  [output.pdf]

Requires: reportlab (pip install reportlab). Images are read from docs/img.
"""
import os
import sys
from datetime import date

from reportlab.lib import colors
from reportlab.lib.enums import TA_CENTER, TA_LEFT
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.units import mm
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import (BaseDocTemplate, Frame, Image, KeepTogether, NextPageTemplate, PageBreak,
                                PageTemplate, Paragraph, Spacer, Table, TableStyle)
from reportlab.platypus.tableofcontents import TableOfContents

HERE = os.path.dirname(os.path.abspath(__file__))


def _project_version():
    """The version comes from <Version> in WhisperDesk.csproj, the single source of truth."""
    import re
    with open(os.path.join(HERE, "..", "WhisperDesk.csproj"), encoding="utf-8") as f:
        m = re.search(r"<Version>\s*([^<\s]+)\s*</Version>", f.read())
    if not m:
        raise SystemExit("No <Version> in WhisperDesk.csproj")
    return m.group(1)


VERSION = _project_version()
IMG = os.path.join(HERE, "img")
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(HERE, "WhisperDesk-User-Admin-Guide.pdf")

# ---------- Fonts & brand ----------
FONTS = r"C:\Windows\Fonts"
pdfmetrics.registerFont(TTFont("Sans", os.path.join(FONTS, "segoeui.ttf")))
pdfmetrics.registerFont(TTFont("Sans-Bold", os.path.join(FONTS, "segoeuib.ttf")))
pdfmetrics.registerFont(TTFont("Sans-Italic", os.path.join(FONTS, "segoeuii.ttf")))
pdfmetrics.registerFont(TTFont("Mono", os.path.join(FONTS, "consola.ttf")))
pdfmetrics.registerFont(TTFont("Mono-Bold", os.path.join(FONTS, "consolab.ttf")))
pdfmetrics.registerFont(TTFont("Sym", os.path.join(FONTS, "seguisym.ttf")))
from reportlab.pdfbase.pdfmetrics import registerFontFamily
registerFontFamily("Sans", normal="Sans", bold="Sans-Bold", italic="Sans-Italic", boldItalic="Sans-Bold")
registerFontFamily("Mono", normal="Mono", bold="Mono-Bold", italic="Mono", boldItalic="Mono-Bold")

TEAL = colors.HexColor("#36D2CB")
TEAL_DEEP = colors.HexColor("#147A76")
INK = colors.HexColor("#0C1A1B")
MUTED = colors.HexColor("#5A706F")
LINE = colors.HexColor("#CFE0DF")
PANEL = colors.HexColor("#EEF6F5")
BLACK = colors.HexColor("#050909")

PLAY = '<font name="Sym">\u25B6</font>'
CHECK = '<font name="Sym">\u2713</font>'
CROSS = '<font name="Sym">\u2715</font>'
ARROW = '<font name="Sym">\u2192</font>'
PENCIL = '<font name="Sym">\u270e</font>'

# ---------- Styles ----------
body = ParagraphStyle("body", fontName="Sans", fontSize=10, leading=14.5, textColor=INK, spaceAfter=6)
small = ParagraphStyle("small", parent=body, fontSize=8.5, leading=12, textColor=MUTED)
bullet = ParagraphStyle("bullet", parent=body, leftIndent=14, bulletIndent=3, spaceAfter=3)
h1 = ParagraphStyle("h1", fontName="Mono-Bold", fontSize=19, leading=24, textColor=TEAL_DEEP, spaceBefore=4, spaceAfter=10)
h2 = ParagraphStyle("h2", fontName="Mono-Bold", fontSize=13, leading=17, textColor=INK, spaceBefore=12, spaceAfter=6)
h3 = ParagraphStyle("h3", fontName="Sans-Bold", fontSize=10.5, leading=14, textColor=TEAL_DEEP, spaceBefore=8, spaceAfter=3)
code = ParagraphStyle("code", fontName="Mono", fontSize=8.8, leading=12.5, textColor=INK, backColor=PANEL,
                      borderColor=LINE, borderWidth=0.6, borderPadding=6, spaceBefore=4, spaceAfter=10, leftIndent=6, rightIndent=6)
caption = ParagraphStyle("caption", parent=small, alignment=TA_CENTER, spaceBefore=3, spaceAfter=12)
tip = ParagraphStyle("tip", parent=body, backColor=colors.HexColor("#E2F6F4"), borderColor=TEAL, borderWidth=0.8,
                     borderPadding=7, spaceBefore=6, spaceAfter=12, leftIndent=7, rightIndent=7)
warn = ParagraphStyle("warn", parent=tip, backColor=colors.HexColor("#FFF5E6"), borderColor=colors.HexColor("#E8A33D"))
cell = ParagraphStyle("cell", parent=body, fontSize=8.8, leading=12, spaceAfter=0)
cellb = ParagraphStyle("cellb", parent=cell, fontName="Sans-Bold")
cellm = ParagraphStyle("cellm", parent=cell, fontName="Mono", fontSize=8.3)
toc1 = ParagraphStyle("toc1", fontName="Sans", fontSize=10.5, leading=17, textColor=INK, leftIndent=8)

W, H = A4
MARGIN = 20 * mm
CONTENT_W = W - 2 * MARGIN


# ---------- Page templates ----------
def cover_page(c, doc):
    c.saveState()
    c.setFillColor(BLACK)
    c.rect(0, 0, W, H, fill=1, stroke=0)
    c.setFillColor(colors.HexColor("#0B1C1C"))
    c.circle(W / 2, H * 0.60, 190, fill=1, stroke=0)
    c.drawImage(os.path.join(IMG, "logo-tile.png"), W / 2 - 95, H * 0.60 - 95, 190, 190, mask="auto")
    c.setFillColor(colors.white)
    c.setFont("Mono-Bold", 34)
    c.drawCentredString(W / 2, H * 0.36, "WhisperDesk")
    c.setFillColor(TEAL)
    c.setFont("Mono", 13)
    c.drawCentredString(W / 2, H * 0.36 - 28, "darkcipher0x@whisperdesk:~$ man whisperdesk")
    c.setFillColor(colors.HexColor("#D2F2EF"))
    c.setFont("Sans", 15)
    c.drawCentredString(W / 2, H * 0.36 - 64, "User & Administrator Guide")
    c.setFillColor(colors.HexColor("#5A8986"))
    c.setFont("Sans", 10.5)
    c.drawCentredString(W / 2, H * 0.36 - 86, f"Version {VERSION}  ·  {date.today():%B %Y}")
    c.setFont("Mono", 9)
    c.drawCentredString(W / 2, 40, "by darkcipher0x  ·  local, GPU-accelerated transcription")
    c.restoreState()


def normal_page(c, doc):
    c.saveState()
    c.setFillColor(BLACK)
    c.rect(0, H - 14 * mm, W, 14 * mm, fill=1, stroke=0)
    c.drawImage(os.path.join(IMG, "logo.png"), MARGIN, H - 12 * mm, 10 * mm, 10 * mm, mask="auto")
    c.setFillColor(colors.white)
    c.setFont("Mono-Bold", 10)
    c.drawString(MARGIN + 13 * mm, H - 8.6 * mm, "WhisperDesk")
    c.setFillColor(TEAL)
    c.setFont("Mono", 8.5)
    c.drawRightString(W - MARGIN, H - 8.6 * mm, f"User & Administrator Guide · v{VERSION}")
    c.setStrokeColor(TEAL)
    c.setLineWidth(1.2)
    c.line(0, H - 14 * mm, W, H - 14 * mm)
    c.setFillColor(MUTED)
    c.setFont("Sans", 8)
    c.drawString(MARGIN, 10 * mm, "WhisperDesk by darkcipher0x")
    c.drawRightString(W - MARGIN, 10 * mm, f"{doc.page}")
    c.restoreState()


class GuideDoc(BaseDocTemplate):
    def __init__(self, path):
        super().__init__(path, pagesize=A4, title="WhisperDesk User & Administrator Guide", author="darkcipher0x",
                         subject=f"WhisperDesk {VERSION}", leftMargin=MARGIN, rightMargin=MARGIN,
                         topMargin=22 * mm, bottomMargin=18 * mm)
        cover = Frame(0, 0, W, H, id="cover")
        frame = Frame(MARGIN, 18 * mm, CONTENT_W, H - 22 * mm - 18 * mm, id="body")
        self.addPageTemplates([PageTemplate("cover", [cover], onPage=cover_page),
                               PageTemplate("normal", [frame], onPage=normal_page)])

    def afterFlowable(self, f):
        if isinstance(f, Paragraph) and f.style.name == "h1":
            self.notify("TOCEntry", (0, f.getPlainText(), self.page))


# ---------- Helpers ----------
story = []
P = lambda t, s=body: story.append(Paragraph(t, s))
H1 = lambda t: story.append(Paragraph(t, h1))
H2 = lambda t: story.append(Paragraph(t, h2))
H3 = lambda t: story.append(Paragraph(t, h3))
TIP = lambda t: story.append(Paragraph("<b>Tip</b>  " + t, tip))
WARN = lambda t: story.append(Paragraph("<b>Note</b>  " + t, warn))
CODE = lambda t: story.append(Paragraph(t.replace("\n", "<br/>"), code))


def BULLETS(items):
    for i in items:
        story.append(Paragraph(i, bullet, bulletText="\u2022"))
    story.append(Spacer(1, 4))


def STEPS(items):
    for n, i in enumerate(items, 1):
        story.append(Paragraph(i, bullet, bulletText=f"{n}."))
    story.append(Spacer(1, 4))


def SHOT(name, cap, width=CONTENT_W):
    from reportlab.lib.utils import ImageReader
    path = os.path.join(IMG, name)
    iw, ih = ImageReader(path).getSize()
    h = width * ih / iw
    img = Image(path, width=width, height=h)
    t = Table([[img]], colWidths=[width])
    t.setStyle(TableStyle([("BOX", (0, 0), (-1, -1), 0.8, LINE), ("LEFTPADDING", (0, 0), (-1, -1), 0),
                           ("RIGHTPADDING", (0, 0), (-1, -1), 0), ("TOPPADDING", (0, 0), (-1, -1), 0),
                           ("BOTTOMPADDING", (0, 0), (-1, -1), 0)]))
    story.append(KeepTogether([t, Paragraph(cap, caption)]))


def TABLE(rows, widths, header=True, mono_first=False):
    data = []
    for r_i, row in enumerate(rows):
        out = []
        for c_i, v in enumerate(row):
            st = cellb if (header and r_i == 0) else (cellm if (mono_first and c_i == 0) else cell)
            out.append(Paragraph(v, st))
        data.append(out)
    t = Table(data, colWidths=widths, repeatRows=1 if header else 0)
    style = [("GRID", (0, 0), (-1, -1), 0.5, LINE), ("VALIGN", (0, 0), (-1, -1), "TOP"),
             ("LEFTPADDING", (0, 0), (-1, -1), 5), ("RIGHTPADDING", (0, 0), (-1, -1), 5),
             ("TOPPADDING", (0, 0), (-1, -1), 4), ("BOTTOMPADDING", (0, 0), (-1, -1), 4)]
    if header:
        style += [("BACKGROUND", (0, 0), (-1, 0), PANEL), ("LINEBELOW", (0, 0), (-1, 0), 1, TEAL)]
    t.setStyle(TableStyle(style))
    story.append(t)
    story.append(Spacer(1, 10))


# =====================================================================
# Cover + contents
# =====================================================================
story.append(NextPageTemplate("normal"))
story.append(PageBreak())
story.append(Paragraph("Contents", ParagraphStyle("contents", parent=h1)))
toc = TableOfContents()
toc.levelStyles = [toc1]
story.append(toc)
story.append(PageBreak())

# =====================================================================
H1("What's new in 1.8")
TABLE([
    ["Feature", "Where / how"],
    ["Microsoft Store edition", "WhisperDesk can be installed from the Microsoft Store. The Store copy is signed by Microsoft and "
     "updated by the Store. It is the same program: everything still runs on this PC, and your library, voices, settings and "
     "models are in the same folders as with the MSI, so they are kept if you uninstall or switch between the two."],
],[42 * mm, CONTENT_W - 42 * mm])

H1("What's new in 1.7")
TABLE([
    ["Feature", "Where / how"],
    ["Encryption (optional)", "Settings → <b>Security &amp; privacy</b> → Turn on encryption. The library, transcripts, voiceprints, forensic "
     "logs, live recordings and crash-recovery files are encrypted with AES-256-GCM; titles and folder names are hidden on disk. "
     "You choose a passphrase and receive a one-time <b>recovery key</b>."],
    ["Lock screen and auto-lock", "With encryption on, WhisperDesk asks for the passphrase at start, locks after inactivity, when Windows "
     "locks or sleeps, or with <b>Ctrl+L</b>. Locking forgets the key."],
    ["Two-factor sign-in and Windows Hello", "Optional 6-digit codes from an authenticator app, and Windows Hello either instead of the "
     "passphrase or required in addition to it (binds the key to this PC's security chip)."],
    ["Network transparency", "Nothing connects at start-up. Model downloads ask first (or Allow / Never). Every connection is listed "
     "under Network activity with the feature that made it. All model files are pinned and checked with SHA-256."],
    ["Add-on permissions and sandbox", "Add-ons declare what they need; you approve each permission. Third-party add-ons run in a Windows "
     "sandbox with no access to your files, library or network unless allowed. Approval is tied to the add-on's fingerprint."],
    ["Fixed in 1.7.1", "Upgrading from 1.5.x could leave out a component (onnxruntime.dll) so 1.7.0 closed straight away. The installer now always "
     "replaces WhisperDesk's files. If that component is ever missing, WhisperDesk still starts normally; only speaker recognition and voice "
     "detection are unavailable until you repair it. PCs without an NPU no longer load any NPU software at start-up."],
    ["Hardening", "Live captions can't be read by other websites; restored backups can't switch add-ons on; folder links are never "
     "followed when moving or deleting; error logs no longer contain file names; copied text stays out of clipboard history."],
],[42 * mm, CONTENT_W - 42 * mm])

H1("What's new in 1.6 (first released as part of 1.7)")
TABLE([
    ["Feature", "Where / how"],
    ["NPU transcription", "On a PC with an Intel NPU (Core Ultra, e.g. Copilot+ PCs), <b>HARDWARE: Auto</b> runs Whisper on the NPU "
     "automatically for Base, Small and Large-v3 Turbo; other models and PCs use the GPU (Vulkan) or CPU as before. "
     "The model's NPU version downloads on first use. Settings → Transcription shows what was found; any NPU problem falls "
     "back to the GPU/CPU by itself. Everything still runs on this PC."],
    ["Speaker voiceprints on the NPU", "Speaker recognition and live speaker identification use the NPU too, once a "
     "start-up check shows the result matches the CPU's (saved voices keep matching). Settings → Speakers → "
     "Voiceprint hardware. Diarization and voice detection stay on the CPU."],
    ["First run", "The model is prepared for the NPU in the background while idle; the status bar shows each step "
     "(download, compile, load, warm-up) with a timer. Preparation happens once and is kept."],
],[42 * mm, CONTENT_W - 42 * mm])

H1("What's new in 1.5")
TABLE([
    ["Feature", "Where / how"],
    ["Forensics view", "In the player, switch <b>☰ Transcript / ∿ Forensics</b> (or Library → right-click → ∿ Forensics…). "
     "One zoomable display shows a time ruler with markers, the speaker timeline, the waveform, a spectrogram (0–4 or 0–8 kHz, "
     "60–100 dB range) with the pitch track, the loudness curve and an overview of the whole recording. Wheel zooms, "
     "Shift+wheel pans, drag selects, click jumps there; hover shows the exact time and frequency."],
    ["Selection tools", "<b>▶ Selection</b> (with Loop), <b>Measure</b> (level, peak, clipping, pitch, jitter, shimmer, HNR, formants, "
     "spectral centroid), <b>Who is speaking?</b> (the selection's voice compared with every speaker in the recording and your "
     "voice library, using each voice's own consistency as the baseline) and <b>Add note…</b> (time-stamped observation)."],
    ["Integrity check", "Whole-file checks from the forensics view; findings appear as orange markers on the ruler."],
    ["Tamper-evident audit log", "Every action (opened, measured, identified, note, integrity check, export) is appended to "
     "<font name=\"Mono\">forensics-log.jsonl</font> in the recording's folder with who, when, which computer, the media's SHA-256 "
     "and the hash of the previous entry. <b>Verify chain</b> recomputes it: any edited, deleted or reordered entry is reported."],
    ["Forensic report", "<b>Export report…</b>: evidence item details and hashes, audit-trail verification, an image of the current "
     "view, all findings and the full audit log, as PDF, HTML, Word, text or JSON, with a .sha256.txt fingerprint file."],
    ["Fixed in 1.5.1", "Privacy clean-up: removed a screenshot of a personal test recording from this guide, demo names "
     "replaced with fictional ones, and no local build paths in the shipped files."],
    ["Fixed in 1.5.2", "Copy buttons (OBS overlay URL, transcript, selected lines, summary) no longer show an error when "
     "another program is briefly holding the clipboard; WhisperDesk retries, and asks you to try again only if it stays busy."],
    ["Changed in 1.5.2", "New libraries are kept in %LOCALAPPDATA%\\WhisperDesk\\Library, which is never synced. A banner warns when "
     "the library is inside OneDrive, Dropbox, Google Drive or iCloud and offers a safe move. Existing libraries are not moved."],
],[42 * mm, CONTENT_W - 42 * mm])

H1("What's new in 1.4")
TABLE([
    ["Feature", "Where / how"],
    ["Install folder (1.4.2)", "Installs to <font name=\"Mono\">C:\\Program Files\\DarkCipher Systems\\WhisperDesk</font>. Upgrading moves it from the old <font name=\"Mono\">C:\\Program Files\\WhisperDesk</font> automatically; your data is untouched."],
    ["Open source (1.4.1)", "WhisperDesk is now open-source under the <b>MIT License</b>. Shown on the launch screen, in "
     "Help → About (with a Licence button) and in the installer; the text is in LICENSE.txt in the install folder."],
    ["Stability on long jobs", "Transcription preview, speaker assignment and saving never block the window. "
     "<b>Performance mode</b> (Settings → Performance): Light keeps the PC free while you live-transcribe or play, "
     "Balanced (default), Maximum. The PC is kept awake while jobs run."],
    ["Accuracy", "<b>Skip silence</b> sends only speech to Whisper; the <b>hallucination filter</b> drops invented "
     "“Thank you.” lines and repeat loops. Speaker detection compares several samples per voice, merges look-alikes "
     "and is capped (Settings → Speakers)."],
    ["Custom models", "Tools → Models → <b>Import a custom model…</b>: any whisper.cpp GGML model (fine-tuned, distilled, "
     "quantised, language-specific). Imported models show with ★ in the Model list."],
    ["Live feed window", "View → Live feed (Ctrl+Shift+F) or <b>⧉ Feed</b>: a clean, chat-style read-only feed with "
     "speaker colours. Snap left/right/bottom, full screen (F11), bigger text (Ctrl + wheel), always on top. The player "
     "has one too that replays lines as the audio plays."],
    ["OBS overlays", "Tools → Add-ons → <i>Live captions for OBS</i>, then Tools → OBS / browser captions. Three looks: "
     "darkcipher0x <b>terminal</b>, stacked <b>chat</b> and classic <b>subtitles</b>, each with URL options."],
    ["Reports on many recordings", "Library → select recordings → <b>Report…</b> (or Tools → Report on recordings): one "
     "combined or separate reports with overview, summaries, participation across recordings, keyword hits, transcripts, "
     "edit log, SHA-256 integrity and an optional consolidated <b>local-AI</b> report. PDF, Word, HTML, text, JSON, zip, "
     "plus a SHA-256 manifest."],
    ["Voice &amp; audio analysis", "Tools → Voice &amp; audio analysis: voice profiles (pitch, jitter, shimmer, HNR, formants, "
     "speaking rate), voice comparison against each voice's own consistency, and audio integrity checks (metadata, "
     "hashes, clipping, digital silence, background steps, mains hum / ENF). Exportable report."],
    ["Backups", "File → <b>Backup…</b> / <b>Restore from backup…</b>: library, voices, settings, add-ons (and models), "
     "optionally encrypted with a password (AES-256-GCM). Every file is verified with SHA-256 on restore."],
    ["Summary transparency", "Every summary ends with <i>How this summary was made</i>: software version, the model that "
     "wrote it (with Ollama version and model details), where it ran, computer, Windows account and time taken."],
    ["Library", "Folder tree with <b>+ / −</b>, multi-select, drag recordings onto folders, Delete key, "
     "<b>Re-transcribe</b> (keeps the old version in a <i>previous</i> folder), report, share."],
    ["Files list", "Right-click: transcribe selected, re-transcribe, <b>schedule</b> (e.g. overnight, optional sleep "
     "afterwards), show transcript, clear finished. The transcript pane has <b>Clear</b> and more right-click options."],
    ["Share", "Share / export… opens the <b>Windows share sheet</b> (Mail, Teams, Nearby Sharing…) or saves as web player, "
     "session package, PDF, Word, text or summary."],
    ["Menus &amp; settings", "File → Import / Export, Backup; Tools menu; Settings in sections (General, Performance, "
     "Transcription, Speakers, Output, Live, Summary &amp; AI, Storage, Privacy). Output formats are a single dropdown with a "
     "saved default."],
    ["Look &amp; feel", "Launch screen, new About window (no sound), the guide opens inside the app (F1) with "
     "“Open in PDF app”, Privacy &amp; security page, cloud-sync warning when the library is inside OneDrive/Dropbox."],
    ["Installer", "Detects an installed version (upgrade / repair / remove). Optional FFmpeg and Ollama via winget, own "
     "paths for FFmpeg, library and models. Uninstall can also remove your library, voices, settings and models — each "
     "ticked separately with a warning. Start menu → <i>WhisperDesk - Repair or uninstall</i>."],
], [42 * mm, CONTENT_W - 42 * mm])

H1("What's new in 1.3")
TABLE([
    ["Feature", "Where / how"],
    ["Fixed in 1.3.2", "<b>No more freezing on long files</b>: the transcript preview updates in small batches and speaker "
     "assignment runs in the background. <b>Skip silence</b> (on by default) only sends speech to Whisper, which stops "
     "invented “Thank you.” lines and repeat loops; a <b>hallucination filter</b> removes any that remain. Speaker detection now "
     "compares several samples per voice, merges look-alike voices and is capped (default 10). New <b>Performance mode</b> "
     "(Light / Balanced / Maximum) in Settings. Every speaker dropdown is grouped the same way: Voice library, Named in this "
     "recording, Unidentified voices, Named in other recordings. The transcript pane has a <b>Clear</b> button and more right-click options."],
    ["Fixed in 1.3.1", "Saving speaker changes from the library now stays on the same recording. Each line's speaker "
     "dropdown (and right-click, Assign) now lists the people in the recording, <b>everyone in your voice library</b> and "
     "people named in other recordings. Picking one adds them to the recording."],
    ["Play / Stop everywhere", "Every line's play button turns into a stop button while it plays. The whole line plays "
     "(up to the next line) unless you press it again. The same applies to speaker clips, the review dialog and the edit dialog."],
    ["Player transport", "Previous line, Play/Pause, Next line and Stop buttons (Ctrl+Left, Space, Ctrl+Right, Esc). "
     "The shared web player has the same controls."],
    ["Right-click menus", "Files list: transcribe, save to library, summary, re-analyse, add/remove/clear, show in Explorer. "
     "Library recordings: open, player, summary, share, re-analyse, edit, <b>move to folder</b>, delete. "
     "Library folders: new, rename, delete, show in Explorer. All buttons are still there."],
    ["Resizable columns", "Drag the edges of any column header in the Files, Library and Voice library lists."],
    ["Multi-select speakers", "Ctrl/Shift+click speaker cards, then <b>Name selected…</b> (one name and sex for all) or "
     "<b>Merge selected</b> (combines them into one speaker and reassigns their lines)."],
    ["Re-analyse speakers", "<b>Re-analyse…</b> in the Speakers panel, or right-click a library recording. Choose the number "
     "of speakers and whether to compare with the voice library. You are warned before existing labels are overwritten. "
     "Line text and edit history are kept. Needs the recording's audio."],
    ["Voice library", "Select a voice to edit its <b>name and sex</b> and click <b>Save</b> (optionally renaming it in every "
     "saved recording), or right-click it and choose <b>Rename</b>. The lower list shows <b>every line that voice spoke</b> "
     "across the library, including unnamed speakers that sound like them. Play any line, or double-click it to open the player there."],
    ["Models folder", "File, Settings, Tools &amp; storage. <b>Move models to…</b> copies every model to a new folder or drive, "
     "switches over and deletes the old copies."],
    ["Detailed summary report", "Every summary now starts with a written report: overview, a walk-through of what was "
     "said with quotes and timestamps, and participation. It appears in TXT, Word, PDF, JSON and the web player."],
    ["AI-written report (optional)", "File, Settings, Summary report: tick <b>Write the report with a local AI model</b>. "
     "This needs Ollama (ollama.com) and a model, e.g. <font face='Courier'>ollama pull llama3.1:8b</font>. "
     "It runs on your GPU and nothing leaves the PC. The <b>prompt is fully editable</b> there, with "
     "{title} {date} {duration} {speakers} {transcript} placeholders and <b>Reset to default</b>. "
     "In the Summary window, <b>Regenerate report</b> runs it again."],
    ["Crash safety", "Unexpected errors are logged to %LOCALAPPDATA%\\WhisperDesk\\logs and the app keeps running. "
     "Live sessions are autosaved every 30 seconds. After a crash or power cut, the session is restored on the next start "
     "into the library's <b>Recovered</b> folder. The recording is flushed to disk every 10 seconds. If the disk fills up, "
     "audio recording stops but the transcript carries on. A warning shows if transcription falls behind."],
    ["Status bar", "The progress bar only appears while a job is running (no more stray bar at the bottom right)."],
], [CONTENT_W * 0.26, CONTENT_W * 0.74])
story.append(PageBreak())

# =====================================================================
H1("1. Introduction")
P("<b>WhisperDesk</b> turns speech into text on your own PC. It transcribes audio and video files, transcribes live "
  "audio from a microphone, Voicemeeter or anything your PC is playing, tells the speakers apart and names them, and "
  "keeps everything organised in a searchable recordings library. Nothing is uploaded: transcription, speaker "
  "identification and voice recognition all run locally, on your graphics card where possible.")
H2("Highlights")
BULLETS([
    "<b>Open source (MIT License)</b>: free to use, study, change and share.",
    "<b>Fast GPU transcription</b> with OpenAI Whisper models (whisper.cpp + Vulkan): on an AMD RX 9070 XT the "
    "recommended Large-v3 Turbo model runs about 20–40× faster than real time. Works on AMD, NVIDIA and Intel GPUs, "
    "with CPU fallback.",
    "<b>Speaker identification</b>: every timestamped line is tagged with who spoke, e.g. "
    "<font name=\"Mono\">[00:01:12] Anna (Female): …</font>. Speaker count is detected automatically.",
    "<b>Voice library</b>: name a voice once and WhisperDesk recognises that person in future files and live sessions.",
    "<b>Live transcription</b> from any input, including Voicemeeter virtual outputs and PC loopback.",
    "<b>Recordings library</b> with folders, types, tags, notes, metadata and full-text search.",
    "<b>Exports</b>: TXT, SRT, VTT, JSON, Word (.docx), PDF, and subtitles burned into videos.",
    "<b>Add-ons</b>: OBS caption overlay, live text file, webhook, and an SDK for your own extensions.",
    "<b>Review before saving</b>: play any line (" + PLAY + ") to check what was said and fix the speaker.",
])
H2("Who this guide is for")
P("Sections 2–11 are for everyone using WhisperDesk. Section 12 (<i>Administration</i>) covers silent installation, "
  "file locations, identifiers, privacy, backup and troubleshooting for whoever installs and maintains it.")

# =====================================================================
H1("2. Installing WhisperDesk")
H2("System requirements")
TABLE([
    ["Component", "Minimum", "Recommended"],
    ["Windows", "Windows 10 64-bit (version 1809+)", "Windows 11 64-bit"],
    ["Processor", "4-core x64 with AVX2", "8+ cores (e.g. Ryzen 7 9800X3D)"],
    ["Memory", "8 GB", "16–32 GB"],
    ["Graphics", "Any (CPU mode)", "Vulkan GPU with 6+ GB VRAM (AMD, NVIDIA or Intel)"],
    ["Disk", "1 GB + models (75 MB – 3.1 GB each)", "SSD with 10 GB free"],
    ["FFmpeg", "Required for reading audio/video", "winget install Gyan.FFmpeg"],
], [32 * mm, 62 * mm, CONTENT_W - 94 * mm])
H2("Install with the MSI (recommended)")
STEPS([
    "Run <font name=\"Mono\">WhisperDesk-1.1.0-x64.msi</font>. If Windows SmartScreen shows <i>“Windows protected your "
    "PC”</i>, click <b>More info → Run anyway</b> (the installer is not code-signed).",
    "Accept the licence, choose the install folder (default <font name=\"Mono\">C:\\Program Files\\DarkCipher Systems\\WhisperDesk</font>) "
    "and click <b>Install</b>.",
    "Start <b>WhisperDesk</b> from the Start menu.",
])
P("Installing a newer MSI upgrades the existing installation automatically; your settings, models, voices and library "
  "are kept.")
H2("Install from the Microsoft Store")
P("Search for <b>WhisperDesk</b> in the Microsoft Store (Windows 10 version 2004 or later) and click <b>Get</b>. The Store "
  "keeps it up to date. FFmpeg is installed separately (<font name=\"Mono\">winget install Gyan.FFmpeg</font>). To repair or "
  "remove the Store copy use <b>Settings → Apps → Installed apps → WhisperDesk</b>; your library, voices, settings and models "
  "are kept when it is removed. Use either the Store copy or the MSI on one PC, not both: they share the same data.")
H2("Portable version")
P("Unzip <font name=\"Mono\">WhisperDesk-1.1.0-x64-portable.zip</font> anywhere and run "
  "<font name=\"Mono\">WhisperDesk.exe</font>. Keep all files together: the <font name=\"Mono\">runtimes</font> folder holds "
  "the GPU engine. No .NET installation is required.")
H2("FFmpeg")
P("WhisperDesk uses FFmpeg to read audio and video and to burn subtitles. Install it once:")
CODE("winget install Gyan.FFmpeg")
P("Alternatively place <font name=\"Mono\">ffmpeg.exe</font> next to <font name=\"Mono\">WhisperDesk.exe</font>, or point to "
  "it in <b>File → Settings → Tools &amp; storage</b>.")
H2("First run")
P("The first time you transcribe, WhisperDesk downloads the selected Whisper model (Large-v3 Turbo: 1.6 GB) and the "
  "small speaker models (about 32 MB). This happens once; progress is shown in the status bar.")

# =====================================================================
H1("3. Quick start")
STEPS([
    "Drag an audio or video file onto the window (or <b>File → Add files…</b>, <font name=\"Mono\">Ctrl+O</font>).",
    "Leave <b>Model</b> on <i>Large-v3 Turbo</i>, <b>Device</b> on <i>GPU (Vulkan)</i> and <b>Identify speakers</b> ticked.",
    "Tick the outputs you want under <b>Save as</b> (e.g. TXT and SRT, Word or PDF).",
    "Click <b>Transcribe</b>. Lines appear as they are recognised; speakers are added when the file finishes.",
    "In the <b>Speakers</b> panel type each person's name, check the suggested sex, then click "
    "<b>Save names &amp; update files</b>.",
])
SHOT("files.png", "The main window after transcribing a meeting with five speakers.")

# =====================================================================
H1("4. The main window")
TABLE([
    ["Area", "What it does"],
    ["Menu bar", "<b>File</b> (add files, voice library, add-ons, settings), <b>View</b> (theme), <b>Help</b> (models folder, about)."],
    ["Header", "Logo, version and the engine badge (<font name=\"Mono\">engine: gpu · vulkan</font> when the GPU is used). "
               "The theme switch picks System, Dark or Light."],
    ["Model / Device / Language / Options", "Shared by file and live transcription. See section 5."],
    ["Tabs", "<b>Files</b> (queue of files), <b>Live</b> (live transcription), <b>Library</b> (saved recordings)."],
    ["Drag handle", "Drag the small bar between the tabs and the transcript to resize them."],
    ["Transcript", "One row per timestamped line: time, " + PLAY + " play, speaker picker, text. "
                   "<b>Export…</b> and <b>Copy</b> are above it."],
    ["Speakers", "One card per voice: play a sample, name, sex, remember voice, remove. "
                 "<b>+ Add speaker</b> and <b>Save names &amp; update files</b> below."],
    ["Status bar", "Current activity and overall progress."],
], [45 * mm, CONTENT_W - 45 * mm])

# =====================================================================
H1("5. Transcribing files")
H2("Models")
TABLE([
    ["Model", "Size", "Use it for"],
    ["Tiny / Base", "75 / 142 MB", "Quick drafts, very old hardware"],
    ["Small", "466 MB", "Good quality on CPU-only PCs"],
    ["Medium", "1.5 GB", "High quality"],
    ["Large-v3 Turbo", "1.6 GB", "<b>Recommended</b>: best balance of speed and accuracy"],
    ["Large-v3", "3.1 GB", "Maximum accuracy, slower"],
], [38 * mm, 28 * mm, CONTENT_W - 66 * mm])
H2("Options")
BULLETS([
    "<b>Device</b>: GPU (Vulkan) or CPU only. If no Vulkan GPU is found the engine badge says so and the CPU is used.",
    "<b>Language</b>: Auto-detect, or pick the spoken language for slightly better accuracy.",
    "<b>Translate to English</b>: produces an English transcript from any supported language.",
    "<b>Identify speakers</b> + count: <i>Auto</i> works best for most recordings. Choose an exact number (2–20) only "
    "if Auto merges or splits people.",
])
H2("Outputs (“Save as”)")
TABLE([
    ["Output", "Contents"],
    ["TXT", "Timestamped log: <font name=\"Mono\">[00:01:12] Anna (Female): text</font> (plain text when speakers are off)."],
    ["SRT / VTT", "Subtitles; speaker names included (can be turned off in Settings)."],
    ["JSON", "Machine-readable: segments with start/end, speaker, sex, language, confidence, plus a speaker summary."],
    ["Word / PDF", "Formatted document with title, speaker summary and colour-coded speaker names."],
    ["Burn into video", "Video files only: creates <font name=\"Mono\">name.subtitled.mp4</font> with subtitles drawn into the picture."],
], [32 * mm, CONTENT_W - 32 * mm])
P("Files are saved next to each source file, or in the folder chosen with <b>Browse…</b>. Click <b>Open output</b> to "
  "open that folder. Several files can be queued; failed files can be retried by clicking Transcribe again.")
SHOT("export-pdf.png", "A PDF export: speaker summary, timestamps and colour-coded speakers.", width=CONTENT_W * 0.62)

# =====================================================================
H1("6. Speakers")
P("With <b>Identify speakers</b> on, WhisperDesk finds who spoke when, gives each voice a card in the Speakers panel and "
  "tags every line. It then double-checks each line against every voice and moves lines that clearly belong to "
  "someone else.")
H2("Naming speakers")
STEPS([
    "Click <b>" + PLAY + " Play</b> on a speaker card to hear a few seconds of that voice.",
    "Type the name (e.g. <i>Anna</i>) and check <b>Sex</b>.",
    "Leave <b>Remember this voice</b> ticked to add the voice to your voice library.",
    "Click <b>Save names &amp; update files</b>: the transcript files are rewritten with the names.",
])
H2("Picking a known person")
P("The <b>Name</b> box is also a dropdown of everyone in your voice library: click the arrow and pick the person. Their "
  "stored sex is filled in, and when you save, this recording is added as another sample of their voice so "
  "recognition keeps improving. Speakers that were not recognised show the closest known voice, e.g. "
  "<i>closest: Anna 42%</i>, so you can see why and pick the name if it is right.")
H2("About the Male/Female suggestion")
WARN("The suggestion comes from voice pitch. It is usually right for typical adult voices, but can be wrong for "
     "children, phone audio, people with naturally higher or lower voices, or overlapping speech. It is only a "
     "suggestion: always check it, and set <i>Unspecified</i> if you prefer not to record it. The thresholds can be "
     "adjusted in Settings.")
H2("Fixing mistakes")
BULLETS([
    "<b>One line wrong</b>: pick the right person in that line's speaker dropdown. Use " + PLAY + " to listen first.",
    "<b>Many lines</b>: select them (Ctrl/Shift+click), right-click → <b>Assign selected lines to</b>.",
    "<b>Someone missing</b>: <b>+ Add speaker</b>, then assign their lines. When you save, WhisperDesk learns their "
    "voice from those lines.",
    "<b>Two cards for one person</b>: assign one card's lines to the other, then remove the empty card with " + CROSS + ".",
])
TIP("Very short lines (“Yes.”, “Thanks.”) carry too little voice to recognise reliably. A quick listen with " + PLAY +
    " is the fastest way to confirm them.")

H2("Correcting the text (audited edits)")
P("Click <b>" + PENCIL + "</b> on any line to correct what was transcribed. The edit window shows the original transcription and any "
  "earlier edits, lets you play the line, and requires a <b>reason</b> (at least 5 characters) before <b>Confirm edit</b> "
  "becomes available. Each confirmed edit is recorded with the <b>date, time, time zone and Windows user</b>, and the line "
  "is tagged <b>" + PENCIL + " edited</b> — hover the tag to see the full history.")
BULLETS([
    "TXT exports end each edited line with <font name=\"Mono\">[edited]</font> and append an <b>EDIT LOG</b>.",
    "Word and PDF exports mark edited lines and add an <b>Edit log</b> appendix (who, when, why, before, after).",
    "JSON exports include <font name=\"Mono\">edited</font>, <font name=\"Mono\">originalText</font> and every edit.",
    "Library recordings keep the full history; add-ons receive a <font name=\"Mono\">LineEdited</font> event.",
    "Subtitles (SRT/VTT/burned-in) show the corrected text without tags.",
])
P("In the main window, click <b>Save changes &amp; update files</b> after editing to rewrite the transcript files.")

# =====================================================================
H1("7. Voice library")
P("The voice library stores <i>voiceprints</i> (numbers describing a voice, not audio) of people you have named. In new "
  "files and live sessions, matching speakers are named automatically and marked <b>" + CHECK + " recognised (NN%)</b>.")
BULLETS([
    "<b>Add a voice by naming a speaker</b> and saving with <i>Remember this voice</i> ticked.",
    "<b>Add a voice ahead of time</b>: <b>File → Voice library…</b>, type a name, pick sex, click <b>Choose audio…</b> "
    "and select 10–60 seconds of that person talking alone. Adding more samples of the same name improves recognition.",
    "<b>Forget a voice</b>: select it and click <b>Forget selected voice</b>.",
])
SHOT("voices.png", "Voice library.", width=CONTENT_W * 0.7)
P("If the wrong names appear, raise <b>Voice library match</b> in Settings (e.g. 0.6). If known people are not "
  "recognised, lower it (e.g. 0.45).")

# =====================================================================
H1("8. Live transcription")
SHOT("live.png", "The Live tab.")
H2("Choosing the input")
BULLETS([
    "<b>Microphones and line-ins</b> appear with a microphone icon.",
    "<b>Voicemeeter</b>: choose <font name=\"Mono\">VoiceMeeter Out B1</font> (or B2/B3) to transcribe whatever you route "
    "to that bus — for example your mic plus Discord. In Voicemeeter, enable the B1 button on each strip you want "
    "transcribed.",
    "<b>What your PC is playing</b>: entries marked <i>(what's playing)</i> capture a playback device (loopback), e.g. "
    "a video call or stream.",
    "Click <font name=\"Sym\">\u21BB</font> after plugging in a new device.",
])
H2("Running a session")
STEPS([
    "Choose the input, and decide whether to <b>Keep audio recording</b> (off = transcript only, no audio on disk).",
    "Click <b>Start live</b>. Watch the <b>Level</b> meter move when someone talks.",
    "Lines appear shortly after each pause, tagged with the speaker. Known voices are named automatically; new voices "
    "become Speaker 1, 2, 3… — you can name them in the Speakers panel while the session runs.",
    "Click <b>Stop</b>. The <b>Save to library</b> dialog opens with a review panel (next section).",
])
TIP("Tune live behaviour in <b>Settings → Live transcription</b>: <i>Pause that ends a line</i> (default 600 ms), "
    "<i>Longest line</i> (15 s) and <i>Same-person threshold</i> (raise it if different people are merged).")

# =====================================================================
H1("9. Review and save to the library")
P("Saving a live session, or clicking <b>Save to library…</b> for a transcribed file, opens the review dialog:")
SHOT("review.png", "Review before saving: title, type, folder, tags and notes on the left; the transcript on the right.")
BULLETS([
    "Click " + PLAY + " on any line to hear exactly that part of the recording.",
    "Fix who said it with the line's dropdown, and name each speaker in the <b>Speakers</b> list.",
    "Enter a <b>Title</b>, <b>Type</b> (Meeting, Interview, Podcast, Call, Lecture, Live, Voice note, Video, Other), "
    "<b>Folder</b> (+ New creates one), <b>Tags</b> and <b>Notes</b>.",
    "For files, choose <b>Link to the original file</b> (no extra space) or <b>Copy the file into the library</b>.",
    "<b>Don't save</b> discards a live session (and its temporary audio).",
])

# =====================================================================
H1("10. Recordings library")
SHOT("library.png", "The Library tab with folders, search and recording details.")
BULLETS([
    "<b>Folders</b>: <b>+ Folder</b> creates a folder inside the selected one; <b>Rename</b> and <b>Delete</b> manage it. "
    "<i>All recordings</i> shows everything.",
    "<b>Search</b> looks in titles, tags, notes, speaker names and the full transcript text. Filter by <b>type</b> with "
    "the dropdown.",
    "<b>Open transcript</b> (or double-click) loads it below for editing; rename speakers or fix lines, then "
    "<b>Save names &amp; update files</b> updates the library copy.",
    "<b>" + PLAY + " Play</b> opens the recording's audio. <b>Edit details…</b> changes title, type, tags, notes and folder. "
    "<b>Show in Explorer</b> opens its folder. <b>Delete</b> removes it permanently.",
])
H2("What is stored")
P("Each recording is a folder named <font name=\"Mono\">yyyy-MM-dd HH.mm – Title</font> containing:")
TABLE([
    ["File", "Contents"],
    ["metadata.json", "Title, type, date, duration, source, language, model, tags, notes, speakers (name, sex, talk time, voiceprint)."],
    ["transcript.json", "Every line: start, end, text, speaker, language."],
    ["transcript.txt / .srt", "Human-readable copies."],
    ["audio.wav / media.*", "The recording, if kept or copied (live audio is 16 kHz mono WAV, about 1.9 MB per minute)."],
], [38 * mm, CONTENT_W - 38 * mm], mono_first=True)

H2("Player and sharing")
P("<b>" + PLAY + " Player</b> opens a recording in player mode: play/pause, seek bar, click any line (or its " + PLAY + ") to "
  "jump there, and the current line is highlighted and followed as it plays. Press Space to play/pause.")
P("<b>Share…</b> (in the Library or the player) exports the session two ways:")
BULLETS([
    "<b>Web player (.html)</b> — one self-contained file with the audio embedded. Anyone can open it in a browser "
    "(no install): speakers, summary, clickable lines, edit tags and edit log.",
    "<b>WhisperDesk session (.wdsession)</b> — for other WhisperDesk users. Double-click it (or <b>File → Open shared "
    "session…</b>) to open it in the player; <b>+ Add to my library</b> imports it.",
])
TIP("Shared files contain compressed audio (about 0.5 MB per minute), the transcript, speakers and edit log. "
    "Voiceprints and local file paths are never included.")

H2("Call summary")
P("<b>Summary…</b> (above the transcript, or in the player) builds an offline summary: overview, who spoke and for how "
  "long, key points, action items, decisions and open questions — each with its timestamp and speaker. <b>Save all "
  "formats…</b> writes it as TXT, Word, PDF and JSON. Tick <b>Summary</b> under <b>Save as</b> to create "
  "<font name=\"Mono\">name.summary.*</font> automatically for every transcribed file. The web player includes the summary.")
WARN("The summary is generated on your PC without AI services. Action items, decisions and questions are detected from "
     "English phrasing; always check them against the transcript.")

# =====================================================================
H1("11. Exporting, subtitles and add-ons")
H2("Export…")
P("The <b>Export…</b> button above the transcript saves the transcript currently shown — a file, a live session or a "
  "library recording — as <b>Word (.docx)</b>, <b>PDF</b>, text, SRT, VTT or JSON.")
H2("Burned-in subtitles")
P("Tick <b>Burn into video</b> to also create <font name=\"Mono\">name.subtitled.mp4</font> for video files (audio files "
  "are skipped). Encoding uses the GPU video encoder (AMD AMF, NVIDIA NVENC or Intel QSV) when available. Style it in "
  "<b>Settings → Burned-in subtitles</b>: font, size, bold/italic, text colour, outlined or boxed style, outline/box "
  "colour and opacity, top/bottom position, margin and quality — with a live preview.")
SHOT("burn-frame.png", "Speaker-tagged subtitles burned into a 720p video.", width=CONTENT_W * 0.75)
H2("Add-ons")
P("<b>Tools → Add-ons…</b> lists add-ons. Tick one to enable it: the first time, WhisperDesk shows what the add-on asks to do "
  "(read live lines, read finished transcripts, use the network, write to a folder, keep secrets, start by itself) and you can "
  "untick anything. Edit its settings and click <b>Save settings</b>. Passwords, tokens and webhook URLs are kept in Windows "
  "Credential Manager, not in settings files. <b>Remove permissions</b> switches an add-on off and forgets everything it was allowed.")
P("Built-in add-ons run inside WhisperDesk. Third-party add-ons run in a separate, sandboxed process (a Windows AppContainer): "
  "they can't read your files, library or settings, can't start other programs, and only reach the network if you allowed it. "
  "If an add-on's DLL changes, it stays off until you approve it again.")
SHOT("addons.png", "Add-ons window.", width=CONTENT_W * 0.9)
H3("Live captions for OBS & browsers")
STEPS([
    "Enable the add-on (default port 7890).",
    "In OBS: <b>Sources → + → Browser</b>, URL <font name=\"Mono\">http://localhost:7890/overlay</font>, width 1920, height 300.",
    "Start a live session — captions appear on stream.",
])
P("Style the overlay with URL options, for example:")
CODE("http://localhost:7890/overlay?lines=2&amp;size=42&amp;speaker=1&amp;color=ffffff&amp;bg=00000099&amp;align=center")
P("Other apps and scripts can follow the transcript at <font name=\"Mono\">/events</font> (Server-Sent Events) or "
  "<font name=\"Mono\">/api/lines</font> (JSON). The server only listens on this PC (localhost), and web pages from other "
  "sites open in your browser can't read it.")
H3("Live transcript text file")
P("Keeps a text file updated with the last lines (mode <i>latest</i>) or the whole session (mode <i>append</i>). Ideal "
  "for OBS <b>Text (GDI+) → Read from file</b>.")
H3("Webhook")
P("POSTs each line and finished transcripts as JSON to a URL — connect Zapier, n8n, Home Assistant or your own server. This "
  "sends transcripts off the PC to that address: internet addresses must use https://, and the URL is stored in Windows "
  "Credential Manager.")
H3("Word export (sample)")
P("A sample third-party add-on that saves each finished transcript as a Word-compatible document. Its source is in "
  "<font name=\"Mono\">sdk\\SampleWordExport</font> and is the starting point for your own add-ons (section 15). It asks for "
  "<i>read finished transcripts</i> and <i>write to a folder</i>; you choose the folder when you approve it.")

# =====================================================================
H1("12. Settings reference")
SHOT("settings.png", "File → Settings (scroll for all sections).", width=CONTENT_W * 0.72)
TABLE([
    ["Setting", "Default", "Effect"],
    ["Brand accent colour", "36D2CB", "Accent for buttons and highlights (light mode uses a deeper shade)."],
    ["CPU threads", "0 (auto)", "Threads used by Whisper on the CPU."],
    ["GPU device index", "0", "Which Vulkan GPU to use when several are installed."],
    ["Flash attention", "Off", "Experimental speed-up on some GPUs."],
    ["Don't use previous text as context", "Off", "Prevents rare repetition loops."],
    ["Vocabulary hint", "(empty)", "Names and terms Whisper should spell correctly."],
    ["Separation sensitivity", "0.5", "Auto speaker count: lower = more speakers, higher = fewer."],
    ["Voice library match", "0.5", "Similarity needed to name a known voice."],
    ["Min speech / silence", "0.3 / 0.5 s", "Shortest speech turn / pause that splits turns."],
    ["Suggest male below / female above", "158 / 168 Hz", "Pitch thresholds for the sex suggestion."],
    ["Include sex in tags", "On", "“Anna (Female):” vs “Anna:”."],
    ["Speaker names in subtitles", "On", "Prefix SRT/VTT/burned subtitles with the speaker."],
    ["Always timestamp TXT", "Off", "Timestamp TXT even without speakers."],
    ["Burned-in subtitle style", "Segoe UI 22, bold, white, outline", "Font, colours, style, position, quality, GPU encoder."],
    ["Pause that ends a line", "600 ms", "Live: silence needed before a line is finished."],
    ["Longest line", "15 s", "Live: forces a break during long monologues."],
    ["Same-person threshold", "0.45", "Live: similarity needed to count as someone already heard."],
    ["FFmpeg location", "auto", "Custom ffmpeg.exe path."],
    ["Library location", "%LOCALAPPDATA%\\WhisperDesk\\Library", "Where recordings are stored. Not synced to the cloud. Move library to… moves them safely."],
    ["Model downloads", "Ask", "Ask each time, Allow without asking, or Never (offline only; import models yourself)."],
    ["AI server on another computer", "Off", "Allows sending transcripts to an Ollama server that is not this PC (https:// for internet addresses)."],
    ["Lock after inactivity", "10 minutes", "With encryption on. Also: lock when Windows locks or sleeps (on), when minimised (off), clear copied text on lock (on)."],
], [52 * mm, 36 * mm, CONTENT_W - 88 * mm])
H2("Keyboard shortcuts")
TABLE([
    ["Shortcut", "Action"],
    ["Ctrl+O", "Add files"],
    ["Ctrl+,", "Settings"],
    ["Ctrl+L", "Lock WhisperDesk (with encryption on)"],
    ["Ctrl / Shift + click", "Select several transcript lines (then right-click → Assign)"],
], [45 * mm, CONTENT_W - 45 * mm], mono_first=True)

# =====================================================================
H1("13. Reports, voice analysis and backups")
H2("Reports on several recordings")
P("In the Library, Ctrl/Shift+click recordings and choose <b>Report…</b> (or Tools → Report on recordings). Tick what to "
  "include, add a reference/case number and purpose, pick formats and generate. <b>Combined</b> makes one document; "
  "<b>Separate</b> makes one per recording plus an index. With the local AI on, a consolidated report (timeline, themes, "
  "commitments, inconsistencies between recordings) is written by your Ollama model with an editable prompt. "
  "A <i>manifest.sha256.txt</i> lists the SHA-256 of every generated file for chain of custody.")
H2("Voice &amp; audio analysis")
BULLETS([
    "<b>Voice profiles</b>: median/mean pitch (F0) and range, pitch variability, jitter, shimmer, harmonics-to-noise ratio, "
    "formants F1–F3, spectral centroid, loudness, speaking rate (words/min, syllables/s) and voiced time.",
    "<b>Compare voices</b>: two voices from the recording or the voice library. The verdict compares how alike A and B are "
    "with how alike each voice is with itself, so the same phone/room doesn't make everyone look identical. "
    "“Consistent with the same speaker” between two detected speakers usually means one person was split in two.",
    "<b>Audio integrity</b>: SHA-256, container/stream metadata (codec, encoder, creation tags), clipping, DC offset, "
    "digital silence (exact zeros), sustained background-level steps (possible splices or device changes) and mains hum "
    "(ENF) presence, which a specialist can compare with grid-frequency records.",
])
H2("The forensics view (player)")
STEPS([
    "Open a recording in the player and click <b>∿ Forensics</b> (or right-click it in the Library → ∿ Forensics…). The audio is "
    "read and fingerprinted (SHA-256) and an <i>Opened</i> entry is written to the audit log.",
    "Zoom with the mouse wheel and drag the bottom strip to move around. Drag across the display to select a stretch; "
    "use ▶ Selection (and Loop) to listen.",
    "<b>Measure</b> the selection, ask <b>Who is speaking?</b>, or <b>Add note…</b> to record what you hear. Run <b>Integrity check</b> "
    "for whole-file findings. Results build up in the Results box; every step is logged.",
    "Click <b>Verify chain</b> at any time, and <b>Export report…</b> when done. Keep the .sha256.txt next to the report.",
])
P("The audit log lives in the recording's library folder (forensics-log.jsonl). It is append-only: entries cannot be edited "
  "in the app, and editing the file by hand is detected by Verify chain. Include it with any evidence you hand over.")
WARN("These are investigative indicators, not proof of identity or authenticity. For court use, have an accredited "
     "forensic examiner review the original recordings.")
H2("Backups")
P("File → <b>Backup…</b> writes one file with the library, voice library, settings and add-ons (models optional). With a "
  "password it is a <font name=\"Mono\">.wdbackup</font> encrypted with AES-256-GCM (key from PBKDF2-SHA256, 600,000 "
  "iterations); without, a plain .zip. File → <b>Restore from backup…</b> merges recordings (existing ones are skipped "
  "unless you tick overwrite), merges voices, and verifies every file against its SHA-256. A forgotten password cannot "
  "be recovered.")
P("With encryption on, the library and voice library inside a backup are already encrypted (the backup also carries the "
  "encryption header, which holds no key). Restoring settings never brings back add-on approvals, the remote-AI permission "
  "or a non-local AI address, and restored add-ons stay off until you approve them.")
# =====================================================================
H1("14. Encryption, app lock and privacy controls")
P("Everything here is in <b>Settings → Security &amp; privacy</b> (also File → Security &amp; privacy…).")
H2("Turning on encryption")
STEPS([
    "Click <b>Turn on encryption…</b>, read what is and isn't covered, and choose a passphrase (at least 12 characters; "
    "four or five unrelated words work well). Tick the box confirming there is no other way back in.",
    "Keep the <b>recovery key</b> that appears (save, print or copy it somewhere separate from this PC) and type its last "
    "four characters to confirm.",
    "WhisperDesk converts your library: each file is encrypted, checked, and only then is the readable copy removed. "
    "If it is interrupted, run it again from the same page.",
])
P("What is encrypted: recordings (audio and video), transcripts and their text/SRT copies, edit history, summaries, "
  "metadata including titles, folder names, voiceprints, forensic audit logs, live recordings while they record, "
  "crash-recovery files, the network activity list, the vocabulary hint, and secrets. Algorithms: AES-256-GCM, with the key "
  "made from the passphrase by PBKDF2-SHA256 (600,000 rounds); all from Windows' own cryptography.")
P("Not encrypted: files you export, share or “Save as”, outputs the Files tab writes next to your audio, backups without a "
  "password, models, add-on programs, and the settings needed before unlocking (theme, library location). The page lists "
  "where your data can still be read.")
WARN("If you lose both the passphrase and the recovery key, the data can't be recovered by anyone, including darkcipher0x.")
H2("Locking")
P("WhisperDesk asks for the passphrase every time it starts. It locks after the inactivity time you choose (default 10 "
  "minutes), when Windows locks or sleeps, optionally when minimised, and with <b>Ctrl+L</b>. Locking hides every window, "
  "forgets the key, deletes decrypted working copies and clears text WhisperDesk copied. A live recording or transcription "
  "that is running keeps going and is saved; the key is forgotten when it finishes. After 5 wrong passphrases, each new try "
  "waits longer (30 seconds, doubling up to 15 minutes).")
H2("Two-factor sign-in and Windows Hello")
BULLETS([
    "<b>Authenticator app (TOTP):</b> after the passphrase, a 6-digit code from Microsoft/Google Authenticator, Aegis, 1Password, "
    "Bitwarden or similar. It protects the lock screen against someone who knows your passphrase but doesn't have your phone. "
    "It does not make the encryption itself stronger.",
    "<b>Windows Hello:</b> either <i>instead of</i> the passphrase (convenient; anyone with your Windows PIN can then open "
    "WhisperDesk on this PC), or <i>in addition to</i> it (the key is bound to this PC's security chip, so a copied library "
    "can't be opened elsewhere without the recovery key).",
    "<b>Recovery key:</b> opens WhisperDesk without the passphrase, codes or Windows Hello, then asks for a new passphrase. "
    "Make a new one (the old one stops working) from the same page.",
])
H2("Network")
P("WhisperDesk connects to nothing when it starts and has no telemetry, analytics, crash reporting or update checks. "
  "<b>Model downloads</b> are set to <i>Ask</i>: a dialog says which model, from which server and how big, before anything "
  "is fetched. <b>Network activity…</b> lists every connection with the feature that made it (no transcript content is "
  "listed). Summaries only go to an AI server on another computer if you allow it under Summary &amp; AI.")
H2("Cloud-synced folders")
P("If the library is in a folder that OneDrive, Dropbox, Google Drive, iCloud Drive, Box or another sync service uploads, "
  "WhisperDesk warns you (it checks the sync folders Windows has registered and well-known folder names) but doesn't stop "
  "you. That folder is outside WhisperDesk's local-only boundary. With encryption on, the service only receives encrypted "
  "files, but it keeps a copy and sees how many files there are, their sizes and when they change.")
H2("What WhisperDesk can't protect against")
BULLETS([
    "Malware running under your Windows account while WhisperDesk is unlocked.",
    "Someone with both your passphrase and your recovery key, or a weak passphrase guessed from a stolen copy.",
    "Files you export or share, and services you connect (add-ons with network access, webhooks, a remote AI server).",
    "Weaknesses in Windows, drivers, FFmpeg, whisper.cpp or ONNX Runtime. Keep them updated, and use BitLocker for the "
    "system drive (it also covers the page file and hibernation file).",
])

H1("15. Administration")
H2("Silent installation and upgrades")
CODE("msiexec /i WhisperDesk-1.4.0-x64.msi /qn\n"
     "msiexec /i WhisperDesk-1.4.0-x64.msi /qn INSTALLFOLDER=\"D:\\Apps\\WhisperDesk\" INSTALLFFMPEG=1 INSTALLOLLAMA=0\n"
     "msiexec /i WhisperDesk-1.4.0-x64.msi /qn LIBRARYPATH=\"D:\\Recordings\" MODELSPATH=\"D:\\Models\"\n"
     "msiexec /x WhisperDesk-1.4.0-x64.msi /qn          (uninstall, keeps all data)\n"
     "msiexec /x WhisperDesk-1.4.0-x64.msi /qn REMOVE_SETTINGS=1 REMOVE_MODELS=1")
P("The MSI is per-machine (administrator rights), installs to C:\\Program Files\\DarkCipher Systems\\WhisperDesk and adds Start-menu shortcuts. Running it "
  "again offers <b>Repair</b> or <b>Remove</b>; a newer MSI upgrades in place; an older one is blocked. On Remove you can "
  "tick which of your data to delete as well; nothing is deleted otherwise.")
H2("Installer options")
TABLE([
    ["Property", "Meaning"],
    ["INSTALLFFMPEG=1", "Download and install FFmpeg (Gyan.FFmpeg) with winget if it isn't found (default off: nothing is downloaded unless ticked)."],
    ["INSTALLOLLAMA=1", "Install Ollama with winget for local AI summaries (default off)."],
    ["FFMPEGPATH", "Full path to an ffmpeg.exe to use."],
    ["LIBRARYPATH / MODELSPATH", "Default library and models folders (users can still change them in Settings)."],
    ["REMOVE_LIBRARY / REMOVE_VOICES / REMOVE_SETTINGS / REMOVE_MODELS", "On uninstall, also delete that data for the "
     "user running the uninstall (=1). Only recording folders are removed from the library."],
], [62 * mm, CONTENT_W - 62 * mm], mono_first=True)
H2("Identifiers")
TABLE([
    ["Item", "Value"],
    ["Product / Manufacturer", "WhisperDesk / darkcipher0x"],
    ["MSI UpgradeCode", "3fc82978-8263-42a1-9d62-8b858d3c0a9b"],
    ["AppUserModelID", "darkcipher0x.WhisperDesk"],
    ["Assembly GUID", "d06d3e32-6317-4cc3-a20f-2645b1c78f11"],
    ["Install folder", "C:\\Program Files\\DarkCipher Systems\\WhisperDesk"],
    ["Install registry key", "HKLM\\Software\\darkcipher0x\\WhisperDesk (InstallDir)"],
], [45 * mm, CONTENT_W - 45 * mm])
H2("File locations")
TABLE([
    ["Path", "Contents"],
    ["%LOCALAPPDATA%\\WhisperDesk\\settings.json", "Settings (with encryption on, the vocabulary hint and folder names are stored encrypted inside it)."],
    ["%LOCALAPPDATA%\\WhisperDesk\\vault.json", "Encryption header: salts, settings and wrapped keys, never a key itself. A copy is kept in the library folder."],
    ["%LOCALAPPDATA%\\WhisperDesk\\work", "Temporary working files (decrypted audio being played, shares, opened sessions). Deleted on lock, exit and start."],
    ["%LOCALAPPDATA%\\WhisperDesk\\network-log.json", "Network activity list (encrypted with encryption on)."],
    ["%LOCALAPPDATA%\\WhisperDesk\\models", "Whisper models; speakers\\ holds the speaker/VAD models."],
    ["%LOCALAPPDATA%\\WhisperDesk\\voices.json", "Voice library (names, sex, voiceprints)."],
    ["%LOCALAPPDATA%\\WhisperDesk\\addons", "One folder per add-on (DLL + addon.json); data\\ holds each add-on's own files."],
    ["%LOCALAPPDATA%\\WhisperDesk\\live", "Temporary live recordings until saved or discarded."],
    ["%LOCALAPPDATA%\\WhisperDesk\\Library", "Recordings library (configurable). Before 1.5.2 the default was Documents\\WhisperDesk Library; existing libraries stay where they are."],
], [70 * mm, CONTENT_W - 70 * mm], mono_first=True)
H2("Privacy and security")
BULLETS([
    "<b>Local by design.</b> Audio, transcripts, voiceprints, summaries and settings stay on the PC. There is no account, "
    "cloud service, telemetry, analytics or crash reporting.",
    "<b>Network use</b> is limited to: downloading models you pick (huggingface.co, github.com; asks first by default, pinned "
    "versions checked with SHA-256); the Ollama server set in Settings → Summary &amp; AI (default http://localhost:11434, i.e. "
    "this PC; another computer only if you allow it); add-ons you enable and allow network access; the Windows share sheet when "
    "you press Share. Nothing connects at start-up, and Settings → Security &amp; privacy → Network activity lists every connection.",
    "<b>Cloud-synced folders.</b> If the library is inside OneDrive, Dropbox, Google Drive or iCloud, that service uploads it. "
    "Windows often puts Documents inside OneDrive. WhisperDesk shows a warning banner at start-up and in Settings → Storage &amp; tools, "
    "with <b>Move library to a local folder</b>: every file is copied, checked (SHA-256), and only then removed from the synced folder.",
    "<b>Voiceprints are biometric data</b> (e.g. GDPR Article 9, Illinois BIPA). Get consent, keep only what you need, and "
    "delete voices with Tools → Voice library → Forget. Voiceprints are never included in shared files or reports.",
    "<b>Recording law</b> differs by country/state (one-party vs all-party consent). Tell people when you record.",
    "<b>At rest:</b> turn on encryption (chapter 14) so recordings, transcripts and voiceprints are AES-256 encrypted with a key "
    "from your passphrase. Use BitLocker for the system drive as well, and password-protected backups for copies stored elsewhere.",
    "<b>Integrity:</b> text edits are audited (who, when, why, before/after); reports and analysis exports carry SHA-256 "
    "fingerprints; backups are verified on restore.",
    "<b>Add-ons</b> get only the permissions you approve; third-party ones run in a Windows sandbox. One you allow network "
    "access can send what it receives. The built-in caption server listens on localhost only, is off by default, and other "
    "websites can't read it.",
    "<b>Logs</b> never contain transcript text, passphrases or keys; file names and folders in them are replaced by placeholders, "
    "and logs are deleted after 14 days.",
    "<b>Releases</b> come with a SHA256SUMS file; build.ps1 refuses to build with a known-vulnerable or unlocked dependency.",
    "Help → <b>Privacy &amp; security</b> shows this statement inside the app with your actual folder locations.",
])
H2("Backup and moving to another PC")
P("Use File → Backup… (optionally encrypted), copy the file to the new PC and use File → Restore from backup…. "
  "Models can be left out — they download again.")
H2("Writing add-ons (developers)")
P("Create a .NET 8 class library that references <font name=\"Mono\">sdk\\WhisperDesk.Addons.dll</font> (Private=false) "
  "and implements <font name=\"Mono\">IWhisperDeskAddon</font>. Put the DLL with an <font name=\"Mono\">addon.json</font> "
  "(id, name, author, entry, capabilities) in its own folder under the add-ons folder and restart. "
  "Events: <font name=\"Mono\">LineAdded</font>, <font name=\"Mono\">SessionStarted/Stopped</font>, "
  "<font name=\"Mono\">TranscriptSaved</font>. Full guide: <font name=\"Mono\">sdk\\README.md</font> in the install folder.")
CODE("public sealed class MyAddon : IWhisperDeskAddon\n"
     "{\n"
     "    public string Id =&gt; \"yourname.my-addon\";\n"
     "    public string Name =&gt; \"My add-on\";  /* Description, Author, Options … */\n"
     "    public void Start(IAddonHost host) =&gt; host.LineAdded += (s, e) =&gt; host.Log($\"{e.Speaker}: {e.Text}\");\n"
     "    public void Stop() { }\n"
     "}")
H2("Building from source")
CODE("cd WhisperDesk\n.\\build.ps1        # → dist\\WhisperDesk-&lt;version&gt;-x64.msi and portable zip")
P("Change <font name=\"Mono\">&lt;Version&gt;</font> in <font name=\"Mono\">WhisperDesk.csproj</font> for a new release. "
  "Regenerate the logo with <font name=\"Mono\">tools\\make-logo.ps1</font> and this guide with "
  "<font name=\"Mono\">docs\\make_guide.py</font>.")

# =====================================================================
H1("16. Troubleshooting")
TABLE([
    ["Problem", "Solution"],
    ["“FFmpeg was not found”", "winget install Gyan.FFmpeg, or set the path in Settings → Tools &amp; storage."],
    ["Forgot the passphrase", "On the lock screen choose <b>Use the recovery key…</b>, then set a new passphrase. Without the recovery key the data can't be opened."],
    ["Lost the phone with the authenticator app", "Use the recovery key, then turn two-factor off and set it up again."],
    ["“The download wasn't allowed”", "Model downloads are set to Never, or you said no. Change it in Settings → Security &amp; privacy, or import the model."],
    ["An add-on stays off", "It needs approving (again, if its DLL changed), or it isn't allowed to start by itself: switch it on in Tools → Add-ons."],
    ["Engine badge says cpu (no vulkan gpu)", "Update the graphics driver (AMD Adrenalin / NVIDIA / Intel). Check Device is GPU."],
    ["Slow transcription", "Use Large-v3 Turbo or a smaller model; make sure GPU is selected; close other GPU-heavy apps."],
    ["Wrong language or odd words", "Set Language explicitly; add names/terms to the Vocabulary hint."],
    ["Same sentence repeated", "Settings → tick “Don't use previous text as context”."],
    ["Two people merged into one speaker", "Set the exact speaker count, or lower Separation sensitivity (e.g. 0.4)."],
    ["One person split into several", "Raise Separation sensitivity (e.g. 0.6); merge cards by reassigning lines."],
    ["Live: nothing appears", "Check the Level meter moves; choose the right input; for Voicemeeter enable the B-bus on the strip."],
    ["Live: lines cut mid-sentence", "Raise “Pause that ends a line” (e.g. 900 ms)."],
    ["Known voice not recognised", "Add more samples (Voice library → Choose audio…) or lower Voice library match."],
    ["Play (" + PLAY + ") says no audio", "The recording was saved transcript-only, or the linked original file was moved."],
    ["My voice is not recognised", "Make sure it is in File → Voice library. Name yourself and save with “Remember this voice”, or pick your name from the Name dropdown and save to add another sample."],
    ["OBS overlay is blank", "Enable the caption add-on; check the URL and port; start a live session."],
    ["SmartScreen warning", "More info → Run anyway (unsigned installer)."],
], [55 * mm, CONTENT_W - 55 * mm])

# =====================================================================
H1("17. Credits and licences")
P("<b>WhisperDesk is open-source software under the MIT License</b> — free to use, study, change and share, provided "
  "the copyright and licence notice are kept. It comes without warranty. The full licence text is in "
  "<font name=\"Mono\">LICENSE.txt</font> in the install folder and under Help → About → Licence.")
P("WhisperDesk © 2026 darkcipher0x. Built with whisper.cpp / ggml and Whisper.net (MIT), OpenAI Whisper models (MIT), "
  "sherpa-onnx (Apache 2.0) with pyannote segmentation and WeSpeaker models, Silero VAD (MIT), NAudio (MIT), "
  "PDFsharp/MigraDoc (MIT) and the WiX Toolset (MS-RL, build only). FFmpeg is not included and is used from your "
  "system installation.")

doc = GuideDoc(OUT)
doc.multiBuild(story)
print("wrote", OUT)
