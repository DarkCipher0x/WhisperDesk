using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace WhisperDesk.Services;

/// <summary>
/// A simple, format-neutral document (headings, paragraphs, key/value rows, tables, transcript lines) that can be
/// written as PDF, Word, plain text, HTML or JSON. Used for multi-recording reports and forensic voice reports.
/// </summary>
public sealed class ReportDocument
{
    public abstract record Block;
    public sealed record Heading(string Text, int Level) : Block;
    public sealed record Paragraph(string Text, bool Muted = false, bool Bold = false) : Block;
    public sealed record Fields(List<(string Label, string Value)> Rows) : Block;
    public sealed record Table(string[] Header, List<string[]> Rows, double[]? Widths = null) : Block;
    public sealed record Line(string Time, string? Speaker, string? SpeakerHex, string Text, bool Edited) : Block;
    public sealed record PageBreak : Block;
    /// <summary>A PNG embedded in PDF/HTML (Word/text reference the file).</summary>
    public sealed record Picture(string Path, string Caption) : Block;

    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Footer { get; set; } = DocumentExporter.Brand;
    public List<Block> Blocks { get; } = new();

    public ReportDocument H1(string t) { Blocks.Add(new Heading(t, 1)); return this; }
    public ReportDocument H2(string t) { Blocks.Add(new Heading(t, 2)); return this; }
    public ReportDocument P(string t, bool muted = false, bool bold = false) { Blocks.Add(new Paragraph(t, muted, bold)); return this; }
    public ReportDocument KV(IEnumerable<(string, string)> rows) { Blocks.Add(new Fields(rows.ToList())); return this; }
    public ReportDocument T(string[] header, IEnumerable<string[]> rows, double[]? widths = null) { Blocks.Add(new Table(header, rows.ToList(), widths)); return this; }
    public ReportDocument L(string time, string? speaker, string? hex, string text, bool edited) { Blocks.Add(new Line(time, speaker, hex, text, edited)); return this; }
    public ReportDocument Break() { Blocks.Add(new PageBreak()); return this; }
    public ReportDocument Img(string path, string caption) { Blocks.Add(new Picture(path, caption)); return this; }

    /// <summary>Adds the report text of a summary ("## heading" lines become headings).</summary>
    public ReportDocument Markdown(string text, int level = 2)
    {
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().Replace("**", "");
            if (line.Length == 0) continue;
            if (line.StartsWith('#')) Blocks.Add(new Heading(line.TrimStart('#', ' '), level));
            else Blocks.Add(new Paragraph(line.StartsWith("* ") || line.StartsWith("- ") ? "•  " + line[2..] : line));
        }
        return this;
    }

    // ---------------- writers ----------------

    public void Save(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".pdf": WritePdf(path); break;
            case ".docx": WriteDocx(path); break;
            case ".html": File.WriteAllText(path, ToHtml(), new UTF8Encoding(false)); break;
            case ".json": File.WriteAllText(path, ToJson(), new UTF8Encoding(false)); break;
            default: File.WriteAllText(path, ToText(), new UTF8Encoding(false)); break;
        }
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title.ToUpperInvariant());
        if (Subtitle.Length > 0) sb.AppendLine(Subtitle);
        sb.AppendLine();
        foreach (var b in Blocks)
        {
            switch (b)
            {
                case Heading h: sb.AppendLine().AppendLine(h.Level == 1 ? h.Text.ToUpperInvariant() : h.Text).AppendLine(new string(h.Level == 1 ? '=' : '-', Math.Min(80, h.Text.Length))); break;
                case Paragraph p: sb.AppendLine(p.Text); break;
                case Fields f: foreach (var (l, v) in f.Rows) sb.AppendLine($"{l}: {v}"); sb.AppendLine(); break;
                case Table t:
                    sb.AppendLine(string.Join(" | ", t.Header));
                    foreach (var r in t.Rows) sb.AppendLine(string.Join(" | ", r));
                    sb.AppendLine();
                    break;
                case Line l: sb.AppendLine($"[{l.Time}] {(l.Speaker != null ? l.Speaker + ": " : "")}{l.Text}{(l.Edited ? " [edited]" : "")}"); break;
                case PageBreak: sb.AppendLine().AppendLine(new string('─', 60)).AppendLine(); break;
                case Picture pic: sb.AppendLine($"[image: {System.IO.Path.GetFileName(pic.Path)}] {pic.Caption}"); break;
            }
        }
        sb.AppendLine().AppendLine(Footer);
        return sb.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        title = Title, subtitle = Subtitle, footer = Footer,
        blocks = Blocks.Select(b => b switch
        {
            Heading h => (object)new { type = "heading", h.Level, h.Text },
            Paragraph p => new { type = "paragraph", p.Text },
            Fields f => new { type = "fields", rows = f.Rows.ToDictionary(r => r.Label, r => r.Value) },
            Table t => new { type = "table", header = t.Header, rows = t.Rows },
            Line l => new { type = "line", time = l.Time, speaker = l.Speaker, text = l.Text, edited = l.Edited },
            Picture pic => new { type = "image", file = System.IO.Path.GetFileName(pic.Path), caption = pic.Caption },
            _ => new { type = "break" },
        }),
    }, new JsonSerializerOptions { WriteIndented = true });

    public string ToHtml()
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder($$"""
            <!doctype html><html><head><meta charset="utf-8"><title>{{E(Title)}}</title><style>
            body{font:14px/1.55 'Segoe UI',sans-serif;color:#0b1a1a;max-width:900px;margin:32px auto;padding:0 24px}
            h1{font-size:24px;margin:0} h2{color:#147A76;font-size:18px;margin-top:28px;border-bottom:1px solid #cfdcdc;padding-bottom:4px}
            h3{color:#147A76;font-size:15px;margin-top:18px} .sub{color:#6b7c7b} .muted{color:#6b7c7b}
            table{border-collapse:collapse;width:100%;margin:8px 0 16px;font-size:13px} th,td{border:1px solid #cfdcdc;padding:4px 8px;text-align:left;vertical-align:top}
            th{background:#eef4f4} .t{font-family:Consolas,monospace;color:#8a9a99;font-size:12px} .ed{color:#b26a00;font-weight:600;font-size:11px}
            dl{display:grid;grid-template-columns:200px 1fr;gap:2px 12px} dt{font-weight:600} dd{margin:0}
            footer{margin-top:40px;color:#6b7c7b;font-size:12px} hr{border:0;border-top:1px dashed #cfdcdc;margin:28px 0}
            </style></head><body><h1>{{E(Title)}}</h1><div class="sub">{{E(Subtitle)}}</div>
            """);
        foreach (var b in Blocks)
        {
            switch (b)
            {
                case Heading h: sb.Append(h.Level == 1 ? "<h2>" : "<h3>").Append(E(h.Text)).Append(h.Level == 1 ? "</h2>" : "</h3>"); break;
                case Paragraph p: sb.Append(p.Muted ? "<p class=\"muted\">" : "<p>").Append(p.Bold ? "<b>" + E(p.Text) + "</b>" : E(p.Text)).Append("</p>"); break;
                case Fields f: sb.Append("<dl>"); foreach (var (l, v) in f.Rows) sb.Append($"<dt>{E(l)}</dt><dd>{E(v)}</dd>"); sb.Append("</dl>"); break;
                case Table t:
                    sb.Append("<table><tr>").Append(string.Concat(t.Header.Select(c => $"<th>{E(c)}</th>"))).Append("</tr>");
                    foreach (var r in t.Rows) sb.Append("<tr>").Append(string.Concat(r.Select(c => $"<td>{E(c)}</td>"))).Append("</tr>");
                    sb.Append("</table>");
                    break;
                case Line l:
                    sb.Append($"<div><span class=\"t\">[{E(l.Time)}]</span> ");
                    if (l.Speaker != null) sb.Append($"<b style=\"color:#{l.SpeakerHex ?? "147A76"}\">{E(l.Speaker)}:</b> ");
                    sb.Append(E(l.Text)).Append(l.Edited ? " <span class=\"ed\">[edited]</span>" : "").Append("</div>");
                    break;
                case PageBreak: sb.Append("<hr>"); break;
                case Picture pic when File.Exists(pic.Path):
                    sb.Append($"<figure style=\"margin:10px 0\"><img style=\"max-width:100%;border:1px solid #cfdcdc\" src=\"data:image/png;base64,{Convert.ToBase64String(File.ReadAllBytes(pic.Path))}\"><figcaption class=\"muted\">{E(pic.Caption)}</figcaption></figure>");
                    break;
            }
        }
        return sb.Append($"<footer>{E(Footer)}</footer></body></html>").ToString();
    }

    private void WriteDocx(string path)
    {
        string Run(string t, bool bold = false, string? color = null, int hp = 21, string? font = null) => DocumentExporter.Run(t, bold, color, hp, font);
        string Para(string runs, int after = 80) => DocumentExporter.Para(runs, after);
        var body = new StringBuilder();
        body.Append(Para(Run(Title, bold: true, hp: 36), 60));
        if (Subtitle.Length > 0) body.Append(Para(Run(Subtitle, color: "6B7C7B", hp: 18), 200));
        foreach (var b in Blocks)
        {
            switch (b)
            {
                case Heading h: body.Append(Para(Run(h.Text, bold: true, color: "147A76", hp: h.Level == 1 ? 30 : 25), 80)); break;
                case Paragraph p: body.Append(Para(Run(p.Text, bold: p.Bold, color: p.Muted ? "6B7C7B" : null), 100)); break;
                case Fields f:
                    foreach (var (l, v) in f.Rows) body.Append(Para(Run(l + ": ", bold: true, hp: 19) + Run(v, hp: 19), 20));
                    body.Append(Para("", 100));
                    break;
                case Table t:
                    body.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders>" +
                                "<w:top w:val=\"single\" w:sz=\"4\" w:color=\"CFDCDC\"/><w:left w:val=\"single\" w:sz=\"4\" w:color=\"CFDCDC\"/>" +
                                "<w:bottom w:val=\"single\" w:sz=\"4\" w:color=\"CFDCDC\"/><w:right w:val=\"single\" w:sz=\"4\" w:color=\"CFDCDC\"/>" +
                                "<w:insideH w:val=\"single\" w:sz=\"4\" w:color=\"CFDCDC\"/><w:insideV w:val=\"single\" w:sz=\"4\" w:color=\"CFDCDC\"/></w:tblBorders></w:tblPr>");
                    void Row(string[] cells, bool head)
                    {
                        body.Append("<w:tr>");
                        foreach (var c in cells)
                            body.Append("<w:tc>" + (head ? "<w:tcPr><w:shd w:val=\"clear\" w:fill=\"EEF4F4\"/></w:tcPr>" : "") +
                                        Para(Run(c, bold: head, hp: 18), 0) + "</w:tc>");
                        body.Append("</w:tr>");
                    }
                    Row(t.Header, true);
                    foreach (var r in t.Rows) Row(r, false);
                    body.Append("</w:tbl>").Append(Para("", 120));
                    break;
                case Line l:
                    body.Append(Para(Run($"[{l.Time}]  ", color: "8A9A99", hp: 18, font: "Consolas") +
                                     (l.Speaker != null ? Run(l.Speaker + ": ", bold: true, color: l.SpeakerHex) : "") + Run(l.Text) +
                                     (l.Edited ? Run("  [edited]", bold: true, color: "B26A00", hp: 16) : "")));
                    break;
                case PageBreak: body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>"); break;
                case Picture pic: body.Append(Para(Run($"[Image: {System.IO.Path.GetFileName(pic.Path)} — saved next to this document] {pic.Caption}", color: "6B7C7B", hp: 18), 100)); break;
            }
        }
        body.Append(Para(Run(Footer, color: "6B7C7B", hp: 16), 0));
        DocumentExporter.SaveDocx(Title, body.ToString(), path);
    }

    private void WritePdf(string path)
    {
        var muted = new Color(0x6B, 0x7C, 0x7B);
        var teal = new Color(0x14, 0x7A, 0x76);
        var doc = new Document();
        doc.Info.Title = Title;
        doc.Info.Author = DocumentExporter.Brand;
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Segoe UI";
        normal.Font.Size = 9.5;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(3);

        Section NewSection()
        {
            var s = doc.AddSection();
            s.PageSetup.PageFormat = PageFormat.A4;
            s.PageSetup.LeftMargin = s.PageSetup.RightMargin = Unit.FromCentimeter(1.8);
            s.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
            s.PageSetup.BottomMargin = Unit.FromCentimeter(2.0);
            var f = s.Footers.Primary.AddParagraph();
            f.Format.Font.Size = 7.5;
            f.Format.Font.Color = muted;
            f.AddText($"{Footer}   ·   page ");
            f.AddPageField();
            f.AddText(" of ");
            f.AddNumPagesField();
            return s;
        }
        var section = NewSection();
        var t = section.AddParagraph(Title);
        t.Format.Font.Size = 18;
        t.Format.Font.Bold = true;
        if (Subtitle.Length > 0)
        {
            var st = section.AddParagraph(Subtitle);
            st.Format.Font.Size = 9;
            st.Format.Font.Color = muted;
            st.Format.SpaceAfter = Unit.FromPoint(10);
        }
        double usable = 21.0 - 3.6;   // A4 width minus margins (cm)

        foreach (var b in Blocks)
        {
            switch (b)
            {
                case Heading h:
                    var hp = section.AddParagraph(h.Text);
                    hp.Format.Font.Size = h.Level == 1 ? 14 : 11.5;
                    hp.Format.Font.Bold = true;
                    hp.Format.Font.Color = teal;
                    hp.Format.SpaceBefore = Unit.FromPoint(h.Level == 1 ? 12 : 8);
                    hp.Format.SpaceAfter = Unit.FromPoint(4);
                    hp.Format.KeepWithNext = true;
                    break;
                case Paragraph p:
                    var pp = section.AddParagraph();
                    var ft = pp.AddFormattedText(p.Text, p.Bold ? TextFormat.Bold : TextFormat.NotBold);
                    if (p.Muted) ft.Font.Color = muted;
                    pp.Format.SpaceAfter = Unit.FromPoint(5);
                    break;
                case Fields f:
                    var ftab = section.AddTable();
                    ftab.AddColumn(Unit.FromCentimeter(4.6));
                    ftab.AddColumn(Unit.FromCentimeter(usable - 4.6));
                    foreach (var (l, v) in f.Rows)
                    {
                        var r = ftab.AddRow();
                        r.Cells[0].AddParagraph().AddFormattedText(l, TextFormat.Bold);
                        r.Cells[1].AddParagraph(v);
                    }
                    section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
                    break;
                case Table tb:
                    var table = section.AddTable();
                    table.Borders.Color = new Color(0xCF, 0xDC, 0xDC);
                    table.Borders.Width = 0.5;
                    table.Format.Font.Size = 8.5;
                    var widths = tb.Widths ?? Enumerable.Repeat(1.0, tb.Header.Length).ToArray();
                    var sum = widths.Sum();
                    foreach (var w in widths) table.AddColumn(Unit.FromCentimeter(usable * w / sum));
                    var head = table.AddRow();
                    head.Shading.Color = new Color(0xEE, 0xF4, 0xF4);
                    head.HeadingFormat = true;
                    for (int i = 0; i < tb.Header.Length; i++) head.Cells[i].AddParagraph().AddFormattedText(tb.Header[i], TextFormat.Bold);
                    foreach (var row in tb.Rows)
                    {
                        var r = table.AddRow();
                        for (int i = 0; i < tb.Header.Length && i < row.Length; i++) r.Cells[i].AddParagraph(row[i]);
                    }
                    section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(6);
                    break;
                case Line l:
                    var lp = section.AddParagraph();
                    lp.Format.LeftIndent = Unit.FromCentimeter(1.8);
                    lp.Format.FirstLineIndent = Unit.FromCentimeter(-1.8);
                    var tm = lp.AddFormattedText($"[{l.Time}]  ");
                    tm.Font.Name = "Consolas";
                    tm.Font.Size = 8;
                    tm.Font.Color = new Color(0x8A, 0x9A, 0x99);
                    if (l.Speaker != null)
                    {
                        var who = lp.AddFormattedText(l.Speaker + ": ", TextFormat.Bold);
                        if (l.SpeakerHex is { Length: 6 } hex)
                            who.Font.Color = new Color(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));
                    }
                    lp.AddText(l.Text);
                    if (l.Edited)
                    {
                        var tag = lp.AddFormattedText("  [edited]", TextFormat.Bold);
                        tag.Font.Size = 7.5;
                        tag.Font.Color = new Color(0xB2, 0x6A, 0x00);
                    }
                    break;
                case PageBreak:
                    section = NewSection();
                    break;
                case Picture pic when File.Exists(pic.Path):
                    var img = section.AddImage(pic.Path);
                    img.LockAspectRatio = true;
                    img.Width = Unit.FromCentimeter(usable);
                    var cap = section.AddParagraph(pic.Caption);
                    cap.Format.Font.Size = 8;
                    cap.Format.Font.Color = muted;
                    cap.Format.SpaceAfter = Unit.FromPoint(8);
                    break;
            }
        }
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        renderer.PdfDocument.Save(path);
    }
}
