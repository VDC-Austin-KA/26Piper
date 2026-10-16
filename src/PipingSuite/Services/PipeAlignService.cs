using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using PipingSuite.Core;

namespace PipingSuite.Services;

public enum MoverChoice { Auto, First, Second }

public enum ConnectionMode
{
    /// <summary>Open end meets open end (pipe, tee outlet, valve...).</summary>
    EndToEnd,
    /// <summary>Open pipe end meets the body of a main pipe: main is split and a tee inserted.</summary>
    TeeIntoMain,
}

/// <param name="MaxShift">Largest correction (internal feet) applied without refusing; guards against picking the wrong pair.</param>
public sealed record AlignOptions(bool AlignX, bool AlignY, bool AlignZ, MoverChoice Mover, double MaxShift);

public sealed record AlignPlan(ConnectionMode Mode, Element Mover, Element Fixed, Connector MoverConnector, Connector? FixedConnector, XYZ? TeePoint);

/// <summary>
/// Fixes small misalignments between two piping elements and connects them.
/// Only the mover is translated; the fixed element (the larger pipe / the main / the fitting) never moves.
/// </summary>
public sealed class PipeAlignService(Document doc)
{
    private const double Eps = 1e-6;
    private static readonly double ParallelCos = Math.Cos(Math.PI / 36); // 5 degrees

    /// <summary>Chooses the connection mode and which element moves.</summary>
    public AlignPlan Plan(Element first, Element second, MoverChoice choice)
    {
        var end = BestEndPair(first, second);
        var tee = BestTee(first, second, choice);

        if (tee is not null && (end is null || tee.Value.Distance < end.Value.Distance))
            return new AlignPlan(ConnectionMode.TeeIntoMain, tee.Value.Branch, tee.Value.Main, tee.Value.BranchConnector, null, tee.Value.Point);

        if (end is null)
            throw new InvalidOperationException("No open connector found near the other element. Pick an unconnected pipe end.");

        var (a, b, _) = end.Value;
        bool moveFirst = choice switch
        {
            MoverChoice.First => true,
            MoverChoice.Second => false,
            _ => AutoMoveFirst(first, second, a, b),
        };
        return moveFirst
            ? new AlignPlan(ConnectionMode.EndToEnd, first, second, a, b, null)
            : new AlignPlan(ConnectionMode.EndToEnd, second, first, b, a, null);
    }

    /// <summary>Runs in its own transaction; everything rolls back if the final connection fails.</summary>
    public string Execute(AlignPlan plan, AlignOptions options)
    {
        using var t = new Transaction(doc, "Align & Connect");
        t.Start();
        var summary = plan.Mode == ConnectionMode.TeeIntoMain ? ConnectTee(plan, options) : ConnectEnds(plan, options);
        t.Commit();
        return summary;
    }

    // ---------- end to end ----------

    private string ConnectEnds(AlignPlan plan, AlignOptions o)
    {
        var mc = plan.MoverConnector;
        var fc = plan.FixedConnector!;
        var p = mc.Origin;
        var d = fc.Origin - p;
        var mDir = mc.CoordinateSystem.BasisZ;
        var fDir = fc.CoordinateSystem.BasisZ;

        if (plan.Mover is Pipe moverPipe)
        {
            // Facing connectors: slide the pipe sideways onto the target axis, then stretch the end.
            if (mDir.DotProduct(fDir) < -ParallelCos)
            {
                var shift = Filter(d - mDir * d.DotProduct(mDir), o);
                Move(plan.Mover, shift, o);
                SetPipeEnd(moverPipe, p + shift, p + shift + mDir * d.DotProduct(mDir));
                var moved = NearestConnector(plan.Mover, fc.Origin);
                return JoinStraight(moved, fc) + Describe(shift);
            }

            // Angled pipe-to-pipe: make the centrelines intersect, then let an elbow fill the corner.
            if (plan.Fixed is Pipe)
            {
                var (onMover, onFixed) = ClosestPoints(p, mDir, fc.Origin, fDir);
                var shift = Filter(onFixed - onMover, o);
                Move(plan.Mover, shift, o);
                doc.Create.NewElbowFitting(NearestConnector(plan.Mover, p + shift), fc);
                return "Connected with an elbow." + Describe(shift);
            }

            throw new InvalidOperationException(
                $"Connector directions differ by {Degrees(-mDir.DotProduct(fDir)):0.#}°. Rotate the fitting first.");
        }

        // Fitting/accessory mover: translate the whole element.
        var move = Filter(d, o);
        Move(plan.Mover, move, o);
        return JoinStraight(NearestConnector(plan.Mover, fc.Origin), fc) + Describe(move);
    }

    private string JoinStraight(Connector a, Connector b)
    {
        if (a.Origin.DistanceTo(b.Origin) > 0.001)
            throw new InvalidOperationException(
                "Ends still do not meet after aligning. Enable the remaining axes or increase the tolerance.");

        bool sameSize = SameSize(a, b);
        if (!sameSize)
        {
            doc.Create.NewTransitionFitting(a, b);
            return "Connected with a transition.";
        }

        if (a.Owner is Pipe && b.Owner is Pipe)
        {
            try
            {
                doc.Create.NewUnionFitting(a, b);
                return "Connected with a union.";
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // No union in the routing preferences: a direct connection is still valid.
            }
        }

        a.ConnectTo(b);
        return "Connected.";
    }

    // ---------- tee into main ----------

    private string ConnectTee(AlignPlan plan, AlignOptions o)
    {
        if (plan.Mover is not Pipe branch)
            throw new InvalidOperationException("Only a pipe can be teed into the main. Pick the branch pipe.");

        var main = (Pipe)plan.Fixed;
        var mainLine = (Line)((LocationCurve)main.Location).Curve;
        var bc = plan.MoverConnector;
        var bDir = bc.CoordinateSystem.BasisZ;

        if (Math.Abs(bDir.DotProduct(mainLine.Direction)) > ParallelCos)
            throw new InvalidOperationException("Branch runs parallel to the main; nothing to tee into.");

        var (onBranch, onMain) = ClosestPoints(bc.Origin, bDir, mainLine.GetEndPoint(0), mainLine.Direction);
        var shift = Filter(onMain - onBranch, o);
        Move(branch, shift, o);

        var branchEnd = onBranch + shift;
        var teePoint = mainLine.Project(branchEnd).XYZPoint;
        EnsureInterior(mainLine, teePoint, main.Diameter);
        SetPipeEnd(branch, bc.Origin + shift, branchEnd);

        var newMainId = PlumbingUtils.BreakCurve(doc, main.Id, teePoint);
        var c1 = NearestConnector(main, teePoint);
        var c2 = NearestConnector(doc.GetElement(newMainId), teePoint);
        doc.Create.NewTeeFitting(c1, c2, NearestConnector(branch, branchEnd));
        return "Main split and tee inserted." + Describe(shift);
    }

    // ---------- planning helpers ----------

    private static (Connector A, Connector B, double Distance)? BestEndPair(Element first, Element second) =>
        (from a in OpenConnectors(first)
         from b in OpenConnectors(second)
         let dist = a.Origin.DistanceTo(b.Origin)
         orderby dist
         select ((Connector, Connector, double)?)(a, b, dist)).FirstOrDefault();

    private static (Element Branch, Pipe Main, Connector BranchConnector, XYZ Point, double Distance)? BestTee(
        Element first, Element second, MoverChoice choice)
    {
        // The main never moves, so a forced mover can only be the branch.
        var pairs = new List<(Element Branch, Element Main)>();
        if (choice != MoverChoice.Second) pairs.Add((first, second));
        if (choice != MoverChoice.First) pairs.Add((second, first));

        (Element, Pipe, Connector, XYZ, double)? best = null;
        foreach (var (branch, mainElem) in pairs)
        {
            if (branch is not Pipe || mainElem is not Pipe main || main.Location is not LocationCurve { Curve: Line line }) continue;

            foreach (var c in OpenConnectors(branch))
            {
                var proj = line.Project(c.Origin);
                if (!IsInterior(line, proj.XYZPoint, main.Diameter)) continue;
                if (best is null || proj.Distance < best.Value.Item5)
                    best = (branch, main, c, proj.XYZPoint, proj.Distance);
            }
        }
        return best;
    }

    /// <summary>Default: fittings and the larger pipe stay put; the smaller pipe moves.</summary>
    private static bool AutoMoveFirst(Element first, Element second, Connector a, Connector b)
    {
        bool firstIsPipe = first is Pipe, secondIsPipe = second is Pipe;
        if (firstIsPipe != secondIsPipe) return firstIsPipe;
        if (firstIsPipe && a.Shape == ConnectorProfileType.Round && b.Shape == ConnectorProfileType.Round)
            return a.Radius <= b.Radius + Eps;
        return true;
    }

    public static IEnumerable<Connector> OpenConnectors(Element e) =>
        PipingElements.GetConnectorManager(e) is { } cm
            ? cm.Connectors.Cast<Connector>().Where(c => c.ConnectorType == ConnectorType.End && !c.IsConnected)
            : [];

    // ---------- geometry ----------

    private static XYZ Filter(XYZ v, AlignOptions o) =>
        new(o.AlignX ? v.X : 0, o.AlignY ? v.Y : 0, o.AlignZ ? v.Z : 0);

    private void Move(Element e, XYZ shift, AlignOptions o)
    {
        if (shift.GetLength() > o.MaxShift)
            throw new InvalidOperationException(
                $"Required move ({Format(shift.GetLength())}) exceeds the tolerance ({Format(o.MaxShift)}).");
        if (shift.GetLength() < Eps) return;

        ElementTransformUtils.MoveElement(doc, e.Id, shift);
        doc.Regenerate();
    }

    /// <summary>Moves the pipe endpoint currently at <paramref name="from"/> to <paramref name="to"/>.</summary>
    private void SetPipeEnd(Pipe pipe, XYZ from, XYZ to)
    {
        var lc = (LocationCurve)pipe.Location;
        var p0 = lc.Curve.GetEndPoint(0);
        var p1 = lc.Curve.GetEndPoint(1);
        lc.Curve = p0.DistanceTo(from) < p1.DistanceTo(from) ? Line.CreateBound(to, p1) : Line.CreateBound(p0, to);
        doc.Regenerate();
    }

    private static Connector NearestConnector(Element e, XYZ point) =>
        PipingElements.GetConnectorManager(e)!.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType == ConnectorType.End)
            .OrderBy(c => c.Origin.DistanceTo(point))
            .First();

    /// <summary>Closest points between two infinite lines (p1 + t·d1, p2 + s·d2).</summary>
    private static (XYZ OnFirst, XYZ OnSecond) ClosestPoints(XYZ p1, XYZ d1, XYZ p2, XYZ d2)
    {
        var w = p1 - p2;
        double a = d1.DotProduct(d1), b = d1.DotProduct(d2), c = d2.DotProduct(d2);
        double d = d1.DotProduct(w), e = d2.DotProduct(w);
        double den = a * c - b * b;
        if (Math.Abs(den) < Eps) return (p1, p2 + d2 * (e / c)); // parallel
        double t = (b * e - c * d) / den, s = (a * e - b * d) / den;
        return (p1 + d1 * t, p2 + d2 * s);
    }

    /// <summary>A tee needs room for its body: keep the split point away from the main's ends.</summary>
    private static bool IsInterior(Line line, XYZ point, double diameter)
    {
        double margin = Math.Max(diameter * 1.5, 0.1);
        return point.DistanceTo(line.GetEndPoint(0)) > margin && point.DistanceTo(line.GetEndPoint(1)) > margin;
    }

    private static void EnsureInterior(Line line, XYZ point, double diameter)
    {
        if (!IsInterior(line, point, diameter))
            throw new InvalidOperationException("Branch meets the main too close to its end for a tee.");
    }

    private static bool SameSize(Connector a, Connector b) =>
        a.Shape != ConnectorProfileType.Round || b.Shape != ConnectorProfileType.Round || Math.Abs(a.Radius - b.Radius) < Eps;

    private static double Degrees(double cos) => Math.Acos(Math.Clamp(cos, -1, 1)) * 180 / Math.PI;

    private string Format(double feet) => UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, feet, false);

    private string Describe(XYZ shift) => shift.GetLength() < Eps ? "" : $" Moved {Format(shift.GetLength())}.";
}
