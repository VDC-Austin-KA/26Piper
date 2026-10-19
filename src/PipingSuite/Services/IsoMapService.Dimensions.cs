using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;

namespace PipingSuite.Services;

public sealed partial class IsoMapService
{
    private const double OnAxis = 0.05;      // ft: a fitting centre this close to a leg's centreline sits on it
    private const double SameStation = 1.0 / 192; // ft (1/16"): stations closer than this are one
    private const double DimOffsetInches = 0.35;  // on paper, beyond the pipe's outside

    private sealed record Run(XYZ Origin, XYZ Dir, double Radius, List<MEPCurve> Pipes);

    /// <summary>
    /// One chained dimension along each straight leg: to the centre of every fitting and accessory on it
    /// (elbows, tees, valves, olets…) and to pipe ends that run out of the selection, so the strings read
    /// centre-to-centre and add up to the pipe lengths. Each dimension lies in the plane of its leg that
    /// faces the viewer most, offset to the outside of the piping. Needs the locked view.
    /// </summary>
    /// <returns>The dimensions' rectangles in the view's crop frame.</returns>
    private List<Rect2> Dimensions(View3D view, ViewFrame frame, IReadOnlyList<Element> elements, List<string> warnings)
    {
        var ids = elements.Select(e => e.Id).ToHashSet();
        var parts = elements.OfType<FamilyInstance>().Where(PipingElements.IsPiping).ToList();
        var centre = elements.Select(e => e.get_BoundingBox(null)).OfType<BoundingBoxXYZ>()
            .Aggregate(XYZ.Zero, (sum, b) => sum + (b.Min + b.Max) / 2) / Math.Max(1, elements.Count);
        var facing = view.GetOrientation().ForwardDirection;
        var geometry = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view };
        double offset = DimOffsetInches / 12 * view.Scale;

        var rects = new List<Rect2>();
        int failed = 0, noReference = 0;
        foreach (var leg in Legs(elements.OfType<MEPCurve>()))
        {
            var stations = new List<(double T, Reference Ref)>();
            double T(XYZ p) => (p - leg.Origin).DotProduct(leg.Dir);
            double reach = leg.Radius * 6 + 0.25;
            double min = leg.Pipes.Min(p => Math.Min(T(End(p, 0)), T(End(p, 1)))), max = leg.Pipes.Max(p => Math.Max(T(End(p, 0)), T(End(p, 1))));

            // Fittings and accessories centred on the leg: their centre reference plane square to it.
            foreach (var fi in parts)
            {
                if (fi.Location is not LocationPoint { Point: var p }) continue;
                double t = T(p);
                if (t < min - reach || t > max + reach) continue; // an elbow's centre lies beyond its pipes' ends
                if ((leg.Origin + leg.Dir * t).DistanceTo(p) > leg.Radius + OnAxis) continue;
                if (CentrePlane(fi, leg.Dir) is { } r) stations.Add((t, r));
                else noReference++;
            }

            // Pipe ends that leave the selection (or end open): the end of the pipe's centreline.
            foreach (var pipe in leg.Pipes)
            {
                var line = Centerline(pipe, geometry);
                foreach (var c in Ends(pipe).Where(c => !c.AllRefs.Cast<Connector>().Any(r => r.Owner is { } o && o.Id != pipe.Id && ids.Contains(o.Id))))
                {
                    if (line is null) { noReference++; continue; }
                    int i = line.GetEndPoint(0).DistanceTo(c.Origin) <= line.GetEndPoint(1).DistanceTo(c.Origin) ? 0 : 1;
                    if (line.GetEndPointReference(i) is { } r) stations.Add((T(c.Origin), r));
                }
            }

            stations = stations.OrderBy(s => s.T).Aggregate(new List<(double T, Reference Ref)>(), (kept, s) =>
            {
                if (kept.Count == 0 || s.T - kept[^1].T > SameStation) kept.Add(s);
                return kept;
            });
            if (stations.Count < 2) continue;

            // The side of the leg facing the viewer most, pushed away from the middle of the piping.
            var side = Perpendiculars(leg.Dir).MaxBy(o => Math.Abs(leg.Dir.CrossProduct(o).DotProduct(facing)))!;
            var mid = leg.Origin + leg.Dir * ((stations[0].T + stations[^1].T) / 2);
            if ((mid - centre).DotProduct(side) < 0) side = side.Negate();
            var shift = side * (leg.Radius + offset);

            try
            {
                view.SketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(leg.Dir.CrossProduct(side).Normalize(), leg.Origin));
                var refs = new ReferenceArray();
                foreach (var s in stations) refs.Append(s.Ref);
                var at = Line.CreateBound(leg.Origin + leg.Dir * stations[0].T + shift, leg.Origin + leg.Dir * stations[^1].T + shift);
                var dim = doc.Create.NewDimension(view, at, refs);
                if (dim?.get_BoundingBox(view) is { } bb) rects.Add(frame.Project(Corners(bb)));
                else if (dim is null) failed++;
            }
            catch (Exception)
            {
                failed++;
            }
        }

        if (noReference > 0) warnings.Add($"{noReference} fitting(s) or pipe end(s) had no centre reference to dimension to.");
        if (failed > 0) warnings.Add($"{failed} leg(s) couldn't be dimensioned (Revit refused the references).");
        return rects;
    }

    /// <summary>Straight pipes grouped by the centreline they run along.</summary>
    private static List<Run> Legs(IEnumerable<MEPCurve> pipes)
    {
        var legs = new List<Run>();
        foreach (var pipe in pipes)
        {
            if (pipe.Location is not LocationCurve { Curve: Line line }) continue;
            var dir = line.Direction.Normalize();
            var radius = Ends(pipe).Select(c => c.Shape == ConnectorProfileType.Round ? c.Radius : 0).DefaultIfEmpty(0).Max();
            var leg = legs.FirstOrDefault(l => Math.Abs(l.Dir.DotProduct(dir)) > 0.9999
                                               && (line.GetEndPoint(0) - l.Origin).CrossProduct(l.Dir).GetLength() < OnAxis);
            if (leg is null) legs.Add(new Run(line.GetEndPoint(0), dir, radius, [pipe]));
            else
            {
                leg.Pipes.Add(pipe);
                if (radius > leg.Radius) legs[legs.IndexOf(leg)] = leg with { Radius = radius };
            }
        }
        return legs;
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

    /// <summary>The pipe's centreline as Revit draws it, which carries end point references.</summary>
    private static Line? Centerline(Element pipe, Options geometry) =>
        pipe.get_Geometry(geometry)?.OfType<Line>().FirstOrDefault(l => l.Reference is not null);

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
