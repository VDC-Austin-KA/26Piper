using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.Services;

/// <param name="Transparency">Surface transparency of the boxes, 0–100, so the piping reads through them.</param>
public sealed record OverallMapOptions(string ViewName, bool Rainbow, int Transparency, bool Legend);

public sealed record OverallMapResult(ViewPlan View, int Boxes, IReadOnlyList<string> Packages, IReadOnlyList<string> Warnings);

/// <summary>
/// Overall package key plan: duplicates a plan (with detailing) and draws a translucent coloured box
/// around each connected spool, one colour per PACKAGE / BC_PACKAGE value, plus a legend. Box colours
/// live on reusable "ISO MAP - &lt;value&gt;" filled region types, so a colour changed there sticks on
/// the next run. One undo removes the view.
/// </summary>
public sealed class OverallMapService(Document doc)
{
    private const string TypePrefix = "ISO MAP - ";
    private const double PadFeet = 0.5;      // box margin around each spool
    private const double SwatchInches = 0.4; // legend swatch height on paper

    /// <summary>Distinct package values in the selection, naturally sorted.</summary>
    public static IReadOnlyList<string> GetPackages(IEnumerable<Element> elements) =>
        elements.Select(IsoMapService.GetPackage).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, ColorSplasherService.NaturalComparer.Instance)
            .ToList();

    public OverallMapResult Create(ViewPlan source, IReadOnlyList<Element> elements, OverallMapOptions options)
    {
        var warnings = new List<string>();
        var spools = Spools(elements);
        var packages = spools.Select(s => s.Package).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v, ColorSplasherService.NaturalComparer.Instance).ToList();
        if (packages.Count == 0) throw new InvalidOperationException("No selected element has a PACKAGE or BC_PACKAGE value.");

        using var t = new Transaction(doc, "Overall ISO Map");
        t.Start();

        var view = (ViewPlan)doc.GetElement(source.Duplicate(ViewDuplicateOption.WithDetailing));
        view.Name = UniqueViewName(options.ViewName);

        var palette = (options.Rainbow ? Palette.Rainbow(packages.Count) : Palette.Distinct(packages.Count, seed: 0)).ToList();
        var types = packages.Select((p, i) => (p, RegionType(p, new Color(palette[i].R, palette[i].G, palette[i].B), warnings)))
            .ToDictionary(x => x.p, x => x.Item2, StringComparer.OrdinalIgnoreCase);
        var translucent = new OverrideGraphicSettings().SetSurfaceTransparency(Math.Clamp(options.Transparency, 0, 100));

        var frame = new PlanFrame(view);
        Rect2? extents = null;
        int boxes = 0;
        foreach (var spool in spools)
        {
            if (frame.Bounds(spool.Elements) is not { } r) continue;
            r = r.Inflate(PadFeet);
            Draw(view, frame, types[spool.Package], r, translucent);
            extents = extents?.Union(r) ?? r;
            boxes++;
        }

        if (options.Legend && extents is { } all) DrawLegend(view, frame, all, packages, types, translucent);

        t.Commit();
        return new OverallMapResult(view, boxes, packages, warnings);
    }

    private sealed record Spool(string Package, IReadOnlyList<Element> Elements);

    /// <summary>
    /// Connected runs of the same package. Elements without a value (usually fittings) join the run
    /// they touch, so a spool's box covers its elbows and flanges too.
    /// </summary>
    private static List<Spool> Spools(IReadOnlyList<Element> elements)
    {
        var byId = elements.ToDictionary(e => e.Id);
        var keys = elements.ToDictionary(e => e.Id, IsoMapService.GetPackage);
        var neighbours = elements.ToDictionary(e => e.Id,
            e => PipingElements.GetConnectedElements(e).Select(n => n.Id).Where(byId.ContainsKey).Distinct().ToList());

        var uf = new UnionFind(byId.Keys);
        foreach (var (id, ns) in neighbours)
        foreach (var n in ns)
            if (keys[id] is { } k && string.Equals(k, keys[n], StringComparison.OrdinalIgnoreCase)) uf.Union(id, n);

        // Unvalued elements adopt a neighbour's run; repeat so chains of fittings resolve.
        var rootKey = byId.Keys.Where(id => keys[id] is not null).GroupBy(uf.Find).ToDictionary(g => g.Key, g => keys[g.First()]!);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (id, ns) in neighbours)
            {
                if (rootKey.ContainsKey(uf.Find(id))) continue;
                var keyed = ns.FirstOrDefault(n => rootKey.ContainsKey(uf.Find(n)));
                if (keyed is null) continue;
                var key = rootKey[uf.Find(keyed)];
                uf.Union(keyed, id);
                rootKey[uf.Find(id)] = key;
                changed = true;
            }
        }

        return byId.Keys.GroupBy(uf.Find)
            .Where(g => rootKey.ContainsKey(g.Key))
            .Select(g => new Spool(rootKey[g.Key], g.Select(id => byId[id]).ToList()))
            .ToList();
    }

    /// <summary>Solid-fill region type for a package; an existing one keeps its (possibly user-edited) colour.</summary>
    private FilledRegionType RegionType(string package, Color color, List<string> warnings)
    {
        var name = Sanitize(TypePrefix + package);
        var all = new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();
        if (all.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } existing) return existing;

        var type = (FilledRegionType)all.First().Duplicate(name);
        var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
            .FirstOrDefault(f => f.GetFillPattern() is { IsSolidFill: true, Target: FillPatternTarget.Drafting });
        if (solid is null) warnings.Add("No drafting solid fill pattern found; boxes use the base region type's pattern.");
        else type.ForegroundPatternId = solid.Id;
        type.ForegroundPatternColor = color;
        type.BackgroundPatternId = ElementId.InvalidElementId;
        type.IsMasking = false;
        return type;
    }

    private void Draw(View view, PlanFrame frame, FilledRegionType type, Rect2 r, OverrideGraphicSettings ogs)
    {
        var region = FilledRegion.Create(doc, type.Id, view.Id, [frame.Loop(r)]);
        view.SetElementOverrides(region.Id, ogs);
    }

    /// <summary>Swatches with the package name inside, stacked to the left of the boxes and top-aligned with them.</summary>
    private void DrawLegend(View view, PlanFrame frame, Rect2 extents, IReadOnlyList<string> packages,
        IReadOnlyDictionary<string, FilledRegionType> types, OverrideGraphicSettings ogs)
    {
        var textType = doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
        double text = (doc.GetElement(textType)?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 3.0 / 32 / 12) * view.Scale;
        double h = Math.Max(SwatchInches / 12 * view.Scale, text * 2.2), gap = h * 0.35;
        double w = packages.Max(p => p.Length) * text * 0.8 + text * 2;
        double right = extents.MinX - w, top = extents.MaxY;

        for (int i = 0; i < packages.Count; i++)
        {
            double y = top - i * (h + gap);
            Draw(view, frame, types[packages[i]], new Rect2(right - w, y - h, right, y), ogs);
            TextNote.Create(doc, view.Id, frame.Point(right - w + text, y - h / 2), packages[i], new TextNoteOptions(textType)
            {
                HorizontalAlignment = HorizontalTextAlignment.Left,
                VerticalAlignment = VerticalTextAlignment.Middle,
            });
        }
    }

    private string UniqueViewName(string name)
    {
        name = Sanitize(name);
        var taken = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = name;
        for (int i = 2; taken.Contains(unique); i++) unique = $"{name} ({i})";
        return unique;
    }

    // Characters Revit refuses in view and type names.
    private static string Sanitize(string name) => string.Concat(name.Select(c => "{}[]|;:<>?`~\\".Contains(c) ? '-' : c)).Trim();

    private readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY)
    {
        public Rect2 Union(Rect2 o) =>
            new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));

        public Rect2 Inflate(double d) => new(MinX - d, MinY - d, MaxX + d, MaxY + d);
    }

    /// <summary>The plan's own right/up axes, so boxes stay square to the sheet on a rotated plan.</summary>
    private sealed class PlanFrame(View view)
    {
        private readonly XYZ _origin = view.Origin, _right = view.RightDirection, _up = view.UpDirection;

        public XYZ Point(double x, double y) => _origin + _right * x + _up * y;

        public CurveLoop Loop(Rect2 r)
        {
            var p = new[] { Point(r.MinX, r.MinY), Point(r.MaxX, r.MinY), Point(r.MaxX, r.MaxY), Point(r.MinX, r.MaxY) };
            return CurveLoop.Create(p.Select((q, i) => (Curve)Line.CreateBound(q, p[(i + 1) % 4])).ToList());
        }

        public Rect2? Bounds(IEnumerable<Element> elements)
        {
            var pts = elements.Select(e => e.get_BoundingBox(null)).OfType<BoundingBoxXYZ>()
                .SelectMany(b => new[] { b.Min, new XYZ(b.Max.X, b.Min.Y, 0), new XYZ(b.Min.X, b.Max.Y, 0), b.Max })
                .Select(q => (X: (q - _origin).DotProduct(_right), Y: (q - _origin).DotProduct(_up)))
                .ToList();
            return pts.Count == 0 ? null : new Rect2(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Max(q => q.X), pts.Max(q => q.Y));
        }
    }
}
