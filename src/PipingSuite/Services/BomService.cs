using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;
using static PipingSuite.Services.IsoMapService;

namespace PipingSuite.Services;

public enum BomPlacement { Right, Below, Left, Above }

/// <param name="TextSize">Text height on paper, in inches; null fits the table to the piping (its height beside it, its width above or below).</param>
/// <param name="SquareCrop">Re-crop square around piping + table (lines up with other ISO views); otherwise tight around both.</param>
public sealed record BomOptions(double? TextSize, BomPlacement Placement, bool SquareCrop);

public sealed record BomRow(int Item, string Qty, string Size, string Description);

public sealed record BomResult(int Rows, double TextSize, IReadOnlyList<string> Notes);

/// <summary>
/// Bill of materials drawn in the view itself, so it goes onto the sheet with the iso: one column of text per
/// ITEM / QTY / SIZE / DESCRIPTION, each a bordered text note, set side by side beside the piping. Pipe is listed
/// by type and size with its total length; fittings and valves by family, type and size with a count. Re-running
/// replaces the view's previous BOM, so a new size or side is just another run. The caller holds the transaction.
/// </summary>
public sealed class BomService(Document doc)
{
    /// <summary>Text note types made for the BOM are named "PipingSuite BOM 3/32&quot;"; notes of these types are the BOM.</summary>
    public const string TypePrefix = "PipingSuite BOM ";

    private static readonly string[] Headers = ["ITEM", "QTY", "SIZE", "DESCRIPTION"];
    private const double FitFrom = 1.0 / 8, FitMin = 1.0 / 16, FitMax = 3.0 / 8; // in on paper

    public static List<BomRow> Rows(Document doc, IEnumerable<Element> elements)
    {
        var units = doc.GetUnits();
        return elements.Where(PipingElements.IsPiping).DistinctBy(e => e.Id)
            .GroupBy(e => (Rank: Rank(e), Description: Description(doc, e), Size: Size(e)))
            .OrderBy(g => g.Key.Rank)
            .ThenBy(g => g.Key.Description, ColorSplasherService.NaturalComparer.Instance)
            .ThenBy(g => g.Key.Size, ColorSplasherService.NaturalComparer.Instance)
            .Select((g, i) => new BomRow(i + 1,
                g.Key.Rank == 0
                    ? UnitFormatUtils.Format(units, SpecTypeId.Length, g.Sum(e => (e.Location as LocationCurve)?.Curve.Length ?? 0), false)
                    : g.Count().ToString(),
                g.Key.Size, g.Key.Description))
            .ToList();
    }

    public BomResult Place(View view, IReadOnlyList<Element> elements, BomOptions options)
    {
        var notes = new List<string>();
        var rows = Rows(doc, elements);
        if (rows.Count == 0) return new BomResult(0, 0, ["There is no piping to list."]);
        if (view is View3D { IsLocked: false } v3)
        {
            v3.SaveOrientationAndLock();
            notes.Add("The 3D view was locked so it can hold the BOM.");
        }

        int removed = Remove(view);
        if (removed > 0) notes.Add("The view's previous BOM was replaced.");

        var frame = new ViewFrame(view);
        var content = Content(view, frame, elements);
        var anchor = Centre(elements);

        double size = options.TextSize ?? FitFrom;
        var columns = Build(view, frame, anchor, rows, size);
        if (options.TextSize is null)
        {
            var table = Extents(columns);
            double fit = options.Placement is BomPlacement.Right or BomPlacement.Left
                ? size * content.Height / table.Height
                : size * content.Width / table.Width;
            fit = Math.Clamp(Math.Round(fit * 64) / 64, FitMin, FitMax);
            if (fit != size)
            {
                doc.Delete(columns.Select(c => c.Note.Id).ToList());
                columns = Build(view, frame, anchor, rows, size = fit);
            }
        }

        // Side by side, one text height clear of the piping, its tags and dimensions.
        var t = Extents(columns);
        double gap = 2 * size / 12 * view.Scale;
        var (left, top) = options.Placement switch
        {
            BomPlacement.Right => (content.MaxX + gap, content.MaxY),
            BomPlacement.Left => (content.MinX - gap - t.Width, content.MaxY),
            BomPlacement.Below => (content.MinX, content.MinY - gap),
            _ => (content.MinX, content.MaxY + gap + t.Height),
        };
        foreach (var (note, box) in columns)
        {
            ElementTransformUtils.MoveElement(doc, note.Id, frame.ToModelVector(left - box.MinX, top - box.MaxY));
            left += box.Width;
        }

        Crop(view, content.Union(new Rect2(left - t.Width, top - t.Height, left, top)), options.SquareCrop);
        return new BomResult(rows.Count, size, notes);
    }

    /// <summary>Deletes the view's BOM notes; returns how many there were.</summary>
    public int Remove(View view)
    {
        var ids = new FilteredElementCollector(doc).OwnedByView(view.Id).OfClass(typeof(TextNote)).Cast<TextNote>()
            .Where(IsBom).Select(n => n.Id).ToList();
        if (ids.Count > 0) doc.Delete(ids);
        return ids.Count;
    }

    private bool IsBom(TextNote note) => doc.GetElement(note.GetTypeId())?.Name.StartsWith(TypePrefix, StringComparison.Ordinal) == true;

    /// <summary>One bordered note per column, header bold and underlined, measured in the crop frame.</summary>
    private List<(TextNote Note, Rect2 Box)> Build(View view, ViewFrame frame, XYZ anchor, IReadOnlyList<BomRow> rows, double size)
    {
        var type = NoteType(size);
        var cells = new[]
        {
            rows.Select(r => r.Item.ToString()), rows.Select(r => r.Qty), rows.Select(r => r.Size), rows.Select(r => r.Description),
        };
        var created = new List<TextNote>();
        for (int c = 0; c < Headers.Length; c++)
        {
            var text = string.Join("\r", cells[c].Prepend(Headers[c]).Select(v => string.IsNullOrWhiteSpace(v) ? "-" : v));
            var note = TextNote.Create(doc, view.Id, anchor, text, new TextNoteOptions(type.Id)
            {
                HorizontalAlignment = c == Headers.Length - 1 ? HorizontalTextAlignment.Left : HorizontalTextAlignment.Center,
                VerticalAlignment = VerticalTextAlignment.Top,
            });
            var formatted = note.GetFormattedText();
            var header = new TextRange(0, Headers[c].Length);
            formatted.SetBoldStatus(header, true);
            formatted.SetUnderlineStatus(header, true);
            note.SetFormattedText(formatted);
            created.Add(note);
        }
        doc.Regenerate();
        return created.Select(n => (n, frame.PlanarRect(n.get_BoundingBox(view)))).ToList();
    }

    private static Rect2 Extents(IEnumerable<(TextNote Note, Rect2 Box)> columns) =>
        new(0, 0, columns.Sum(c => c.Box.Width), columns.Max(c => c.Box.Height));

    /// <summary>The piping plus the view's tags, dimensions and other text: what the table must stay clear of.</summary>
    private Rect2 Content(View view, ViewFrame frame, IEnumerable<Element> elements)
    {
        var content = frame.Bounds(elements);
        foreach (var e in new FilteredElementCollector(doc).OwnedByView(view.Id).WhereElementIsNotElementType())
        {
            if (e.get_BoundingBox(view) is not { } bb) continue;
            if (e is IndependentTag || e is TextNote n && !IsBom(n)) content = content.Union(frame.PlanarRect(bb));
            else if (e is Dimension) content = content.Union(frame.Project(Corners(bb)));
        }
        return content;
    }

    private static XYZ Centre(IEnumerable<Element> elements)
    {
        var boxes = elements.Select(e => e.get_BoundingBox(null)).Where(b => b is not null).ToList();
        return boxes.Count == 0 ? XYZ.Zero : boxes.Aggregate(XYZ.Zero, (s, b) => s + (b.Min + b.Max) / 2) / boxes.Count;
    }

    /// <summary>The BOM text type for this size: a copy of the default text type with a border and opaque background.</summary>
    private TextNoteType NoteType(double inches)
    {
        var name = TypePrefix + Fraction(inches);
        var types = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();
        if (types.FirstOrDefault(t => t.Name == name) is { } existing) return existing;

        var source = doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType)) as TextNoteType ?? types.First();
        var type = (TextNoteType)source.Duplicate(name);
        type.get_Parameter(BuiltInParameter.TEXT_SIZE)?.Set(inches / 12);
        type.get_Parameter(BuiltInParameter.TEXT_BOX_VISIBILITY)?.Set(1);
        type.get_Parameter(BuiltInParameter.TEXT_BACKGROUND)?.Set(0); // opaque
        type.get_Parameter(BuiltInParameter.LEADER_OFFSET_SHEET)?.Set(inches / 12 * 0.5);
        return type;
    }

    /// <summary>Inches to the nearest 1/64 as a reduced fraction, e.g. 0.09375 → 3/32".</summary>
    public static string Fraction(double inches)
    {
        int n = (int)Math.Round(inches * 64), d = 64;
        while (n > 0 && n % 2 == 0 && d > 1) (n, d) = (n / 2, d / 2);
        return d == 1 ? $"{n}\"" : n > d ? $"{n / d} {n % d}/{d}\"" : $"{n}/{d}\"";
    }

    private static int Rank(Element e) => (BuiltInCategory)e.Category.Id.Value switch
    {
        BuiltInCategory.OST_PipeCurves => 0,
        BuiltInCategory.OST_PipeFitting => 1,
        _ => 2,
    };

    private static string Size(Element e) => e.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE)?.AsString() ?? string.Empty;

    /// <summary>Pipe: "PIPE, " + its segment (material and schedule), else its type. Fittings and valves: family and type.</summary>
    private static string Description(Document doc, Element e)
    {
        if (e is MEPCurve)
            return "PIPE, " + (e.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM)?.AsValueString() is { Length: > 0 } segment
                ? segment : doc.GetElement(e.GetTypeId())?.Name);
        if (e is not FamilyInstance { Symbol: { } symbol }) return doc.GetElement(e.GetTypeId())?.Name ?? e.Name;
        return symbol.Name is "Standard" or "Default" || symbol.Name == symbol.FamilyName ? symbol.FamilyName : $"{symbol.FamilyName} : {symbol.Name}";
    }
}
