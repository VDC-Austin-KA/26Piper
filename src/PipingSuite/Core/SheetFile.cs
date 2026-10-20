using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PipingSuite.Core;

/// <summary>Reads and writes tabular data as .csv or .xlsx without any third-party packages.</summary>
public static class SheetFile
{
    const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    const string Prolog = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
    static readonly UTF8Encoding Utf8 = new(false);

    /// <summary>Writes rows (row 0 is the header) as .csv or .xlsx, chosen by the path's extension.</summary>
    public static void Write(string path, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (IsXlsx(path)) WriteXlsx(path, rows);
        else WriteCsv(path, rows);
    }

    /// <summary>Reads a .csv or the first worksheet of an .xlsx into rows of strings (row 0 is the header).</summary>
    public static List<List<string>> Read(string path) => IsXlsx(path) ? ReadXlsx(path) : ReadCsv(path);

    static bool IsXlsx(string path)
    {
        var ext = Path.GetExtension(path ?? throw new ArgumentNullException(nameof(path)));
        if (ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)) return true;
        if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase)) return false;
        throw new ArgumentException($"Unsupported file type '{ext}': use .csv or .xlsx ({path}).", nameof(path));
    }

    static void WriteCsv(string path, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var v = row[i] ?? "";
                if (v.IndexOfAny([',', '"', '\r', '\n']) >= 0 || v.StartsWith(' ') || v.EndsWith(' '))
                    sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
                else sb.Append(v);
            }
            sb.Append("\r\n");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    static List<List<string>> ReadCsv(string path)
    {
        string s;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs, Utf8, detectEncodingFromByteOrderMarks: true))
            s = sr.ReadToEnd();
        var rows = new List<List<string>>();
        var row = new List<string>();
        var f = new StringBuilder();
        bool quoted = false, pending = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            pending = true;
            if (quoted)
            {
                if (c != '"') f.Append(c);
                else if (i + 1 < s.Length && s[i + 1] == '"') { f.Append('"'); i++; }
                else quoted = false;
            }
            else if (c == '"' && f.Length == 0) quoted = true;
            else if (c == ',') { row.Add(f.ToString()); f.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                row.Add(f.ToString()); f.Clear();
                rows.Add(row); row = new List<string>(); pending = false;
            }
            else f.Append(c);
        }
        if (pending) { row.Add(f.ToString()); rows.Add(row); }
        return rows;
    }

    static void WriteXlsx(string path, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        const string ct = "application/vnd.openxmlformats-officedocument.spreadsheetml.";
        Part(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
            + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + $"<Override PartName=\"/xl/workbook.xml\" ContentType=\"{ct}sheet.main+xml\"/>"
            + $"<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"{ct}worksheet+xml\"/>"
            + $"<Override PartName=\"/xl/styles.xml\" ContentType=\"{ct}styles+xml\"/></Types>");
        const string pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
        Part(zip, "_rels/.rels", $"<Relationships xmlns=\"{pkg}\"><Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Part(zip, "xl/workbook.xml", $"<workbook xmlns=\"{MainNs}\" xmlns:r=\"{RelNs}\"><sheets><sheet name=\"Parameters\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Part(zip, "xl/_rels/workbook.xml.rels", $"<Relationships xmlns=\"{pkg}\">"
            + $"<Relationship Id=\"rId1\" Type=\"{RelNs}/worksheet\" Target=\"worksheets/sheet1.xml\"/>"
            + $"<Relationship Id=\"rId2\" Type=\"{RelNs}/styles\" Target=\"styles.xml\"/></Relationships>");
        Part(zip, "xl/styles.xml", $"<styleSheet xmlns=\"{MainNs}\">"
            + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
            + "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>"
            + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
            + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
            + "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
            + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>"
            + "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");

        var settings = new XmlWriterSettings { Encoding = Utf8, CloseOutput = true, CheckCharacters = false, NewLineHandling = NewLineHandling.Entitize };
        using var w = XmlWriter.Create(zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open(), settings);
        w.WriteStartDocument(true);
        w.WriteStartElement("worksheet", MainNs);
        w.WriteStartElement("sheetViews");
        w.WriteStartElement("sheetView");
        w.WriteAttributeString("workbookViewId", "0");
        w.WriteStartElement("pane");
        w.WriteAttributeString("ySplit", "1");
        w.WriteAttributeString("topLeftCell", "A2");
        w.WriteAttributeString("activePane", "bottomLeft");
        w.WriteAttributeString("state", "frozen");
        w.WriteEndElement();
        w.WriteStartElement("selection");
        w.WriteAttributeString("pane", "bottomLeft");
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteStartElement("sheetData");
        for (var r = 0; r < rows.Count; r++)
        {
            w.WriteStartElement("row");
            w.WriteAttributeString("r", (r + 1).ToString());
            for (var c = 0; c < rows[r].Count; c++)
            {
                var v = XmlSafe(rows[r][c]);
                if (v.Length == 0) continue;
                w.WriteStartElement("c");
                w.WriteAttributeString("r", ColName(c) + (r + 1));
                if (r == 0) w.WriteAttributeString("s", "1");
                w.WriteAttributeString("t", "inlineStr");
                w.WriteStartElement("is");
                w.WriteStartElement("t");
                w.WriteAttributeString("xml", "space", null, "preserve");
                w.WriteString(v);
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        w.WriteEndDocument();
    }

    static void Part(ZipArchive zip, string name, string xml)
    {
        using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        var bytes = Utf8.GetBytes(Prolog + xml);
        s.Write(bytes, 0, bytes.Length);
    }

    static string XmlSafe(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "";
        var sb = new StringBuilder(v.Length);
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (char.IsHighSurrogate(c) && i + 1 < v.Length && char.IsLowSurrogate(v[i + 1])) { sb.Append(c).Append(v[++i]); }
            else if (c is '\t' or '\n' or '\r' || (c >= 0x20 && c <= 0xD7FF) || (c >= 0xE000 && c <= 0xFFFD)) sb.Append(c);
        }
        return sb.ToString();
    }

    static string ColName(int i)
    {
        var s = "";
        for (i++; i > 0; i = (i - 1) / 26) s = (char)('A' + (i - 1) % 26) + s;
        return s;
    }

    static int ColIndex(string cellRef)
    {
        var n = 0;
        foreach (var ch in cellRef)
        {
            if (!char.IsAsciiLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n - 1;
    }

    static List<List<string>> ReadXlsx(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        XNamespace m = MainNs, r = RelNs;

        XDocument? Load(string name)
        {
            var e = zip.GetEntry(name);
            if (e == null) return null;
            using var s = e.Open();
            return XDocument.Load(s, LoadOptions.PreserveWhitespace);
        }
        static string Text(XElement? e) => e == null ? "" : string.Concat(e.Descendants(MainName("t")).Where(t => t.Parent?.Name != MainName("rPh")).Select(t => t.Value));

        var sheetPath = "xl/worksheets/sheet1.xml";
        var rid = (string?)Load("xl/workbook.xml")?.Root?.Element(m + "sheets")?.Elements(m + "sheet").FirstOrDefault()?.Attribute(r + "id");
        var target = rid == null ? null : (string?)Load("xl/_rels/workbook.xml.rels")?.Root?.Elements()
            .FirstOrDefault(e => (string?)e.Attribute("Id") == rid)?.Attribute("Target");
        if (target != null) sheetPath = target.StartsWith('/') ? target[1..] : "xl/" + target;
        var sheet = Load(sheetPath) ?? Load("xl/worksheets/sheet1.xml")
            ?? throw new InvalidDataException($"No worksheet found in '{path}'.");

        var shared = Load("xl/sharedStrings.xml")?.Root?.Elements(m + "si").Select(Text).ToList() ?? [];
        var rows = new List<List<string>>();
        var rowIdx = -1;
        foreach (var row in sheet.Root?.Element(m + "sheetData")?.Elements(m + "row") ?? [])
        {
            rowIdx = int.TryParse((string?)row.Attribute("r"), out var rn) ? rn - 1 : rowIdx + 1;
            if (rowIdx < 0 || rowIdx >= 1_048_576) throw new InvalidDataException($"Invalid row index in '{path}'.");
            while (rows.Count <= rowIdx) rows.Add([]);
            var cells = rows[rowIdx];
            var colIdx = -1;
            foreach (var c in row.Elements(m + "c"))
            {
                var cr = (string?)c.Attribute("r");
                colIdx = cr != null && ColIndex(cr) >= 0 ? ColIndex(cr) : colIdx + 1;
                if (colIdx >= 16_384) throw new InvalidDataException($"Invalid column index in '{path}'.");
                var v = (string?)c.Element(m + "v") ?? "";
                var val = (string?)c.Attribute("t") switch
                {
                    "s" => int.TryParse(v, out var si) && si >= 0 && si < shared.Count ? shared[si] : "",
                    "inlineStr" => Text(c.Element(m + "is")),
                    "b" => v is "1" ? "TRUE" : "FALSE",
                    _ => v,
                };
                while (cells.Count <= colIdx) cells.Add("");
                cells[colIdx] = val;
            }
        }
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
        var width = rows.Count == 0 ? 0 : rows.Max(x => x.Count);
        foreach (var x in rows) while (x.Count < width) x.Add("");
        return rows;
    }

    static XName MainName(string local) => XNamespace.Get(MainNs) + local;
}
