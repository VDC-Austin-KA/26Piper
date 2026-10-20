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
/// leg; a family without one is measured to the centreline of the pipe leaving it at right angles, which runs
/// through the same centre. Works in plans, sections and 3D views (a 3D view is locked first; each dimension lies
/// in the plane of its leg that faces the viewer most). The caller holds the transaction.
/// </summary>
public sealed class DimensionService(Document doc)
{
    private const double OnAxis = 0.05;            // ft: a fitting centre this close to a leg's centreline sits on it
    private const double SameStation = 1.0 / 192;  // ft (1/16"): stations closer than this are one
    private const double DimOffsetInches = 0.35;   // on paper, beyond the pipe's outside

    private sealed record Run(XYZ Origin, XYZ Dir, double Radius, List<MEPCurve> Pipes);

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
        var parts = piping.OfType<FamilyInstance>().ToList();
        var centreOf = Centres(piping);
        var facing = view.ViewDirection.Negate(); // towards the model
        bool is3D = view is View3D;
        var geometry = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view };
        var lines = new Dictionary<ElementId, Line?>();
        Line? Centerline(MEPCurve p) => lines.TryGetValue(p.Id, out var l) ? l : lines[p.Id] = FindCenterline(p, geometry);
        double offset = DimOffsetInches / 12 * view.Scale;

        var runs = Runs(piping.OfType<MEPCurve>());
        int edgeOn = 0, noReference = 0;
        var refused = new List<string>();
        foreach (var run in runs)
        {
            double T(XYZ p) => (p - run.Origin).DotProduct(run.Dir);

            // The dimension's plane: in a plan or section, the view's; in 3D, the one of the leg's sides that faces the viewer most.
            XYZ side;
            if (is3D) side = Perpendiculars(run.Dir).MaxBy(o => Math.Abs(run.Dir.CrossProduct(o).DotProduct(facing)))!;
            else if (Math.Abs(run.Dir.DotProduct(facing)) > 0.01) { edgeOn++; continue; } // runs towards the viewer: no length to show
            else side = facing.CrossProduct(run.Dir).Normalize();
            var normal = run.Dir.CrossProduct(side).Normalize();

            var stations = new List<(double T, Reference Ref)>();
            double min = run.Pipes.Min(p => Math.Min(T(End(p, 0)), T(End(p, 1)))), max = run.Pipes.Max(p => Math.Max(T(End(p, 0)), T(End(p, 1))));
            double reach = run.Radius * 6 + 0.25; // an elbow's centre lies beyond its pipes' ends

            foreach (var fi in parts)
            {
                if (fi.Location is not LocationPoint { Point: var p }) continue;
                double t = T(p);
                if (t < min - reach || t > max + reach || (run.Origin + run.Dir * t).DistanceTo(p) > run.Radius + OnAxis) continue;
                if (CentrePlane(fi, run.Dir) is { } plane) { stations.Add((t, plane)); continue; }

                // No centre plane: the centreline of a pipe leaving this fitting square to the leg, in the dimension's plane.
                var across = PipingElements.GetConnectedElements(fi).OfType<MEPCurve>()
                    .Where(m => m.Location is LocationCurve { Curve: Line l } && Math.Abs(l.Direction.DotProduct(run.Dir)) < 0.01
                                && Math.Abs(l.Direction.DotProduct(normal)) < 0.01)
                    .Select(m => (Pipe: m, Line: Centerline(m))).FirstOrDefault(x => x.Line?.Reference is not null);
                if (across.Line is { } line)
                    stations.Add((T(line.Project(p)?.XYZPoint ?? p), line.Reference));
                else noReference++;
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
            var mid = run.Origin + run.Dir * ((stations[0].T + stations[^1].T) / 2);
            if ((mid - centreOf[run.Pipes[0].Id]).DotProduct(side) < 0) side = side.Negate();
            var shift = side * (run.Radius + offset);

            try
            {
                if (is3D) view.SketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal, run.Origin));
                var refs = new ReferenceArray();
                foreach (var s in stations) refs.Append(s.Ref);
                var at = Line.CreateBound(run.Origin + run.Dir * stations[0].T + shift, run.Origin + run.Dir * stations[^1].T + shift);
                if ((type is null ? doc.Create.NewDimension(view, at, refs) : doc.Create.NewDimension(view, at, refs, type)) is { } dim) dims.Add(dim);
                else refused.Add($"[{run.Pipes[0].Id.Value}] Revit returned no dimension");
            }
            catch (Exception ex)
            {
                refused.Add($"[{run.Pipes[0].Id.Value}] {ex.Message}");
            }
        }

        if (edgeOn > 0) notes.Add($"{edgeOn} leg(s) run towards the viewer in this view, so they have no length to dimension here.");
        if (noReference > 0) notes.Add($"{noReference} fitting(s) or pipe end(s) had no centre reference or centreline to dimension to.");
        if (refused.Count > 0)
            notes.Add($"Revit refused {refused.Count} leg(s) (pipe id, reason): " + string.Join("; ", refused.Take(5)) + (refused.Count > 5 ? " …" : string.Empty));
        return new DimensionResult(dims, runs.Count, notes);
    }

    /// <summary>Straight pipes grouped by the centreline they run along.</summary>
    private static List<Run> Runs(IEnumerable<MEPCurve> pipes)
    {
        var runs = new List<Run>();
        foreach (var pipe in pipes)
        {
            if (pipe.Location is not LocationCurve { Curve: Line line }) continue;
            var dir = line.Direction.Normalize();
            var radius = Ends(pipe).Select(c => c.Shape == ConnectorProfileType.Round ? c.Radius : 0).DefaultIfEmpty(0).Max();
            int i = runs.FindIndex(r => Math.Abs(r.Dir.DotProduct(dir)) > 0.9999
                                        && (line.GetEndPoint(0) - r.Origin).CrossProduct(r.Dir).GetLength() < OnAxis);
            if (i < 0) runs.Add(new Run(line.GetEndPoint(0), dir, radius, [pipe]));
            else
            {
                runs[i].Pipes.Add(pipe);
                if (radius > runs[i].Radius) runs[i] = runs[i] with { Radius = radius };
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

    private static XYZ End(MEPCurve pipe, int i) => ((LocationCurve)pipe.Location).Curve.GetEndPoint(i);

    private static IEnumerable<Connector> Ends(Element e) =>
        PipingElements.GetConnectorManager(e)?.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType == ConnectorType.End && c.Domain == Domain.DomainPiping) ?? [];
}
