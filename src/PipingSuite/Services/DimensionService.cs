using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;

namespace PipingSuite.Services;

/// <param name="Dimensions">One per segment: fitting centre to fitting centre, or to a pipe end.</param>
/// <param name="Legs">Straight legs found; <paramref name="Dimensioned"/> of them got dimensions.</param>
public sealed record DimensionResult(IReadOnlyList<Dimension> Dimensions, int Legs, int Dimensioned, IReadOnlyList<string> Notes);

/// <summary>
/// Spool-iso style dimensions along each straight leg, to the centre of every fitting and accessory on it (elbows,
/// tees, olets, valves…) and to pipe ends that leave the set: one dimension per segment, centre to centre, so each
/// can be moved on its own. Segments whose text would collide are stepped out onto further rows. Pipe ends are
/// measured to the pipe's end face (its centreline end in coarse views). A fitting is measured to its family's centre reference plane square to the
/// leg; a family without one is measured to the centreline of the pipe leaving it at right angles, which runs
/// through the same centre; failing that, to a short invisible line drawn through the centre. Fittings and
/// accessories bolted straight on in line (a valve on an elbow) are on the leg too. Small takeoffs (a 1/4" vent off
/// a 2" line) aren't dimensioned along themselves: the line they come off is dimensioned to their centre.
/// Works in plans, sections and 3D views (a 3D view is locked first; each dimension lies in the plane of its leg
/// that faces the viewer most). The caller holds the transaction.
/// </summary>
public sealed class DimensionService(Document doc)
{
    private const double OnAxis = 0.05;            // ft: a fitting centre this close to a leg's centreline sits on it
    private const double SameStation = 1.0 / 192;  // ft (1/16"): stations closer than this are one
    private const double DimOffsetInches = 0.35;   // on paper, beyond the pipe's outside
    private const double SmallTakeoff = 1.0 / 12;  // ft: a line this size or smaller, under half its spool's main size, isn't dimensioned along itself

    private sealed record Run(XYZ Origin, XYZ Dir, double Radius, List<MEPCurve> Pipes);

    /// <param name="type">Linear dimension type to use; null for the document's default.</param>
    public DimensionResult Dimension(View view, IReadOnlyList<Element> elements, DimensionType? type = null)
    {
        var notes = new List<string>();
        var dims = new List<Dimension>();
        if (view is View3D { IsPerspective: true }) return new DimensionResult(dims, 0, 0, ["Perspective views can't be dimensioned."]);
        if (view is View3D { IsLocked: false } v3)
        {
            v3.SaveOrientationAndLock();
            notes.Add("The 3D view was locked so it can hold dimensions.");
        }

        var piping = elements.Where(PipingElements.IsPiping).DistinctBy(e => e.Id).ToList();
        var ids = piping.Select(e => e.Id).ToHashSet();
        var parts = piping.OfType<FamilyInstance>().ToList();
        var (centreOf, mainRadius) = Centres(piping);
        var partIds = parts.Select(p => p.Id).ToHashSet();
        var facing = view.ViewDirection.Negate(); // towards the model
        bool is3D = view is View3D;
        var geometry = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view };
        var lines = new Dictionary<ElementId, Line?>();
        Line? Centerline(MEPCurve p) => lines.TryGetValue(p.Id, out var l) ? l : lines[p.Id] = FindCenterline(p, geometry);
        double offset = DimOffsetInches / 12 * view.Scale;
        // Rows: a dimension's text is about seven characters wide; a row further out is 2.5 text heights clear.
        double text = ((type ?? doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.LinearDimensionType)) as DimensionType)
            ?.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 3.0 / 32 / 12) * view.Scale;
        int dimensioned = 0;
        var repaired = new List<string>(); // references Revit wouldn't dimension to, swapped for an invisible line

        var runs = Runs(piping.OfType<MEPCurve>());
        int edgeOn = 0, small = 0, helpers = 0;
        var invisible = Category.GetCategory(doc, BuiltInCategory.OST_InvisibleLines)?.GetGraphicsStyle(GraphicsStyleType.Projection);
        var refused = new List<string>();
        var missing = new List<string>();  // fittings and pipe ends with nothing to measure to
        var rejected = new List<string>(); // references Revit wouldn't dimension to, left out of their leg
        foreach (var run in runs)
        {
            double T(XYZ p) => (p - run.Origin).DotProduct(run.Dir);
            bool OnAxis(XYZ p) => (run.Origin + run.Dir * T(p)).DistanceTo(p) <= run.Radius + DimensionService.OnAxis;

            // A small takeoff is dimensioned only where it leaves its line (that line's string runs to its centre).
            if (run.Radius * 2 <= SmallTakeoff + 1e-6 && run.Radius * 2 < mainRadius[run.Pipes[0].Id]) { small++; continue; }

            // The dimension's plane: in a plan or section, the view's; in 3D, the one of the leg's sides that faces the viewer most.
            XYZ side;
            if (is3D) side = Perpendiculars(run.Dir).MaxBy(o => Math.Abs(run.Dir.CrossProduct(o).DotProduct(facing)))!;
            else if (Math.Abs(run.Dir.DotProduct(facing)) > 0.01) { edgeOn++; continue; } // runs towards the viewer: no length to show
            else side = facing.CrossProduct(run.Dir).Normalize();
            var normal = run.Dir.CrossProduct(side).Normalize();

            var stations = new List<(double T, Reference Ref, string What)>();
            double min = run.Pipes.Min(p => Math.Min(T(End(p, 0)), T(End(p, 1)))), max = run.Pipes.Max(p => Math.Max(T(End(p, 0)), T(End(p, 1))));
            double reach = run.Radius * 6 + 0.25; // an elbow's centre lies beyond its pipes' ends
            SketchPlane? sketch = null;
            SketchPlane Sketch() => sketch ??= SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(normal, run.Origin));

            // A short invisible line through the leg at t, square to it, in the dimension's plane: something to measure to.
            Reference? Mark(double t)
            {
                try
                {
                    var half = side * Math.Max(run.Radius, 0.05);
                    var centre = run.Origin + run.Dir * t;
                    var mark = doc.Create.NewModelCurve(Line.CreateBound(centre - half, centre + half), Sketch());
                    if (invisible is not null) mark.LineStyle = invisible;
                    helpers++;
                    return mark.GeometryCurve.Reference;
                }
                catch (Exception)
                {
                    return null;
                }
            }

            // On the leg: fittings and accessories centred on its axis that its pipes reach through connections, one
            // after another (a valve bolted to an elbow, an olet on the pipe), or that sit just past its pipes' ends.
            var onLeg = new HashSet<ElementId>();
            var queue = new Queue<Element>(run.Pipes);
            while (queue.Count > 0)
                foreach (var n in PipingElements.GetConnectedElements(queue.Dequeue()))
                    if (partIds.Contains(n.Id) && n.Location is LocationPoint { Point: var np } && OnAxis(np) && onLeg.Add(n.Id)) queue.Enqueue(n);

            foreach (var fi in parts)
            {
                if (fi.Location is not LocationPoint { Point: var p }) continue;
                double t = T(p);
                if (!onLeg.Contains(fi.Id) && (t < min - reach || t > max + reach || !OnAxis(p))) continue;
                var name = $"[{fi.Id.Value}] {fi.Symbol?.FamilyName}";
                if (CentrePlane(fi, run.Dir) is { } plane) { stations.Add((t, plane, $"{name} centre plane")); continue; }

                // No centre plane: the centreline of a pipe leaving this fitting square to the leg, in the dimension's plane.
                var across = PipingElements.GetConnectedElements(fi).OfType<MEPCurve>()
                    .Where(m => m.Location is LocationCurve { Curve: Line l } && Math.Abs(l.Direction.DotProduct(run.Dir)) < 0.01
                                && Math.Abs(l.Direction.DotProduct(normal)) < 0.01)
                    .Select(m => (Pipe: m, Line: Centerline(m))).FirstOrDefault(x => x.Line?.Reference is not null);
                if (across.Line is { } line)
                {
                    stations.Add((T(line.Project(p)?.XYZPoint ?? p), line.Reference, $"{name} centre, via pipe [{across.Pipe.Id.Value}]"));
                    continue;
                }

                // Nothing to measure to: an invisible line through the centre.
                if (Mark(t) is { } mark) stations.Add((t, mark, $"{name} centre, via an invisible line"));
                else missing.Add(name);
            }

            // Pipe ends that leave the set (or end open): the pipe's end face, else the end of its centreline.
            foreach (var pipe in run.Pipes)
            foreach (var c in Ends(pipe).Where(c => !c.AllRefs.Cast<Connector>().Any(r => r.Owner is { } o && o.Id != pipe.Id && ids.Contains(o.Id))))
            {
                var r = EndFace(pipe, c.Origin, run.Dir, geometry);
                if (r is null && Centerline(pipe) is { } line)
                    r = line.GetEndPointReference(line.GetEndPoint(0).DistanceTo(c.Origin) <= line.GetEndPoint(1).DistanceTo(c.Origin) ? 0 : 1);
                if ((r ?? Mark(T(c.Origin))) is { } end) stations.Add((T(c.Origin), end, $"[{pipe.Id.Value}] pipe end"));
                else missing.Add($"[{pipe.Id.Value}] pipe end");
            }

            stations = stations.OrderBy(s => s.T).Aggregate(new List<(double T, Reference Ref, string What)>(), (kept, s) =>
            {
                if (kept.Count == 0 || s.T - kept[^1].T > SameStation) kept.Add(s);
                return kept;
            });
            if (stations.Count < 2) continue;

            // Outside the piping: away from the middle of the spool this leg belongs to.
            var mid = run.Origin + run.Dir * ((stations[0].T + stations[^1].T) / 2);
            if ((mid - centreOf[run.Pipes[0].Id]).DotProduct(side) < 0) side = side.Negate();
            Line Along(double a, double b, int row)
            {
                var shift = side * (run.Radius + offset + row * 2.5 * text);
                return Line.CreateBound(run.Origin + run.Dir * a + shift, run.Origin + run.Dir * b + shift);
            }

            // Revit won't measure to some references ("not geometric references"): find them by trying each against
            // the others, and measure to an invisible line in the same place instead.
            if (is3D) view.SketchPlane = Sketch();
            var probe = Along(stations[0].T, stations[^1].T, 0);
            if (!Works(view, probe, stations.Select(s => s.Ref), type))
            {
                var good = new bool[stations.Count];
                for (int i = 0; i < stations.Count; i++)
                for (int j = i + 1; j < stations.Count; j++)
                    if (!(good[i] && good[j]) && Works(view, probe, [stations[i].Ref, stations[j].Ref], type)) good[i] = good[j] = true;
                for (int i = stations.Count - 1; i >= 0; i--)
                {
                    if (good[i]) continue;
                    if (Mark(stations[i].T) is { } mark)
                    {
                        repaired.Add(stations[i].What);
                        stations[i] = (stations[i].T, mark, stations[i].What + ", via an invisible line");
                    }
                    else
                    {
                        rejected.Add(stations[i].What);
                        stations.RemoveAt(i);
                    }
                }
                if (stations.Count < 2) continue;
            }

            // One dimension per segment. A segment too short for its text, or whose text would run into a
            // neighbour's, steps out to the first row where it's clear.
            var rows = new List<List<(double From, double To)>>();
            var failed = new List<string>();
            int placed = 0;
            for (int i = 0; i + 1 < stations.Count; i++)
            {
                var (a, b) = (stations[i], stations[i + 1]);
                double centre = (a.T + b.T) / 2, half = Math.Max(b.T - a.T, 7 * text) / 2;
                int row = rows.FindIndex(r => !r.Any(x => x.From < centre + half && centre - half < x.To));
                if (row < 0) { rows.Add([]); row = rows.Count - 1; }
                rows[row].Add((centre - half, centre + half));

                var (dim, why) = TryCreate(view, Along(a.T, b.T, row), [a.Ref, b.Ref], type);
                if (dim is not null) { dims.Add(dim); placed++; }
                else failed.Add($"{a.What} to {b.What}: {why}");
            }
            if (placed > 0) dimensioned++;
            if (failed.Count > 0) refused.Add($"[{run.Pipes[0].Id.Value}] " + string.Join("; ", failed));
        }

        if (edgeOn > 0) notes.Add($"{edgeOn} leg(s) run towards the viewer in this view, so they have no length to dimension here.");
        if (small > 0) notes.Add($"{small} small takeoff leg(s) (1\" and under, off a line over twice their size) were dimensioned only to their centre on the line they come off.");
        if (helpers > 0) notes.Add($"{helpers} invisible line(s) were drawn to measure to, at centres and ends with nothing else Revit would dimension to.");
        if (missing.Count > 0)
            notes.Add($"{missing.Count} fitting(s) or pipe end(s) had no centre reference or centreline to dimension to: " + string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? " …" : string.Empty));
        if (repaired.Count > 0)
            notes.Add($"{repaired.Count} reference(s) Revit wouldn't dimension to were measured to an invisible line in the same place: " + string.Join(", ", repaired.Take(8)) + (repaired.Count > 8 ? " …" : string.Empty));
        if (rejected.Count > 0)
            notes.Add($"Left {rejected.Count} reference(s) Revit wouldn't dimension to out of their leg: " + string.Join(", ", rejected.Take(8)) + (rejected.Count > 8 ? " …" : string.Empty));
        if (refused.Count > 0)
            notes.Add($"Revit refused segment(s) on {refused.Count} leg(s) (pipe id, reason): " + string.Join("; ", refused.Take(5)) + (refused.Count > 5 ? " …" : string.Empty));
        return new DimensionResult(dims, runs.Count, dimensioned, notes);
    }

    private (Dimension? Dim, string Why) TryCreate(View view, Line at, IEnumerable<Reference> refs, DimensionType? type)
    {
        var array = new ReferenceArray();
        foreach (var r in refs) array.Append(r);
        try
        {
            var dim = type is null ? doc.Create.NewDimension(view, at, array) : doc.Create.NewDimension(view, at, array, type);
            return (dim, dim is null ? "Revit returned no dimension" : string.Empty);
        }
        catch (Exception ex)
        {
            return (null, ex.Message.Split('\n')[0].Trim());
        }
    }

    /// <summary>Whether Revit will dimension between these references; the trial dimension is rolled back.</summary>
    private bool Works(View view, Line at, IEnumerable<Reference> refs, DimensionType? type)
    {
        using var trial = new SubTransaction(doc);
        trial.Start();
        try { return TryCreate(view, at, refs, type).Dim is not null; }
        finally { trial.RollBack(); }
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
    /// <returns>Each element's spool middle, and the radius of the biggest pipe in its spool.</returns>
    private static (Dictionary<ElementId, XYZ> Centres, Dictionary<ElementId, double> MainRadius) Centres(IReadOnlyList<Element> piping)
    {
        var byId = piping.ToDictionary(e => e.Id);
        var uf = new UnionFind(byId.Keys);
        foreach (var e in piping)
        foreach (var n in PipingElements.GetConnectedElements(e).Where(n => byId.ContainsKey(n.Id)))
            uf.Union(e.Id, n.Id);

        var centres = new Dictionary<ElementId, XYZ>();
        var main = new Dictionary<ElementId, double>();
        foreach (var g in byId.Keys.GroupBy(uf.Find))
        {
            var boxes = g.Select(id => byId[id].get_BoundingBox(null)).OfType<BoundingBoxXYZ>().ToList();
            var c = boxes.Count == 0 ? XYZ.Zero : boxes.Aggregate(XYZ.Zero, (s, b) => s + (b.Min + b.Max) / 2) / boxes.Count;
            var radius = g.Select(id => byId[id]).OfType<MEPCurve>().SelectMany(Ends).Select(e => e.Shape == ConnectorProfileType.Round ? e.Radius : 0).DefaultIfEmpty(0).Max();
            foreach (var id in g) (centres[id], main[id]) = (c, radius);
        }
        return (centres, main);
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

    /// <summary>The pipe's flat end face at <paramref name="end"/>, square to its run: what a dimension to the end of a solid pipe measures to.</summary>
    private static Reference? EndFace(Element pipe, XYZ end, XYZ dir, Options inView) =>
        pipe.get_Geometry(inView)?.OfType<Solid>().SelectMany(s => s.Faces.Cast<Face>()).OfType<PlanarFace>()
            .FirstOrDefault(f => f.Reference is not null && Math.Abs(f.FaceNormal.DotProduct(dir)) > 0.999
                                 && Math.Abs((end - f.Origin).DotProduct(f.FaceNormal)) < 0.01)?.Reference;

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
