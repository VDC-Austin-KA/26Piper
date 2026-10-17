using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using PipingSuite.Core;

namespace PipingSuite.Services;

public enum DisconnectKind
{
    /// <summary>Two open ends touching, same size, facing each other: connect them.</summary>
    Ends,
    /// <summary>Two open pipe ends touching at an angle: insert an elbow.</summary>
    Elbow,
    /// <summary>Two open pipe ends touching in line but different sizes: insert a transition.</summary>
    Transition,
    /// <summary>An open pipe end touching the side of another pipe: add a takeoff.</summary>
    Takeoff,
    /// <summary>An olet sitting on a pipe without being attached to it: re-make it attached.</summary>
    Olet,
    /// <summary>Touching, but needs judgement (fittings at an angle, size change on a fitting): shown, not fixed.</summary>
    Review,
    /// <summary>An open end with nothing near it: shown so it can be found, not fixed.</summary>
    Open,
}

/// <param name="Owner">Element with the open end.</param>
/// <param name="Other">What it touches (null for an open end).</param>
/// <param name="At">Where the open end is.</param>
public sealed record Disconnect(DisconnectKind Kind, ElementId Owner, ElementId? Other, XYZ At, double Gap, string What)
{
    public bool CanFix => Kind is not (DisconnectKind.Review or DisconnectKind.Open);
}

/// <summary>
/// Connect Disconnects: finds open connectors in a piping system that touch something they should be
/// joined to, and joins them. Loose pieces are usually in a different system (Revit splits systems at a
/// break), so the search looks at everything around each open end, not just the picked system.
/// </summary>
public sealed class DisconnectService(Document doc)
{
    private static readonly ElementMulticategoryFilter Joinable = new(new List<BuiltInCategory>
    {
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_PipeAccessory, BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures,
        BuiltInCategory.OST_Sprinklers,
    });

    private const double Facing = -0.98; // cosine: ends pointing at each other
    private const double Square = 0.2;   // |cosine|: a branch end roughly square to the pipe it lands on

    /// <summary>The picked elements plus everything in their piping systems (a picked system counts as all of it).</summary>
    public List<Element> Scope(IEnumerable<Element> picked)
    {
        var found = new Dictionary<ElementId, Element>();
        foreach (var e in picked)
        {
            var systems = e is PipingSystem ps
                ? [ps]
                : PhysicalConnectors(e).Select(c => c.MEPSystem).OfType<PipingSystem>();
            if (e is not PipingSystem) found.TryAdd(e.Id, e);
            foreach (var system in systems.DistinctBy(s => s.Id))
            foreach (Element member in system.PipingNetwork)
                found.TryAdd(member.Id, member);
        }
        return found.Values.Where(e => PhysicalConnectors(e).Any()).ToList();
    }

    /// <summary>Every open end in <paramref name="scope"/>, with what (if anything) it should be joined to.</summary>
    /// <param name="tolerance">How far apart (ft) still counts as touching.</param>
    /// <param name="sameSystemType">Only join ends of the same system classification (or unclassified fittings).</param>
    public List<Disconnect> Find(IReadOnlyList<Element> scope, double tolerance, bool sameSystemType)
    {
        var results = new List<Disconnect>();
        var paired = new HashSet<(long, long)>();
        foreach (var e in scope)
        foreach (var c in PhysicalConnectors(e).Where(c => !c.IsConnected).ToList())
        {
            var p = c.Origin;
            var pad = new XYZ(1, 1, 1) * (tolerance + 0.05);
            var near = new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(Joinable)
                .WherePasses(new BoundingBoxIntersectsFilter(new Outline(p - pad, p + pad)))
                .Where(o => o.Id != e.Id).ToList();

            // 1. Another open end at the same spot.
            var partner = near.SelectMany(o => PhysicalConnectors(o).Where(d => !d.IsConnected))
                .Where(d => d.Origin.DistanceTo(p) <= tolerance && (!sameSystemType || SameSystem(c, d)))
                .MinBy(d => d.Origin.DistanceTo(p));
            if (partner is not null)
            {
                long a = e.Id.Value, b = partner.Owner.Id.Value;
                if (!paired.Add((Math.Min(a, b), Math.Max(a, b)))) continue; // each touching pair is listed once
                results.Add(Classify(e, c, partner));
                continue;
            }

            // 2. Landing on the side of a pipe.
            var host = near.OfType<Pipe>().FirstOrDefault(h => OnSide(h, c, tolerance) && (!sameSystemType || SameSystem(c, PhysicalConnectors(h).First())));
            if (host is not null)
            {
                bool olet = OletService.IsTap(e);
                results.Add(new Disconnect(olet ? DisconnectKind.Olet : e is Pipe ? DisconnectKind.Takeoff : DisconnectKind.Review,
                    e.Id, host.Id, p, 0,
                    olet ? $"{Name(e)} sits on {Name(host)} without being attached"
                    : e is Pipe ? $"{Name(e)} ends on the side of {Name(host)}"
                    : $"{Name(e)} ends on the side of {Name(host)} (needs a tee or olet; not a pipe end)"));
                continue;
            }

            // 3. Nothing there: a real open end, listed so it can be found.
            results.Add(new Disconnect(DisconnectKind.Open, e.Id, null, p, 0, $"{Name(e)}: open end, nothing within reach"));
        }
        return results.OrderBy(r => r.Kind).ToList();
    }

    private Disconnect Classify(Element e, Connector c, Connector d)
    {
        double gap = c.Origin.DistanceTo(d.Origin);
        bool sameSize = Math.Abs(Radius(c) - Radius(d)) < 1e-4;
        bool facing = c.CoordinateSystem.BasisZ.DotProduct(d.CoordinateSystem.BasisZ) < Facing;
        bool pipes = e is MEPCurve && d.Owner is MEPCurve;
        var what = $"{Name(e)} ↔ {Name(d.Owner)}";
        var kind = sameSize && facing ? DisconnectKind.Ends
                 : pipes && facing ? DisconnectKind.Transition
                 : pipes && sameSize ? DisconnectKind.Elbow
                 : DisconnectKind.Review;
        var note = kind switch
        {
            DisconnectKind.Transition => " (sizes differ: transition)",
            DisconnectKind.Elbow => " (at an angle: elbow)",
            DisconnectKind.Review => sameSize ? " (at an angle between fittings)" : " (sizes differ at a fitting)",
            _ => string.Empty,
        };
        return new Disconnect(kind, e.Id, d.Owner.Id, c.Origin, gap, what + note);
    }

    /// <summary>
    /// Fixes each item in its own transaction inside one undo group: a fix Revit refuses is rolled back and
    /// reported without touching the others. Connectors are looked up afresh, as earlier fixes change them.
    /// </summary>
    public (int Fixed, List<string> Notes) Fix(IEnumerable<Disconnect> items)
    {
        var notes = new List<string>();
        int done = 0;
        using var tg = new TransactionGroup(doc, "Connect Disconnects");
        tg.Start();
        foreach (var item in items.Where(i => i.CanFix))
        {
            var failures = new FailureCollector();
            using var t = new Transaction(doc, "Connect Disconnects - " + item.Kind);
            t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            t.Start();
            try
            {
                FixOne(item);
                if (t.Commit() == TransactionStatus.Committed) done++;
                else notes.Add($"{item.What}: Revit refused ({string.Join("; ", failures.Errors.DefaultIfEmpty("no reason given"))}).");
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                notes.Add($"{item.What}: {ex.Message}");
            }
        }
        if (done > 0) tg.Assimilate();
        else tg.RollBack();
        return (done, notes);
    }

    private void FixOne(Disconnect item)
    {
        var owner = doc.GetElement(item.Owner) ?? throw new InvalidOperationException("The element is gone.");
        var other = item.Other is { } o ? doc.GetElement(o) ?? throw new InvalidOperationException("The other element is gone.") : null;
        var mine = OpenNear(owner, item.At) ?? throw new InvalidOperationException("Already connected.");

        switch (item.Kind)
        {
            case DisconnectKind.Ends:
                var theirs = OpenNear(other!, item.At) ?? throw new InvalidOperationException("Already connected.");
                // Close a hairline gap by moving a pipe end onto the other connector, then join.
                if (mine.Origin.DistanceTo(theirs.Origin) > 1e-6)
                {
                    if (MoveEnd(owner, mine.Origin, theirs.Origin)) mine = OpenNear(owner, theirs.Origin)!;
                    else if (MoveEnd(other!, theirs.Origin, mine.Origin)) theirs = OpenNear(other!, mine.Origin)!;
                }
                mine.ConnectTo(theirs);
                break;
            case DisconnectKind.Elbow:
                doc.Create.NewElbowFitting(mine, OpenNear(other!, item.At)!);
                break;
            case DisconnectKind.Transition:
                doc.Create.NewTransitionFitting(mine, OpenNear(other!, item.At)!);
                break;
            case DisconnectKind.Takeoff:
                doc.Create.NewTakeoffFitting(mine, (MEPCurve)other!);
                break;
            case DisconnectKind.Olet:
                Reseat((FamilyInstance)owner, (Pipe)other!);
                break;
        }
    }

    /// <summary>
    /// Re-makes an unattached olet on the pipe it sits on (same type, size and facing) and reconnects whatever
    /// its outlet fed. Revit only attaches an olet to a pipe when it creates it as a takeoff.
    /// </summary>
    private void Reseat(FamilyInstance olet, Pipe main)
    {
        var axis = ((LocationCurve)main.Location).Curve;
        var outlet = PhysicalConnectors(olet).MaxBy(c => c.Origin.DistanceTo(axis.Project(c.Origin).XYZPoint))!; // the seat is the end nearest the axis
        var fed = outlet.IsConnected
            ? outlet.AllRefs.Cast<Connector>().Where(r => r.Owner.Id != olet.Id && r.ConnectorType != ConnectorType.Logical && r.Owner is not MEPSystem)
                .Select(r => (r.Owner.Id, r.Origin)).FirstOrDefault()
            : default;
        var (at, facing, size, type) = (outlet.Origin, outlet.CoordinateSystem.BasisZ, Radius(outlet) * 2, olet.Symbol.Id);

        doc.Delete(olet.Id);
        var created = new OletService(doc, doc.ActiveView).PlaceOn(main, at, facing, size, type);
        if (fed.Id is null) return;

        var newOutlet = OletService.AsOlet(created)?.Branch ?? throw new InvalidOperationException("The new olet has no outlet.");
        var target = doc.GetElement(fed.Id) is { } t ? OpenNear(t, fed.Origin) : null;
        if (target is null) return;
        if (target.Origin.DistanceTo(newOutlet.Origin) > 1e-6 && MoveEnd(target.Owner, target.Origin, newOutlet.Origin))
            target = OpenNear(target.Owner, newOutlet.Origin)!;
        target.ConnectTo(newOutlet);
    }

    /// <summary>Moves a straight pipe's end at <paramref name="from"/> to <paramref name="to"/>; false for anything else.</summary>
    private static bool MoveEnd(Element e, XYZ from, XYZ to)
    {
        if (e is not MEPCurve { Location: LocationCurve { Curve: Line line } lc }) return false;
        var (a, b) = (line.GetEndPoint(0), line.GetEndPoint(1));
        lc.Curve = a.DistanceTo(from) < b.DistanceTo(from) ? Line.CreateBound(to, b) : Line.CreateBound(a, to);
        e.Document.Regenerate();
        return true;
    }

    /// <summary>
    /// <paramref name="c"/> lands on the side of <paramref name="pipe"/>: within its outside radius (plus
    /// tolerance) of the centreline, clear of its ends, square to it and pointing into it.
    /// </summary>
    private static bool OnSide(Pipe pipe, Connector c, double tolerance)
    {
        if (pipe.Location is not LocationCurve { Curve: Line line }) return false;
        var axis = line.Direction;
        var foot = line.Project(c.Origin).XYZPoint;
        double along = (foot - line.GetEndPoint(0)).DotProduct(axis);
        double radius = (pipe.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER)?.AsDouble() ?? pipe.Diameter) / 2;
        var dir = c.CoordinateSystem.BasisZ;
        return along > pipe.Diameter && along < line.Length - pipe.Diameter
               && c.Origin.DistanceTo(foot) <= radius + tolerance
               && Math.Abs(dir.DotProduct(axis)) < Square
               && dir.DotProduct(foot - c.Origin) >= 0;
    }

    /// <summary>Same system classification, or one side unclassified (fittings, accessories report none).</summary>
    private static bool SameSystem(Connector a, Connector b)
    {
        static bool Wild(PipeSystemType t) => t is PipeSystemType.UndefinedSystemType or PipeSystemType.Fitting or PipeSystemType.Global;
        try
        {
            return Wild(a.PipeSystemType) || Wild(b.PipeSystemType) || a.PipeSystemType == b.PipeSystemType;
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException)
        {
            return true; // not a piping connector after all: don't block on it
        }
    }

    private static Connector? OpenNear(Element e, XYZ at) =>
        PhysicalConnectors(e).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(at));

    private static IEnumerable<Connector> PhysicalConnectors(Element e) =>
        PipingElements.GetConnectorManager(e)?.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType != ConnectorType.Logical && c.Domain == Domain.DomainPiping) ?? [];

    private static double Radius(Connector c) => c.Shape == ConnectorProfileType.Round ? c.Radius : 0;

    private static string Name(Element e) => $"{e.Category?.Name ?? "Element"} [{e.Id.Value}]";
}
