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

/// <param name="Scale">View scale denominator, e.g. 24 for 1/2" = 1'-0"; sets the tag size relative to the piping.</param>
/// <param name="Color">Colour per <paramref name="ColorParameter"/> value; off leaves the shaded piping as it is.</param>
/// <param name="Bom">Bill of materials table beside the piping (see <see cref="BomService"/>); null for none.</param>
public sealed record IsoMapOptions(string ViewName, IsoCorner Corner, int Scale, string ColorParameter, bool Rainbow, bool TagSegments,
    bool Color = true, BomOptions? Bom = null);

public sealed record IsoMapResult(View3D View, int ColoredValues, int Tags, IReadOnlyList<string> Warnings);

/// <summary>
/// Builds an "ISO MAP" sheet view from a selection: a new locked, shaded isometric with only the
/// selection visible, coloured per <see cref="IsoMapOptions.ColorParameter"/> value, and one tag per
/// value stacked in a column beside the piping with shouldered leaders. The column side and height are
/// chosen to make the content + tags as square as possible, then the crop is made square, so the view
/// packs well onto a sheet. One undo reverts the whole view.
/// </summary>
public sealed class IsoMapService(Document doc)
{
    public const string TagFamily = "ISO Tag";
    public const string TagType = "Pipe Size System Tag";
    public static readonly string[] PackageParameters = ["PACKAGE", "BC_PACKAGE"];

    // Tag column spacing, as multiples of the tallest tag.
    private const double RowPitch = 1.8, Shoulder = 1.6, Clearance = 0.6;

    public static string? GetPackage(Element e) =>
        PackageParameters.Select(p => ParameterHelper.GetValueByName(e, p)).FirstOrDefault(v => v is not null);

    public FamilySymbol? FindTagType() =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
            .FirstOrDefault(s => s.FamilyName.Equals(TagFamily, StringComparison.OrdinalIgnoreCase)
                                 && s.Name.Equals(TagType, StringComparison.OrdinalIgnoreCase));

    public IsoMapResult Create(IReadOnlyList<Element> elements, IsoMapOptions options)
    {
        var warnings = new List<string>();

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
            if (view.ViewTemplateId == ElementId.InvalidElementId) // a template owns these otherwise
            {
                view.Scale = options.Scale;
                view.DetailLevel = ViewDetailLevel.Fine;
                view.DisplayStyle = DisplayStyle.ShadingWithEdges;
            }
            else warnings.Add("The 3D view type applies a view template, so its scale, detail level and style were kept.");

            var box = ModelBounds(elements);
            view.SetOrientation(Orientation(options.Corner, box));
            view.SetSectionBox(box); // later model additions outside the selection stay out of the map
            view.SetCategoryHidden(new ElementId(BuiltInCategory.OST_SectionBox), true);
            doc.Regenerate();

            view.IsolateElementsTemporary(elements.Select(e => e.Id).ToList());
            view.ConvertTemporaryHideIsolateToPermanent();
            view.SaveOrientationAndLock(); // tags in 3D need a locked view
            t.Commit();
        }

        var groups = elements
            .GroupBy(e => ParameterHelper.GetValueByName(e, options.ColorParameter))
            .Where(g => g.Key is not null)
            .OrderBy(g => g.Key!, ColorSplasherService.NaturalComparer.Instance)
            .ToList();
        if (groups.Count == 0) warnings.Add($"No selected element has a \"{options.ColorParameter}\" value, so nothing was colored or tagged.");
        if (options.Color) Colorize(view, groups, options.Rainbow);

        int tags = 0;
        using (var t = new Transaction(doc, "ISO Map - Tags and crop"))
        {
            t.Start();
            var frame = new ViewFrame(view);
            var extents = frame.Bounds(elements);
            if (options.TagSegments && groups.Count > 0)
            {
                if (FindTagType() is { } symbol)
                {
                    if (!symbol.IsActive) symbol.Activate();
                    var placed = PlaceTags(view, frame, symbol, elements, groups, warnings);
                    tags = placed.Count;
                    foreach (var r in placed) extents = extents.Union(r);
                }
                else warnings.Add($"Tag type \"{TagFamily} : {TagType}\" is not loaded, so line segments were not tagged.");
            }
            Crop(view, extents);
            if (options.Bom is { } bom) warnings.AddRange(new BomService(doc).Place(view, elements, bom).Notes);
            t.Commit();
        }

        tg.Assimilate();
        return new IsoMapResult(view, groups.Count, tags, warnings);
    }

    /// <summary>Colour Splasher fills per value, edges left black like a shaded iso; elements without a value keep their graphics.</summary>
    private void Colorize(View view, IReadOnlyList<IGrouping<string?, Element>> groups, bool rainbow)
    {
        if (groups.Count == 0) return;
        var colors = (rainbow ? Palette.Rainbow(groups.Count) : Palette.Distinct(groups.Count, seed: 0)).ToList();
        new ColorSplasherService(doc, view).Apply(groups.Select((g, i) =>
            ((IReadOnlyList<ElementId>)g.Select(e => e.Id).ToList(), new Color(colors[i].R, colors[i].G, colors[i].B))), colorLines: false);
    }

    /// <summary>
    /// One tag per line segment, on its longest pipe. Tags stack in columns (ordered so leaders don't
    /// cross each other), left- or right-aligned just clear of the piping, each with a horizontal shoulder
    /// and a leader to the part of its pipe nearest the column. A single column on either side and a split
    /// left/right column are tried over a range of heights: the squarest overall layout wins, then the one
    /// whose leaders cross the fewest pipes.
    /// </summary>
    /// <returns>The tag rectangles in the view's crop frame.</returns>
    internal List<Rect2> PlaceTags(View view, ViewFrame frame, FamilySymbol symbol, IReadOnlyList<Element> elements,
        IReadOnlyList<IGrouping<string?, Element>> groups, List<string> warnings)
    {
        var created = new List<(IndependentTag Tag, Curve Curve)>();
        foreach (var g in groups)
        {
            var pipe = g.OfType<Pipe>().Where(p => p.Location is LocationCurve)
                .OrderByDescending(p => ((LocationCurve)p.Location).Curve.Length).FirstOrDefault();
            if (pipe is null)
            {
                warnings.Add($"\"{g.Key}\" has no pipe to tag.");
                continue;
            }
            var curve = ((LocationCurve)pipe.Location).Curve;
            try
            {
                created.Add((IndependentTag.Create(doc, symbol.Id, view.Id, new Reference(pipe), false, TagOrientation.Horizontal, curve.Evaluate(0.5, true)), curve));
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                warnings.Add($"\"{g.Key}\" not tagged: {ex.Message}");
            }
        }
        if (created.Count == 0) return [];

        doc.Regenerate();
        var items = new List<TagItem>();
        foreach (var (tag, curve) in created)
        {
            if (tag.get_BoundingBox(view) is not { } bb) continue;
            // Leader lands on the stretch of pipe nearest the column (middle 60%, so it reads as this pipe).
            var points = Enumerable.Range(2, 7).Select(i => curve.Evaluate(i / 10.0, true)).ToList();
            var right = points.OrderByDescending(q => Math.Round(frame.ToLocal(q).X, 6)).ThenBy(q => q.DistanceTo(points[3])).First();
            var left = points.OrderBy(q => Math.Round(frame.ToLocal(q).X, 6)).ThenBy(q => q.DistanceTo(points[3])).First();
            items.Add(new TagItem(tag, frame.PlanarRect(bb), [right, left], [frame.ToLocal(right), frame.ToLocal(left)]));
        }
        if (items.Count == 0) return [];

        var outline = new Outline(frame, elements);
        var content = frame.Bounds(elements);
        double h = items.Max(i => i.Box.Height), pitch = h * RowPitch;
        var all = Enumerable.Range(0, items.Count).ToList();

        // One column on either side, or (for branches off both sides of a run) a split: tags for the
        // branches above the run's main direction on one side, the rest on the other.
        var candidates = new List<List<ColumnPlan>>();
        foreach (int side in new[] { 1, -1 }) candidates.Add([Plan(all, side, content)]);
        var (dx, dy) = outline.MainDirection();
        if (dx < 0) (dx, dy) = (-dx, -dy);
        var (cx, cy) = content.Center;
        var above = all.Where(i => (items[i].At[0].Y + items[i].At[1].Y) / 2 - cy > dy / dx * ((items[i].At[0].X + items[i].At[1].X) / 2 - cx)).ToList();
        var below = all.Except(above).ToList();
        if (above.Count > 0 && below.Count > 0 && Math.Abs(dx) > 0.1) // a vertical riser has no "above"
        {
            int aboveSide = dy > 0 ? -1 : 1; // run rising to the right leaves room top-left, falling leaves top-right
            var first = Plan(above, aboveSide, content);
            candidates.Add([first, Plan(below, -aboveSide, first.Extents)]);
        }
        var layout = candidates.MinBy(c => Score(c[^1].Extents, c.Sum(p => p.Crossings), c.Sum(p => p.Leaders)))!;

        var rects = new List<Rect2>();
        foreach (var plan in layout)
        {
            int k = plan.Side > 0 ? 0 : 1;
            for (int i = 0; i < plan.Order.Count; i++)
            {
                var (tag, box, anchors, at) = items[plan.Order[i]];
                var row = Row(box, plan.Side, plan.Elbow, plan.Top, i);
                var head = tag.TagHeadPosition + frame.ToModelVector(row.Center.X - box.Center.X, row.Center.Y - box.Center.Y);
                try
                {
                    tag.HasLeader = true;
                    tag.LeaderEndCondition = LeaderEndCondition.Free;
                    var reference = tag.GetTaggedReferences().First();
                    tag.SetLeaderEnd(reference, anchors[k]);
                    tag.TagHeadPosition = head;
                    tag.SetLeaderElbow(reference, anchors[k] + frame.ToModelVector(plan.Elbow - at[k].X, row.Center.Y - at[k].Y));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    tag.TagHeadPosition = head; // keep the column even if the leader can't be shaped
                    warnings.Add($"Leader for tag [{tag.Id.Value}] left as placed: {ex.Message}");
                }
                rects.Add(row);
            }
        }
        return rects;

        // Squarest overall layout first; then leaders that stay clear of the piping, then short ones.
        double Score(Rect2 extents, int crossings, double leaders) =>
            Math.Max(extents.Width, extents.Height) + 2 * h * crossings + 0.1 * leaders / items.Count;

        // Row i of the column: text starts one shoulder beyond the elbow, aligned on the side facing the pipes.
        Rect2 Row(Rect2 box, int side, double elbow, double top, int i) =>
            box.MovedTo(elbow + side * (h * Shoulder + box.Width / 2), top - (i + 0.5) * pitch);

        // Best column for these tags on one side, sweeping its centre over the content's height.
        ColumnPlan Plan(List<int> subset, int side, Rect2 around)
        {
            int s0 = side > 0 ? 0 : 1;
            ColumnPlan? best = null;
            for (int s = 0; s <= 20; s++)
            {
                double top = content.MinY + content.Height * s / 20 + subset.Count * pitch / 2;
                // Elbows sit beyond every anchor and beyond any piping level with the column.
                double reach = side > 0 ? subset.Max(i => items[i].At[0].X) : subset.Min(i => items[i].At[1].X);
                for (int i = 0; i < subset.Count; i++)
                    if (outline.Reach(top - (i + 1) * pitch, top - i * pitch, side) is { } r)
                        reach = side > 0 ? Math.Max(reach, r) : Math.Min(reach, r);
                double elbow = reach + side * h * Clearance;
                var order = Untangle(subset, s0, elbow, top);

                var extents = around;
                double leaders = 0;
                int crossings = 0;
                for (int i = 0; i < order.Count; i++)
                {
                    var item = items[order[i]];
                    var row = Row(item.Box, side, elbow, top, i);
                    extents = extents.Union(row);
                    leaders += Math.Abs(elbow - item.At[s0].X) + Math.Abs(row.Center.Y - item.At[s0].Y);
                    crossings += outline.Crossings((elbow, row.Center.Y), item.At[s0]);
                }
                var plan = new ColumnPlan(side, top, elbow, order, extents, crossings, leaders);
                if (best is null || Score(extents, crossings, leaders) < Score(best.Extents, best.Crossings, best.Leaders)) best = plan;
            }
            return best!;
        }

        // Top-to-bottom by where each leader lands, then swap neighbouring rows whose leaders cross.
        List<int> Untangle(List<int> subset, int s0, double elbow, double top)
        {
            var order = subset.OrderByDescending(i => items[i].At[s0].Y).ToList();
            for (bool swapped = true; swapped;)
            {
                swapped = false;
                for (int i = 0; i + 1 < order.Count; i++)
                {
                    var (a, b) = (items[order[i]].At[s0], items[order[i + 1]].At[s0]);
                    double ya = top - (i + 0.5) * pitch, yb = ya - pitch;
                    if (!Crosses((elbow, ya), a, (elbow, yb), b)) continue;
                    (order[i], order[i + 1]) = (order[i + 1], order[i]);
                    swapped = true; // uncrossing shortens the total leader length, so this terminates
                }
            }
            return order;
        }
    }

    /// <summary>One stacked tag column: which side, where it sits, and which tag goes in each row.</summary>
    private sealed record ColumnPlan(int Side, double Top, double Elbow, List<int> Order, Rect2 Extents, int Crossings, double Leaders);

    /// <summary>A placed tag, its on-screen box, and where its leader would land for a right [0] or left [1] column.</summary>
    private sealed record TagItem(IndependentTag Tag, Rect2 Box, XYZ[] Anchors, (double X, double Y)[] At);

    private static bool Crosses((double X, double Y) p1, (double X, double Y) p2, (double X, double Y) q1, (double X, double Y) q2)
    {
        static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        return Cross(p1, p2, q1) * Cross(p1, p2, q2) < 0 && Cross(q1, q2, p1) * Cross(q1, q2, p2) < 0;
    }

    /// <summary>Crop around the content, centred, with a small margin: square, or just the content's shape.</summary>
    internal static void Crop(View view, Rect2 extents, bool square = true)
    {
        double halfX = (square ? Math.Max(extents.Width, extents.Height) : extents.Width) * 0.55; // 10% margin overall
        double halfY = square ? halfX : extents.Height * 0.55;
        var (cx, cy) = extents.Center;
        var crop = view.CropBox;
        crop.Min = new XYZ(cx - halfX, cy - halfY, crop.Min.Z);
        crop.Max = new XYZ(cx + halfX, cy + halfY, crop.Max.Z);
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

    internal static IEnumerable<XYZ> Corners(BoundingBoxXYZ b) =>
        from x in new[] { b.Min.X, b.Max.X }
        from y in new[] { b.Min.Y, b.Max.Y }
        from z in new[] { b.Min.Z, b.Max.Z }
        select b.Transform.OfPoint(new XYZ(x, y, z));

    /// <summary>Axis-aligned rectangle in the view's crop coordinates (x = right, y = up).</summary>
    internal readonly record struct Rect2(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public (double X, double Y) Center => ((MinX + MaxX) / 2, (MinY + MaxY) / 2);

        public Rect2 Union(Rect2 o) =>
            new(Math.Min(MinX, o.MinX), Math.Min(MinY, o.MinY), Math.Max(MaxX, o.MaxX), Math.Max(MaxY, o.MaxY));

        public Rect2 MovedTo(double cx, double cy) =>
            new(cx - Width / 2, cy - Height / 2, cx + Width / 2, cy + Height / 2);
    }

    /// <summary>Projects model points into the view's crop frame, where the crop box itself is defined.</summary>
    internal sealed class ViewFrame(View view)
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
        public Rect2 Bounds(IEnumerable<Element> elements) => Project(elements.SelectMany(e =>
            e.Location is LocationCurve { Curve: { } c } ? [c.GetEndPoint(0), c.GetEndPoint(1)]
            : e.get_BoundingBox(null) is { } b ? Corners(b)
            : Enumerable.Empty<XYZ>()));

        /// <summary>
        /// On-screen rectangle of an annotation lying in the view plane. Its model bounding box is the
        /// axis-aligned box around that tilted rectangle, so half-extent e = a·|right| + b·|up|; solve for
        /// the half-width a and half-height b (least squares) instead of over-measuring the projected box.
        /// </summary>
        public Rect2 PlanarRect(BoundingBoxXYZ bb)
        {
            var (min, max) = (bb.Transform.OfPoint(bb.Min), bb.Transform.OfPoint(bb.Max));
            XYZ e = (max - min) / 2, r = Abs(_toModel.BasisX), u = Abs(_toModel.BasisY);
            double rr = r.DotProduct(r), uu = u.DotProduct(u), ru = r.DotProduct(u), re = r.DotProduct(e), ue = u.DotProduct(e);
            double det = rr * uu - ru * ru;
            if (det < 1e-9) return Project(Corners(bb));
            double a = (re * uu - ue * ru) / det, b = (ue * rr - re * ru) / det;
            if (a <= 0 || b <= 0) return Project(Corners(bb));
            var (x, y) = ToLocal((min + max) / 2);
            return new Rect2(x - a, y - b, x + a, y + b);
        }

        private static XYZ Abs(XYZ v) => new(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
    }

    /// <summary>The selection's 2D footprint: pipes as centreline segments padded by their radius, the rest as boxes.</summary>
    private sealed class Outline
    {
        private readonly List<((double X, double Y) A, (double X, double Y) B, double Pad)> _segments = [];
        private readonly List<Rect2> _boxes = [];

        public Outline(ViewFrame frame, IEnumerable<Element> elements)
        {
            foreach (var e in elements)
            {
                if (e is MEPCurve { Location: LocationCurve { Curve: { } c } } curve)
                    _segments.Add((frame.ToLocal(c.GetEndPoint(0)), frame.ToLocal(c.GetEndPoint(1)), curve.Diameter / 2));
                else if (e.get_BoundingBox(null) is { } b)
                    _boxes.Add(frame.Project(Corners(b)));
            }
        }

        /// <summary>Furthest x of the piping towards <paramref name="side"/> (+1 right, -1 left) inside the band y0..y1; null when the band is clear.</summary>
        public double? Reach(double y0, double y1, int side)
        {
            double? best = null;
            void Take(double x) => best = best is null ? x : side > 0 ? Math.Max(best.Value, x) : Math.Min(best.Value, x);

            foreach (var (a, b, pad) in _segments)
            {
                double lo = y0 - pad, hi = y1 + pad, dy = b.Y - a.Y;
                if (Math.Abs(dy) < 1e-9)
                {
                    if (a.Y >= lo && a.Y <= hi) { Take(a.X + side * pad); Take(b.X + side * pad); }
                    continue;
                }
                double t0 = Math.Max(0, Math.Min((lo - a.Y) / dy, (hi - a.Y) / dy));
                double t1 = Math.Min(1, Math.Max((lo - a.Y) / dy, (hi - a.Y) / dy));
                if (t0 > t1) continue;
                Take(a.X + (b.X - a.X) * t0 + side * pad);
                Take(a.X + (b.X - a.X) * t1 + side * pad);
            }
            foreach (var r in _boxes)
                if (r.MinY < y1 && r.MaxY > y0) Take(side > 0 ? r.MaxX : r.MinX);
            return best;
        }

        /// <summary>Length-weighted principal direction of the pipes on screen (the run's main axis).</summary>
        public (double X, double Y) MainDirection()
        {
            double xx = 0, xy = 0, yy = 0;
            foreach (var (a, b, _) in _segments)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y;
                (xx, xy, yy) = (xx + dx * dx, xy + dx * dy, yy + dy * dy);
            }
            double angle = Math.Atan2(2 * xy, xx - yy) / 2;
            return (Math.Cos(angle), Math.Sin(angle));
        }

        /// <summary>How many pipe centrelines the leader from <paramref name="a"/> to <paramref name="b"/> cuts across.</summary>
        public int Crossings((double X, double Y) a, (double X, double Y) b) => _segments.Count(s => Crosses(a, b, s.A, s.B));
    }
}
