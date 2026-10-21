using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Windows.Media;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;

namespace WhisperDesk.Services;

/// <summary>Exports a transcript as a Word document (.docx) or PDF: title, speaker summary, then one line per timestamp.</summary>
public static class DocumentExporter
{
    internal static string Brand => "WhisperDesk by darkcipher0x";

    private static string Hex(SpeakerInfo s) =>
        s.Color is SolidColorBrush b ? $"{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : "36D2CB";

    // Saturated speaker colours are too light on white paper: darken them for print.
    internal static string PrintHex(SpeakerInfo s)
    {
        var c = s.Color is SolidColorBrush b ? b.Color : System.Windows.Media.Colors.Teal;
        return $"{(int)(c.R * 0.62):X2}{(int)(c.G * 0.62):X2}{(int)(c.B * 0.62):X2}";
    }

    private static IEnumerable<TranscriptLine> Spoken(TranscriptResult r) => r.Lines.Where(l => l.Text.Trim().Length > 0);

    private static string SpeakerSummary(TranscriptResult r) =>
        string.Join(",  ", r.Speakers.Select(s => $"{s.Tag} – {s.TalkTime:mm\\:ss}"));

    // ---------------- Word (.docx, written directly as Office Open XML) ----------------

    internal static string X(string s) => SecurityElement.Escape(s) ?? "";
    internal static string Run(string text, bool bold = false, string? color = null, int halfPoints = 21, string? font = null) =>
        "<w:r><w:rPr>" +
        (font != null ? $"<w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\"/>" : "") +
        (bold ? "<w:b/>" : "") +
        (color != null ? $"<w:color w:val=\"{color}\"/>" : "") +
        $"<w:sz w:val=\"{halfPoints}\"/></w:rPr><w:t xml:space=\"preserve\">{X(text)}</w:t></w:r>";
    internal static string Para(string runs, int after = 80) => $"<w:p><w:pPr><w:spacing w:after=\"{after}\"/></w:pPr>{runs}</w:p>";

    public static void WriteDocx(TranscriptResult r, string title, string path)
    {
        var body = new StringBuilder();
        body.Append(Para(Run(title, bold: true, halfPoints: 36), 60));
        body.Append(Para(Run($"{DateTime.Now:yyyy-MM-dd HH:mm} · {Path.GetFileName(r.SourceFile)} · {Brand}", color: "6B7C7B", halfPoints: 18), 200));
        if (r.HasSpeakers)
            body.Append(Para(Run("Speakers: ", bold: true, halfPoints: 20) + Run(SpeakerSummary(r), halfPoints: 20), 240));

        foreach (var l in Spoken(r))
        {
            var runs = Run($"[{l.Time}]  ", color: "8A9A99", halfPoints: 18, font: "Consolas");
            if (l.Speaker is { } s) runs += Run(s.Tag + ": ", bold: true, color: PrintHex(s));
            runs += Run(l.Text.Trim());
            if (l.IsEdited) runs += Run("  [edited]", bold: true, color: "B26A00", halfPoints: 16);
            body.Append(Para(runs));
        }

        // Edit log appendix: every correction with who / when / why.
        var edits = EditRows(r);
        if (edits.Count > 0)
        {
            body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
            body.Append(Para(Run("Edit log", bold: true, halfPoints: 30), 60));
            body.Append(Para(Run($"{edits.Count} correction(s). Each was confirmed with a reason; times include the time zone.", color: "6B7C7B", halfPoints: 18), 160));
            foreach (var e in edits)
            {
                body.Append(Para(Run($"{e.Edit.AtText}  ·  {e.Edit.User}  ·  line [{e.Line.Time}]" +
                                     (e.Line.Speaker != null ? $"  ·  {e.Line.Speaker.Label}" : ""), bold: true, halfPoints: 19), 20));
                body.Append(Para(Run("Reason: ", bold: true, halfPoints: 19) + Run(e.Edit.Reason, halfPoints: 19), 20));
                body.Append(Para(Run("Before: ", bold: true, color: "6B7C7B", halfPoints: 19) + Run(e.Edit.OldText, color: "6B7C7B", halfPoints: 19), 20));
                body.Append(Para(Run("After: ", bold: true, halfPoints: 19) + Run(e.Edit.NewText, halfPoints: 19), 180));
            }
        }

        SaveDocx(title, body.ToString(), path);
    }

    internal static void SaveDocx(string title, string body, string path)
    {
        var document =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            body +
            "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"567\" w:footer=\"567\" w:gutter=\"0\"/></w:sectPr>" +
            "</w:body></w:document>";
        var styles =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docDefaults><w:rPrDefault><w:rPr>" +
            "<w:rFonts w:ascii=\"Segoe UI\" w:hAnsi=\"Segoe UI\" w:cs=\"Segoe UI\"/><w:sz w:val=\"21\"/><w:lang w:val=\"en-GB\"/></w:rPr></w:rPrDefault>" +
            "<w:pPrDefault><w:pPr><w:spacing w:after=\"80\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults></w:styles>";
        var contentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "</Types>";
        var rels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "</Relationships>";
        var docRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";
        var core =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dc:title>{X(title)}</dc:title><dc:creator>{Brand}</dc:creator>" +
            $"<dcterms:created xsi:type=\"dcterms:W3CDTF\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</dcterms:created></cp:coreProperties>";

        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add("[Content_Types].xml", contentTypes);
        Add("_rels/.rels", rels);
        Add("word/document.xml", document);
        Add("word/styles.xml", styles);
        Add("word/_rels/document.xml.rels", docRels);
        Add("docProps/core.xml", core);
    }

    // ---------------- Call summary (Word / PDF) ----------------

    public static void WriteSummaryDocx(CallSummary s, string path)
    {
        var body = new StringBuilder();
        body.Append(Para(Run("Summary · " + s.Title, bold: true, halfPoints: 34), 60));
        body.Append(Para(Run($"{s.Date} · {s.OverviewText} · {Brand}", color: "6B7C7B", halfPoints: 18), 200));
        foreach (var (isHeading, text) in s.ReportBlocks())
            body.Append(isHeading ? Para(Run(text, bold: true, color: "147A76", halfPoints: 26), 60) : Para(Run(text), 120));
        body.Append(Para(Run($"Report: {s.ReportBy}", color: "6B7C7B", halfPoints: 16), 240));
        if (s.Speakers.Count > 0)
        {
            body.Append(Para(Run("Who spoke", bold: true, color: "147A76", halfPoints: 26), 60));
            foreach (var sp in s.Speakers)
                body.Append(Para(Run("•  " + sp.Name + (sp.Sex != Sexes.Unspecified ? $" ({sp.Sex})" : "") + ": ", bold: true) +
                                 Run($"{sp.Talk:mm\\:ss} ({sp.Share:P0}) · {sp.Turns} turns · {sp.Words} words"), 40));
            body.Append(Para("", 120));
        }
        foreach (var (heading, items, empty) in s.Sections())
        {
            body.Append(Para(Run(heading, bold: true, color: "147A76", halfPoints: 26), 60));
            if (items.Count == 0) body.Append(Para(Run(empty, color: "6B7C7B"), 120));
            foreach (var i in items)
                body.Append(Para(Run($"[{i.Time}]  ", color: "8A9A99", halfPoints: 18, font: "Consolas") +
                                 (i.Speaker != null ? Run(i.Speaker + ": ", bold: true) : "") + Run(i.Text), 50));
            body.Append(Para("", 120));
        }
        body.Append(Para(Run("How this summary was made", bold: true, color: "147A76", halfPoints: 22), 60));
        foreach (var (label, value) in s.Provenance)
            body.Append(Para(Run(label + ": ", bold: true, halfPoints: 17) + Run(value, color: "4E5E5D", halfPoints: 17), 20));
        body.Append(Para("", 80));
        body.Append(Para(Run("Items are detected and written automatically — check them against the transcript.",
                             color: "6B7C7B", halfPoints: 16)));
        SaveDocx("Summary · " + s.Title, body.ToString(), path);
    }

    public static void WriteSummaryPdf(CallSummary s, string path)
    {
        var doc = new Document();
        doc.Info.Title = "Summary · " + s.Title;
        doc.Info.Author = Brand;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Segoe UI";
        normal.Font.Size = 10;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(3);
        var muted = new MigraDoc.DocumentObjectModel.Color(0x6B, 0x7C, 0x7B);
        var teal = new MigraDoc.DocumentObjectModel.Color(0x14, 0x7A, 0x76);

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);
        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = muted;
        footer.AddText($"{Brand}   ·   summary ({s.ReportBy})   ·   page ");
        footer.AddPageField();

        var t = section.AddParagraph("Summary · " + s.Title);
        t.Format.Font.Size = 17;
        t.Format.Font.Bold = true;
        var m = section.AddParagraph($"{s.Date} · {s.OverviewText}");
        m.Format.Font.Size = 9;
        m.Format.Font.Color = muted;
        m.Format.SpaceAfter = Unit.FromPoint(12);

        void Heading(string text)
        {
            var h = section.AddParagraph(text);
            h.Format.Font.Size = 12.5;
            h.Format.Font.Bold = true;
            h.Format.Font.Color = teal;
            h.Format.SpaceBefore = Unit.FromPoint(8);
            h.Format.SpaceAfter = Unit.FromPoint(4);
            h.Format.KeepWithNext = true;
        }

        foreach (var (isHeading, text) in s.ReportBlocks())
        {
            if (isHeading) { Heading(text); continue; }
            var rp = section.AddParagraph(text);
            rp.Format.SpaceAfter = Unit.FromPoint(6);
            rp.Format.Alignment = ParagraphAlignment.Justify;
        }
        var by = section.AddParagraph($"Report: {s.ReportBy}");
        by.Format.Font.Size = 8;
        by.Format.Font.Color = muted;
        by.Format.SpaceAfter = Unit.FromPoint(10);

        if (s.Speakers.Count > 0)
        {
            Heading("Who spoke");
            foreach (var sp in s.Speakers)
            {
                var p = section.AddParagraph();
                p.AddFormattedText("•  " + sp.Name + (sp.Sex != Sexes.Unspecified ? $" ({sp.Sex})" : "") + ": ", TextFormat.Bold);
                p.AddText($"{sp.Talk:mm\\:ss} ({sp.Share:P0}) · {sp.Turns} turns · {sp.Words} words");
            }
        }
        foreach (var (heading, items, empty) in s.Sections())
        {
            Heading(heading);
            if (items.Count == 0) { var e = section.AddParagraph(empty); e.Format.Font.Color = muted; }
            foreach (var i in items)
            {
                var p = section.AddParagraph();
                p.Format.LeftIndent = Unit.FromCentimeter(1.8);
                p.Format.FirstLineIndent = Unit.FromCentimeter(-1.8);
                var tm = p.AddFormattedText($"[{i.Time}]  ");
                tm.Font.Name = "Consolas";
                tm.Font.Size = 8.5;
                tm.Font.Color = muted;
                if (i.Speaker != null) p.AddFormattedText(i.Speaker + ": ", TextFormat.Bold);
                p.AddText(i.Text);
            }
        }
        Heading("How this summary was made");
        foreach (var (label, value) in s.Provenance)
        {
            var pp = section.AddParagraph();
            pp.Format.Font.Size = 8.5;
            pp.Format.SpaceAfter = Unit.FromPoint(1);
            pp.AddFormattedText(label + ": ", TextFormat.Bold);
            pp.AddFormattedText(value).Font.Color = muted;
        }
        var note = section.AddParagraph("Items are detected and written automatically — check them against the transcript.");
        note.Format.Font.Size = 8;
        note.Format.Font.Color = muted;
        note.Format.SpaceBefore = Unit.FromPoint(10);

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }

    private static List<(TranscriptLine Line, LineEdit Edit)> EditRows(TranscriptResult r) =>
        r.Lines.SelectMany(l => l.Edits.Select(e => (l, e))).OrderBy(x => x.e.At).ToList();

    private static void AddLabelled(Section section, string label, string text, bool muted)
    {
        var p = section.AddParagraph();
        p.Format.LeftIndent = Unit.FromCentimeter(0.4);
        p.Format.SpaceAfter = Unit.FromPoint(1);
        p.AddFormattedText(label, TextFormat.Bold);
        var t = p.AddFormattedText(text);
        if (muted) t.Font.Color = new MigraDoc.DocumentObjectModel.Color(0x6B, 0x7C, 0x7B);
    }

    // ---------------- PDF (MigraDoc / PDFsharp) ----------------

    public static void WritePdf(TranscriptResult r, string title, string path)
    {
        var doc = new Document();
        doc.Info.Title = title;
        doc.Info.Author = Brand;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Segoe UI";
        normal.Font.Size = 10;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(4);

        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);
        section.PageSetup.TopMargin = Unit.FromCentimeter(2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.2);

        var footer = section.Footers.Primary.AddParagraph();
        footer.Format.Font.Size = 8;
        footer.Format.Font.Color = new MigraDoc.DocumentObjectModel.Color(0x6B, 0x7C, 0x7B);
        footer.AddText($"{Brand}   ·   page ");
        footer.AddPageField();
        footer.AddText(" of ");
        footer.AddNumPagesField();

        var t = section.AddParagraph(title);
        t.Format.Font.Size = 18;
        t.Format.Font.Bold = true;
        t.Format.SpaceAfter = Unit.FromPoint(2);

        var meta = section.AddParagraph($"{DateTime.Now:yyyy-MM-dd HH:mm} · {Path.GetFileName(r.SourceFile)}");
        meta.Format.Font.Size = 9;
        meta.Format.Font.Color = new MigraDoc.DocumentObjectModel.Color(0x6B, 0x7C, 0x7B);
        meta.Format.SpaceAfter = Unit.FromPoint(10);

        if (r.HasSpeakers)
        {
            var sp = section.AddParagraph();
            sp.AddFormattedText("Speakers: ", TextFormat.Bold);
            sp.AddText(SpeakerSummary(r));
            sp.Format.SpaceAfter = Unit.FromPoint(12);
        }

        foreach (var l in Spoken(r))
        {
            var p = section.AddParagraph();
            p.Format.LeftIndent = Unit.FromCentimeter(1.8);
            p.Format.FirstLineIndent = Unit.FromCentimeter(-1.8);
            var time = p.AddFormattedText($"[{l.Time}]  ");
            time.Font.Name = "Consolas";
            time.Font.Size = 8.5;
            time.Font.Color = new MigraDoc.DocumentObjectModel.Color(0x8A, 0x9A, 0x99);
            if (l.Speaker is { } s)
            {
                var hex = PrintHex(s);
                var who = p.AddFormattedText(s.Tag + ": ", TextFormat.Bold);
                who.Font.Color = new MigraDoc.DocumentObjectModel.Color(
                    Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
            }
            p.AddText(l.Text.Trim());
            if (l.IsEdited)
            {
                var tag = p.AddFormattedText("  [edited]", TextFormat.Bold);
                tag.Font.Size = 8;
                tag.Font.Color = new MigraDoc.DocumentObjectModel.Color(0xB2, 0x6A, 0x00);
            }
        }

        // Edit log appendix
        var edits = EditRows(r);
        if (edits.Count > 0)
        {
            var logSection = doc.AddSection();
            logSection.PageSetup = section.PageSetup.Clone();
            var lf = logSection.Footers.Primary.AddParagraph();
            lf.Format.Font.Size = 8;
            lf.Format.Font.Color = new MigraDoc.DocumentObjectModel.Color(0x6B, 0x7C, 0x7B);
            lf.AddText($"{Brand}   ·   edit log   ·   page ");
            lf.AddPageField();
            lf.AddText(" of ");
            lf.AddNumPagesField();

            var h = logSection.AddParagraph("Edit log");
            h.Format.Font.Size = 16;
            h.Format.Font.Bold = true;
            var sub = logSection.AddParagraph($"{edits.Count} correction(s). Each was confirmed with a reason; times include the time zone.");
            sub.Format.Font.Size = 9;
            sub.Format.Font.Color = new MigraDoc.DocumentObjectModel.Color(0x6B, 0x7C, 0x7B);
            sub.Format.SpaceAfter = Unit.FromPoint(10);

            foreach (var e in edits)
            {
                var head = logSection.AddParagraph();
                head.Format.SpaceBefore = Unit.FromPoint(6);
                head.Format.KeepWithNext = true;
                head.AddFormattedText($"{e.Edit.AtText}  ·  {e.Edit.User}  ·  line [{e.Line.Time}]" +
                                      (e.Line.Speaker != null ? $"  ·  {e.Line.Speaker.Label}" : ""), TextFormat.Bold);
                AddLabelled(logSection, "Reason: ", e.Edit.Reason, false);
                AddLabelled(logSection, "Before: ", e.Edit.OldText, true);
                AddLabelled(logSection, "After: ", e.Edit.NewText, false);
            }
        }

        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }
}
