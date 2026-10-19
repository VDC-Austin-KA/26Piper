using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;

namespace PipingSuite.Services;

/// <param name="Legs">Straight legs found; <see cref="Dimensions"/> holds one per leg that could be dimensioned.</param>
public sealed record DimensionResult(IReadOnlyList<Dimension> Dimensions, int Legs, IReadOnlyList<string> Notes);

/// <summary>
/// Spool-iso style dimensions: one chained dimension along each straight leg, to the centre of every fitting and
/// accessory on it (elbows, tees, olets, valves…) and to pipe ends that leave the set, so each string reads centre
/// to centre and adds up to the leg. A fitting is measured to its family's centre reference plane square to the
/// leg; a family without one (a 45° elbow on a rolled offset) is measured to the centreline of a pipe leaving it at
/// right angles, else to a short invisible line drawn through its centre. Fittings joined straight to each other
/// (a return bend, an elbow on a valve) make a leg of their own. A flanged joint is measured once, to its centre
/// (where the flanges meet), not to each flange; a flange on a valve is left to the valve's centre. Unions and
/// couplings aren't measured to.
/// Works in plans, sections and 3D views (a 3D view is locked first; each dimension lies in the plane of its leg
/// that faces the viewer most). The caller holds the transaction.
/// </summary>
public sealed class DimensionService(Document doc)
{
    private const double OnAxis = 0.05;            // ft: a fitting centre this close to a leg's centreline sits on it
    private const double SameStation = 1.0 / 192;  // ft (1/16"): stations closer than this are one
    private const double DimOffsetInches = 0.35;   // on paper, beyond the pipe's outside

    private static readonly HashSet<PartType> NotMeasured = [PartType.Union, PartType.PipeMechanicalCoupling];

    /// <param name="Min">Extent along <paramref name="Dir"/> from <paramref name="Origin"/>.</param>
    /// <param name="Key">An element on the leg, for its spool and for reporting.</param>
    private sealed record Run(ElementId Key, XYZ Origin, XYZ Dir, double Radius, List<MEPCurve> Pipes, double Min, double Max)
    {
        public double T(XYZ p) => (p - Origin).DotProduct(Dir);
        public XYZ At(double t) => Origin + Dir * t;
    }

    /// <param name="type">Linear dimension type to use; null for the document's default.</param>
    public DimensionResult Dimension(View view, IReadOnlyList<Element> elements, DimensionType? type = null)
    {
        var notes = new List<string>();
        var dims = new List<Dimension>();
        if (view is View3D { IsPerspective: true }) return new DimensionResult(dims, 0, ["Perspective views can't be dimensioned."]);
        if (view is View3D { IsLocked: false } v3)
        {
            v3.SaveOrientationAndLock();
            notes.Add("The 3D view was locked so it can hold dimensions.");
        }

        var piping = elements.Where(PipingElements.IsPiping).DistinctBy(e => e.Id).ToList();
        var ids = piping.Select(e => e.Id).ToHashSet();
        var parts = piping.OfType<FamilyInstance>().Where(Measured).ToList();
        var joints = Joints(piping.OfType<FamilyInstance>().Where(IsJoint).ToList(), ids);
        var centreOf = Centres(piping);
        var facing = view.ViewDirection.Negate(); // towards the model
        bool is3D = view is View3D;
        var geometry = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view };
        var lines = new Dictionary<ElementId, Line?>();
        Line? Centerline(MEPCurve p) => lines.TryGetValue(p.Id, out var l) ? l : lines[p.Id] = FindCenterline(p, geometry);
        double offset = DimOffsetInches / 12 * view.Scale;

        var runs = Runs(piping.OfType<MEPCurve>(), parts, ids);
        int edgeOn = 0, noReference = 0, helpers = 0;
        var invisible = Category.GetCategory(doc, BuiltInCategory.OST_InvisibleLines)?.GetGraphicsStyle(GraphicsStyleType.Projection);
        var refused = new List<string>();
        foreach (var run in runs)
        {
            double T(XYZ p) => run.T(p);

            // The dimension's plane: in a plan or section, the view's; in 3D, the one of the leg's sides that faces the viewer most.
            XYZ side;
            if (is3D) side = Perpendiculars(run.Dir).MaxBy(o => Math.Abs(run.Dir.CrossProduct(o).DotProduct(facing)))!;
            else if (Math.Abs(run.Dir.DotProduct(facing)) > 0.01) { edgeOn++; continue; } // runs towards the viewer: no length to show
            else side = facing.CrossProduct(run.Dir).Normalize();
            var normal = run.Dir.CrossProduct(side).Normalize();

            var stations = new List<(double T, Reference Ref)>();
            double reach = run.Radius * 6 + 0.25; // an elbow's centre lies beyond its pipes' ends
            SketchPlane? plane = null;
            SketchPlane Plane() => plane ??= SketchPlane.Create(doc, Autodesk.Revit.DB.Plane.CreateByNormalAndOrigin(normal, run.Origin));

            foreach (var fi in parts)
            {
                if (fi.Location is not LocationPoint { Point: var p }) continue;
                double t = T(p);
                if (t < run.Min - reach || t > run.Max + reach || run.At(t).DistanceTo(p) > run.Radius + OnAxis) continue;
                if (CentrePlane(fi, run.Dir) is { } centre) { stations.Add((t, centre)); continue; }

                // No centre plane: the centreline of a pipe leaving this fitting square to the leg, in the dimension's plane.
                var across = PipingElements.GetConnectedElements(fi).OfType<MEPCurve>()
                    .Where(m => m.Location is LocationCurve { Curve: Line l } && Math.Abs(l.Direction.DotProduct(run.Dir)) < 0.01
                                && Math.Abs(l.Direction.DotProduct(normal)) < 0.01)
                    .Select(m => (Pipe: m, Line: Centerline(m))).FirstOrDefault(x => x.Line?.Reference is not null);
                if (across.Line is { } line)
                {
                    stations.Add((T(line.Project(p)?.XYZPoint ?? p), line.Reference));
                    continue;
                }

                Mark(t);
            }

            // Flanged joints: one station where the flanges meet.
            foreach (var p in joints)
            {
                double t = T(p);
                if (t >= run.Min - reach && t <= run.Max + reach && run.At(t).DistanceTo(p) <= run.Radius + OnAxis) Mark(t);
            }

            // Nothing to measure to: a short invisible line through the point, square to the leg, in the dimension's plane.
            void Mark(double t)
            {
                try
                {
                    var half = side * Math.Max(run.Radius, 0.05);
                    var mark = doc.Create.NewModelCurve(Line.CreateBound(run.At(t) - half, run.At(t) + half), Plane());
                    if (invisible is not null) mark.LineStyle = invisible;
                    stations.Add((t, mark.GeometryCurve.Reference));
                    helpers++;
                }
                catch (Exception)
                {
                    noReference++;
                }
            }

            // Pipe ends that leave the set (or end open): the end of the pipe's centreline.
            foreach (var pipe in run.Pipes)
            foreach (var c in Ends(pipe).Where(c => !c.AllRefs.Cast<Connector>().Any(r => r.Owner is { } o && o.Id != pipe.Id && ids.Contains(o.Id))))
            {
                if (Centerline(pipe) is not { } line) { noReference++; continue; }
                int i = line.GetEndPoint(0).DistanceTo(c.Origin) <= line.GetEndPoint(1).DistanceTo(c.Origin) ? 0 : 1;
                if (line.GetEndPointReference(i) is { } r) stations.Add((T(c.Origin), r));
                else noReference++;
            }

            stations = stations.OrderBy(s => s.T).Aggregate(new List<(double T, Reference Ref)>(), (kept, s) =>
            {
                if (kept.Count == 0 || s.T - kept[^1].T > SameStation) kept.Add(s);
                return kept;
            });
            if (stations.Count < 2) continue;

            // Outside the piping: away from the middle of the spool this leg belongs to.
            var mid = run.At((stations[0].T + stations[^1].T) / 2);
            if ((mid - centreOf[run.Key]).DotProduct(side) < 0) side = side.Negate();
            var shift = side * (run.Radius + offset);

            try
            {
                if (is3D) view.SketchPlane = Plane();
                var refs = new ReferenceArray();
                foreach (var s in stations) refs.Append(s.Ref);
                var at = Line.CreateBound(run.At(stations[0].T) + shift, run.At(stations[^1].T) + shift);
                if ((type is null ? doc.Create.NewDimension(view, at, refs) : doc.Create.NewDimension(view, at, refs, type)) is { } dim) dims.Add(dim);
                else refused.Add($"[{run.Key.Value}] Revit returned no dimension");
            }
            catch (Exception ex)
            {
                refused.Add($"[{run.Key.Value}] {ex.Message}");
            }
        }

        if (edgeOn > 0) notes.Add($"{edgeOn} leg(s) run towards the viewer in this view, so they have no length to dimension here.");
        if (helpers > 0) notes.Add($"{helpers} flanged joint(s) or fitting centre(s) without a centre reference plane square to their leg got an invisible reference line to measure to.");
        if (noReference > 0) notes.Add($"{noReference} fitting(s) or pipe end(s) had no centre reference or centreline to dimension to.");
        if (refused.Count > 0)
            notes.Add($"Revit refused {refused.Count} leg(s) (element id, reason): " + string.Join("; ", refused.Take(5)) + (refused.Count > 5 ? " …" : string.Empty));
        return new DimensionResult(dims, runs.Count, notes);
    }

    /// <summary>
    /// Straight legs: pipes grouped by the centreline they run along, plus measured fittings joined straight to each
    /// other with no pipe between (through flanges, unions and couplings), merged with a leg on the same centreline.
    /// </summary>
    private static List<Run> Runs(IEnumerable<MEPCurve> pipes, IReadOnlyList<FamilyInstance> parts, HashSet<ElementId> ids)
    {
        var runs = new List<Run>();
        void Add(ElementId key, XYZ a, XYZ b, double radius, MEPCurve? pipe)
        {
            if (a.DistanceTo(b) < SameStation) return;
            var dir = (b - a).Normalize();
            int i = runs.FindIndex(r => Math.Abs(r.Dir.DotProduct(dir)) > 0.9999 && (a - r.Origin).CrossProduct(r.Dir).GetLength() < OnAxis);
            if (i < 0)
            {
                runs.Add(new Run(key, a, dir, radius, pipe is null ? [] : [pipe], 0, a.DistanceTo(b)));
                return;
            }
            var r = runs[i];
            if (pipe is not null) r.Pipes.Add(pipe);
            runs[i] = r with
            {
                Radius = Math.Max(r.Radius, radius),
                Min = Math.Min(r.Min, Math.Min(r.T(a), r.T(b))),
                Max = Math.Max(r.Max, Math.Max(r.T(a), r.T(b))),
            };
        }

        foreach (var pipe in pipes)
            if (pipe.Location is LocationCurve { Curve: Line line })
                Add(pipe.Id, line.GetEndPoint(0), line.GetEndPoint(1), Ends(pipe).Select(Radius).DefaultIfEmpty(0).Max(), pipe);

        var measured = parts.ToDictionary(p => p.Id);
        foreach (var fi in parts)
        {
            if (fi.Location is not LocationPoint { Point: var from }) continue;
            foreach (var c in Ends(fi).Where(c => c.IsConnected))
            {
                // Walk through flanges, unions and couplings; stop at a pipe (its leg is found above) or a measured fitting.
                var at = c;
                for (int hops = 0; hops < 10; hops++)
                {
                    var next = at.AllRefs.Cast<Connector>().FirstOrDefault(r => r.ConnectorType == ConnectorType.End && r.Owner is { } o && o.Id != at.Owner.Id && ids.Contains(o.Id));
                    if (next is null || next.Owner is MEPCurve) break;
                    if (measured.TryGetValue(next.Owner.Id, out var to))
                    {
                        if (fi.Id.Value < to.Id.Value && to.Location is LocationPoint { Point: var end }) Add(fi.Id, from, end, Radius(c), null);
                        break;
                    }
                    var ends = Ends(next.Owner).ToList();
                    if (ends.Count != 2) break;
                    at = ends.First(e => e.Id != next.Id);
                }
            }
        }
        return runs;
    }

    /// <summary>Middle of each element's connected spool, so dimensions go to the outside of the spool they belong to.</summary>
    private static Dictionary<ElementId, XYZ> Centres(IReadOnlyList<Element> piping)
    {
        var byId = piping.ToDictionary(e => e.Id);
        var uf = new UnionFind(byId.Keys);
        foreach (var e in piping)
        foreach (var n in PipingElements.GetConnectedElements(e).Where(n => byId.ContainsKey(n.Id)))
            uf.Union(e.Id, n.Id);

        var centres = new Dictionary<ElementId, XYZ>();
        foreach (var g in byId.Keys.GroupBy(uf.Find))
        {
            var boxes = g.Select(id => byId[id].get_BoundingBox(null)).OfType<BoundingBoxXYZ>().ToList();
            var c = boxes.Count == 0 ? XYZ.Zero : boxes.Aggregate(XYZ.Zero, (s, b) => s + (b.Min + b.Max) / 2) / boxes.Count;
            foreach (var id in g) centres[id] = c;
        }
        return centres;
    }

    /// <summary>A family's centre reference plane square to <paramref name="dir"/> (left/right, front/back or elevation).</summary>
    private static Reference? CentrePlane(FamilyInstance fi, XYZ dir)
    {
        var t = fi.GetTransform();
        return new[]
            {
                (FamilyInstanceReferenceType.CenterLeftRight, t.BasisX),
                (FamilyInstanceReferenceType.CenterFrontBack, t.BasisY),
                (FamilyInstanceReferenceType.CenterElevation, t.BasisZ),
            }
            .Where(c => Math.Abs(c.Item2.Normalize().DotProduct(dir)) > 0.999)
            .Select(c => fi.GetReferences(c.Item1).FirstOrDefault())
            .FirstOrDefault(r => r is not null);
    }

    /// <summary>The pipe's centreline as Revit draws it, which carries the line and end point references; coarse geometry if the view hides it.</summary>
    private static Line? FindCenterline(Element pipe, Options inView) =>
        Find(pipe.get_Geometry(inView))
        ?? Find(pipe.get_Geometry(new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, DetailLevel = ViewDetailLevel.Coarse }));

    private static Line? Find(GeometryElement? g) => g?.OfType<Line>().FirstOrDefault(l => l.Reference is not null);

    private static IEnumerable<XYZ> Perpendiculars(XYZ dir)
    {
        foreach (var axis in new[] { XYZ.BasisZ, XYZ.BasisX, XYZ.BasisY })
        {
            var o = axis - dir * axis.DotProduct(dir);
            if (o.GetLength() > 0.1) yield return o.Normalize();
        }
        var flat = dir.CrossProduct(XYZ.BasisZ);
        if (flat.GetLength() > 0.1) yield return flat.Normalize();
    }

    /// <summary>Fittings and accessories measured to their centre: not flanges, gaskets, unions or couplings.</summary>
    private static bool Measured(FamilyInstance fi) =>
        !IsJoint(fi) && (fi.MEPModel is not Autodesk.Revit.DB.Mechanical.MechanicalFitting { } f || !NotMeasured.Contains(f.PartType));

    /// <summary>A flange or gasket: by part type, or by family or type name (many flange families report another part type).</summary>
    private static bool IsJoint(FamilyInstance fi) =>
        fi.MEPModel is Autodesk.Revit.DB.Mechanical.MechanicalFitting { PartType: PartType.PipeFlange }
        || new[] { fi.Symbol?.FamilyName, fi.Symbol?.Name }.Any(n => n is not null
            && (n.Contains("flange", StringComparison.OrdinalIgnoreCase) || n.Contains("gasket", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Where each flanged joint's flanges meet: mated flanges (and any gasket between) are one joint, centred between
    /// its outer ends. A lone flange is measured at its face when that faces equipment or leaves the set. A joint
    /// against a valve or other measured fitting is left out: that fitting's centre is dimensioned instead.
    /// </summary>
    private static List<XYZ> Joints(IReadOnlyList<FamilyInstance> flanges, HashSet<ElementId> ids)
    {
        var byId = flanges.ToDictionary(f => f.Id);
        var uf = new UnionFind(byId.Keys);
        foreach (var f in flanges)
        foreach (var n in PipingElements.GetConnectedElements(f).Where(n => byId.ContainsKey(n.Id)))
            uf.Union(f.Id, n.Id);

        var points = new List<XYZ>();
        foreach (var g in byId.Keys.GroupBy(uf.Find).Select(g => g.Select(id => byId[id]).ToList()))
        {
            var inside = g.Select(f => f.Id).ToHashSet();
            var outer = g.SelectMany(Ends)
                .Select(c => (c, Next: c.AllRefs.Cast<Connector>().FirstOrDefault(r => r.Owner is { } o && o.Id != c.Owner.Id && o is not MEPSystem)?.Owner))
                .Where(x => x.Next is null || !inside.Contains(x.Next.Id))
                .ToList();
            if (outer.Any(x => x.Next is FamilyInstance fi && ids.Contains(fi.Id) && PipingElements.IsPiping(fi) && Measured(fi))) continue;

            if (g.Count > 1 && outer.Count == 2) points.Add((outer[0].c.Origin + outer[1].c.Origin) / 2);
            else if (g.Count == 1 && outer.FirstOrDefault(x => x.Next is null || !ids.Contains(x.Next.Id)) is { c: not null } face) points.Add(face.c.Origin);
        }
        return points;
    }

    private static double Radius(Connector c) => c.Shape == ConnectorProfileType.Round ? c.Radius : 0;

    private static IEnumerable<Connector> Ends(Element e) =>
        PipingElements.GetConnectorManager(e)?.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType == ConnectorType.End && c.Domain == Domain.DomainPiping) ?? [];
}
