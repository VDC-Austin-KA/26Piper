using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using PipingSuite.Core;
using PipingSuite.UI.ViewModels;

namespace PipingSuite.Services;

/// <summary>The four plan corners an isometric is viewed from (eye above that corner, looking down across the model).</summary>
public enum IsoCorner { SouthWest, SouthEast, NorthEast, NorthWest }

public sealed record IsoMapOptions(string ViewName, IsoCorner Corner, string ColorParameter, bool Rainbow, bool TagPipes);

public sealed record IsoMapResult(View3D View, int ColoredValues, int Tags, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds an "ISO MAP" sheet view from a selection: a new locked isometric 3D view with only the
/// selection visible, coloured per <see cref="IsoMapOptions.ColorParameter"/> value and with every pipe
/// tagged. Tags are pushed out along the content's short side and the crop is made square, so the
/// view packs well onto a sheet. One undo reverts the whole view.
/// </summary>
public sealed class IsoMapService(Document doc)
{
    public const string TagFamily = "ISO Tag";
    public const string TagType = "Pipe Size System Tag";
    public static readonly string[] PackageParameters = ["PACKAGE", "BC_PACKAGE"];

    private const double Gap = 0.15; // tag spacing as a fraction of tag height

    public static string? GetPackage(Element e) =>
        PackageParameters.Select(p => ParameterHelper.GetValueByName(e, p)).FirstOrDefault(v => v is not null);

    public FamilySymbol? FindTagType() =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(s => s.FamilyName.Equals(TagFamily, StringComparison.OrdinalIgnoreCase)
                                 && s.Name.Equals(TagType, StringComparison.OrdinalIgnoreCase));

    public IsoMapResult Create(IReadOnlyList<Element> elements, IsoMapOptions options)
    {
        var warnings = new List<string>();
        var ids = elements.Select(e => e.Id).ToList();

        using var tg = new TransactionGroup(doc, "ISO Map");
        tg.Start();

        View3D view;
        using (var t = new Transaction(doc, "ISO Map - View"))
        {
            t.Start();
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            view = View3D.CreateIsometric(doc, type.Id);
            view.Name = UniqueViewName(options.ViewName);

            var box = ModelBounds(elements);
            view.SetOrientation(Orientation(options.Corner, box));
            view.SetSectionBox(box); // later model additions outside the selection stay out of the map
            view.SetCategoryHidden(new ElementId(BuiltInCategory.OST_SectionBox), true);
            doc.Regenerate();

            view.IsolateElementsTemporary(ids);
            view.ConvertTemporaryHideIsolateToPermanent();
            view.SaveOrientationAndLock(); // tags in 3D need a locked view
            t.Commit();
        }

        int colored = Colorize(view, elements, options);

        int tags = 0;
        using (var t = new Transaction(doc, "ISO Map - Tags and crop"))
        {
            t.Start();
            var placed = new List<IndependentTag>();
            if (options.TagPipes)
            {
                if (FindTagType() is { } symbol)
                {
                    if (!symbol.IsActive) symbol.Activate();
                    placed = PlaceTags(view, symbol, elements, warnings);
                    tags = placed.Count;
                }
                else warnings.Add($"Tag type \"{TagFamily} : {TagType}\" is not loaded, so pipes were not tagged.");
            }
            CropSquare(view, elements, placed);
            t.Commit();
        }

        tg.Assimilate();
        return new IsoMapResult(view, colored, tags, warnings);
    }

    /// <summary>Colour Splasher overrides per value of the colour parameter; elements without a value keep their graphics.</summary>
    private int Colorize(View view, IReadOnlyList<Element> elements, IsoMapOptions options)
    {
        var groups = elements
            .GroupBy(e => ParameterHelper.GetValueByName(e, options.ColorParameter))
            .Where(g => g.Key is not null)
            .OrderBy(g => g.Key!, ColorSplasherService.NaturalComparer.Instance)
            .ToList();
        if (groups.Count == 0) return 0;

        var colors = (options.Rainbow ? Palette.Rainbow(groups.Count) : Palette.Distinct(groups.Count, seed: 0)).ToList();
        new ColorSplasherService(doc, view).Apply(groups.Select((g, i) =>
            ((IReadOnlyList<ElementId>)g.Select(e => e.Id).ToList(), new Color(colors[i].R, colors[i].G, colors[i].B))));
        return groups.Count;
    }

    /// <summary>
    /// Tags each pipe beside its midpoint, offset along the content's short side (above/below a wide
    /// layout, left/right of a tall one) on the side facing away from the centre, so annotations fill
    /// out the square. Overlapping tags step further out and get a leader.
    /// </summary>
    private List<IndependentTag> PlaceTags(View3D view, FamilySymbol symbol, IReadOnlyList<Element> elements, List<string> warnings)
    {
        var frame = new ViewFrame(view);
        var content = frame.Bounds(doc, elements.Select(e => e.Id).ToList());

        var tags = new List<(IndependentTag Tag, XYZ Mid)>();
        foreach (var pipe in elements.OfType<Pipe>())
        {
            if (pipe.Location is not LocationCurve { Curve: { } curve }) continue;
            var mid = curve.Evaluate(0.5, true);
            try
            {
                tags.Add((IndependentTag.Create(doc, symbol.Id, view.Id, new Reference(pipe), false, TagOrientation.Horizontal, mid), mid));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                warnings.Add($"Pipe [{pipe.Id.Value}] not tagged: {ex.Message}");
            }
        }
        if (tags.Count == 0) return [];
        doc.Regenerate();

        bool vertical = content.Width >= content.Height; // push tags out along the short side
        var (cx, cy) = content.Center;
        var taken = new List<Rect2>();
        foreach (var (tag, mid) in tags)
        {
            if (tag.get_BoundingBox(view) is not { } bb) continue;
            var box = frame.Project(Corners(bb));
            var (mx, my) = frame.ToLocal(mid);
            double step = (vertical ? box.Height : box.Width) * (1 + Gap);
            int side = (vertical ? my >= cy : mx >= cx) ? 1 : -1;
            Rect2 Slot(int k) // k-th tag height (or width) out from the pipe, on the side of k's sign
            {
                double off = k * step - Math.Sign(k) * step / 2;
                return vertical ? box.MovedTo(mx, my + off) : box.MovedTo(mx + off, my);
            }

            // Slots: just beside the pipe, then further out on the same side, then the other side.
            Rect2? fit = null;
            foreach (int k in Enumerable.Range(1, 6).Select(n => side * n).Concat(Enumerable.Range(1, 6).Select(n => -side * n)))
            {
                var candidate = Slot(k);
                if (taken.Any(candidate.Overlaps)) continue;
                fit = candidate;
                if (Math.Abs(k) > 1) tag.HasLeader = true;
                break;
            }
            fit ??= Slot(side); // crowded: keep it beside the pipe

            var (dx, dy) = (fit.Value.Center.X - box.Center.X, fit.Value.Center.Y - box.Center.Y);
            tag.TagHeadPosition += frame.ToModelVector(dx, dy);
            taken.Add(fit.Value);
        }
        return tags.Select(t => t.Tag).ToList();
    }

    /// <summary>Square crop around the elements and tags, centred, with a small margin.</summary>
    private void CropSquare(View3D view, IReadOnlyList<Element> elements, IReadOnlyList<IndependentTag> tags)
    {
        doc.Regenerate();
        var frame = new ViewFrame(view);
        var all = frame.Bounds(doc, elements.Select(e => e.Id).ToList());
        foreach (var tag in tags)
            if (tag.get_BoundingBox(view) is { } bb) all = all.Union(frame.Project(Corners(bb)));

        double half = Math.Max(all.Width, all.Height) * 0.55; // 10% margin overall
        var (cx, cy) = all.Center;
        var crop = view.CropBox;
        crop.Min = new XYZ(cx - half, cy - half, crop.Min.Z);
        crop.Max = new XYZ(cx + half, cy + half, crop.Max.Z);
        view.CropBoxActive = true;
        view.CropBox = crop;
        view.CropBoxVisible = false;
    }

    private static ViewOrientation3D Orientation(IsoCorner corner, BoundingBoxXYZ box)
    {
        var (x, y) = corner switch
        {
            IsoCorner.SouthWest => (-1, -1),
            IsoCorner.SouthEast => (1, -1),
            IsoCorner.NorthEast => (1, 1),
            _ => (-1, 1),
        };
        var forward = new XYZ(-x, -y, -1).Normalize(); // from the corner, down towards the centre
        var right = forward.CrossProduct(XYZ.BasisZ).Normalize();
        var up = right.CrossProduct(forward).Normalize();
        var centre = (box.Min + box.Max) / 2;
        return new ViewOrientation3D(centre - forward * (box.Max - box.Min).GetLength(), up, forward);
    }

    private static BoundingBoxXYZ ModelBounds(IEnumerable<Element> elements)
    {
        var boxes = elements.Select(e => e.get_BoundingBox(null)).Where(b => b is not null).ToList();
        if (boxes.Count == 0) throw new InvalidOperationException("The selected elements have no geometry.");
        var pad = new XYZ(1, 1, 1); // 1 ft so insulation and end caps aren't clipped
        return new BoundingBoxXYZ
        {
            Min = new XYZ(boxes.Min(b => b.Min.X), boxes.Min(b => b.Min.Y), boxes.Min(b => b.Min.Z)) - pad,
            Max = new XYZ(boxes.Max(b => b.Max.X), boxes.Max(b => b.Max.Y), boxes.Max(b => b.Max.Z)) + pad,
        };
    }

    private string UniqueViewName(string name)
    {
        // Characters Revit refuses in view names.
        name = string.Concat(name.Select(c => "{}[]|;:<>?`~\\".Contains(c) ? '-' : c)).Trim();
        var taken = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = name;
        for (int i = 2; taken.Contains(unique); i++) unique = $"{name} ({i})";
        return unique;
    }

    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ b) =>
        from x in new[] { b.Min.X, b.Max.X }
        from y in new[] { b.Min.Y, b.Max.Y }
        from z in new[] { b.Min.Z, b.Max.Z }
        select b.Transform.OfPoint(new XYZ(x, y, z));

    /// <summary>Axis-aligned rectangle in the view's crop coordinates (x = right, y = up).</summary>
    private readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public (double X, double Y) Center => ((MinX + MaxX) / 2, (MinY + MaxY) / 2);

        public bool Overlaps(Rect2 o) => MinX < o.MaxX && o.MinX < MaxX && MinY < o.MaxY && o.MinY < MaxY;

        public Rect2 Union(Rect2 o) =>
            new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));

        public Rect2 MovedTo(double cx, double cy) =>
            new(cx - Width / 2, cy - Height / 2, cx + Width / 2, cy + Height / 2);
    }

    /// <summary>Projects model points into the view's crop frame, where the crop box itself is defined.</summary>
    private sealed class ViewFrame(View view)
    {
        private readonly Transform _toModel = view.CropBox.Transform;
        private readonly Transform _toLocal = view.CropBox.Transform.Inverse;

        public (double X, double Y) ToLocal(XYZ p)
        {
            var l = _toLocal.OfPoint(p);
            return (l.X, l.Y);
        }

        public XYZ ToModelVector(double dx, double dy) => _toModel.BasisX * dx + _toModel.BasisY * dy;

        public Rect2 Project(IEnumerable<XYZ> points)
        {
            var local = points.Select(ToLocal).ToList();
            return new Rect2(local.Min(p => p.X), local.Min(p => p.Y), local.Max(p => p.X), local.Max(p => p.Y));
        }

        /// <summary>Pipe ends (tighter than a box on sloped runs) or bounding-box corners for everything else.</summary>
        public Rect2 Bounds(Document doc, IReadOnlyList<ElementId> ids) => Project(ids.Select(doc.GetElement).SelectMany(e =>
            e.Location is LocationCurve { Curve: { } c } ? [c.GetEndPoint(0), c.GetEndPoint(1)]
            : e.get_BoundingBox(null) is { } b ? Corners(b)
            : Enumerable.Empty<XYZ>()));
    }
}
