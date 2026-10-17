using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using PipingSuite.Core;

namespace PipingSuite.Services;

/// <summary>
/// A branch off a header: its elements and the connector where it meets the header ("root"), which
/// sits at <paramref name="Origin"/> facing back along <paramref name="Out"/> (away from the header).
/// When the branch includes its olet (<paramref name="OletId"/>), the root is the olet's outlet instead
/// and <paramref name="Header"/> is the pipe the olet sits on: the olet is replaced too, re-made on that pipe.
/// </summary>
public sealed record Branch(IReadOnlyList<Element> Elements, ElementId RootId, XYZ Origin, XYZ Out, double RootRadius,
    Element? Header, string? SystemName, ElementId? OletId = null)
{
    public IEnumerable<ElementId> Ids => Elements.Select(e => e.Id);
}

/// <summary>The correctly built branch: loose elements, or the members of one assembly (copied as the assembly).</summary>
public sealed record Source(IReadOnlyList<Element> Elements, ElementId? AssemblyId);

/// <summary>An old branch and where the source lands in its place (source pose → this branch's pose).</summary>
public sealed record Target(Branch Old, Transform Place);

/// <summary>Numbering for each placement: <paramref name="Start"/> on the first, stepped once per placement after.</summary>
public sealed record Numbering(string Parameter, IncrementMode Mode, string Start);

public sealed record ReplaceResult(int Replaced, IReadOnlyList<ElementId> NewIds, IReadOnlyList<string> Warnings);

/// <summary>
/// Replace Configuration. A branch matches a picked one when the two can be walked side by side from
/// their header connections, element for element: same type and sizes, every connector at the same spot
/// and facing the same way once the picked branch is moved onto the candidate's header connection and
/// turned to face out of it (keeping up as up, so the other side of a header or another header direction
/// matches, but a branch that drops never matches one that rises). Only the picked branch's elements are compared, so whatever a branch runs on into
/// (equipment, a rack manifold) doesn't matter, and the header can be any section, size or system.
/// Each match is then swapped for the correct branch, placed the way it best fits the picked one.
/// </summary>
public sealed class ReplaceConfigurationService(Document doc)
{
    private const double Tol = 0.04;     // ft (~1/2"): connector positions that count as the same
    private const double FitTol = 0.1;   // ft: looser, for fitting the correct branch over the old one
    private const double SameDir = 0.99; // cosine: connector directions that count as the same

    /// <summary>Turns about the root axis to try, nearest "keeps up as up" first (0, ±15°, ±30° … 180°).</summary>
    private static readonly double[] Turns =
        [0, .. Enumerable.Range(1, 11).SelectMany(k => new[] { k, -k }).Select(k => k * Math.PI / 12), Math.PI];

    /// <summary>
    /// Splits a selection into its separate branches (connected pieces), each with its header found
    /// automatically. Pieces connected to nothing are skipped.
    /// </summary>
    public List<Branch> Split(IReadOnlyList<Element> elements)
    {
        var byId = elements.ToDictionary(e => e.Id);
        var uf = new UnionFind(byId.Keys);
        foreach (var e in elements)
        foreach (var n in PipingElements.GetConnectedElements(e).Where(n => byId.ContainsKey(n.Id)))
            uf.Union(e.Id, n.Id);

        return byId.Keys.GroupBy(uf.Find)
            .Select(g => g.Select(id => byId[id]).ToList())
            .Select(AutoDescribe).OfType<Branch>()
            .ToList();
    }

    /// <summary>
    /// How to lay the correct branch over <paramref name="bad"/>: tries each of its ends of the bad root's
    /// size as the header end, at each upright turn, and keeps the pose where most of its connectors land on
    /// the bad branch's (ties: an end already on a header, then the smallest turn). Null if it has no such end.
    /// </summary>
    public Transform? Fit(Source good, Branch bad)
    {
        // Olets are left out of the fit: theirs sit higher or lower on a different header size.
        var olet = RootOlet(good.Elements);
        var body = good.Elements.Where(e => e.Id != olet?.Id).ToList();
        var goodIds = body.Select(e => e.Id).ToHashSet();
        var goodConnectors = body.SelectMany(PhysicalConnectors).ToList();
        var badPoints = bad.Elements.Where(e => e.Id != bad.OletId).SelectMany(PhysicalConnectors).Select(c => c.Origin).ToList();
        var roots = olet is not null && Outlet(olet, body) is { } fed
            ? [fed] // with its olet, the correct branch's header end is plain: what the olet feeds
            : goodConnectors.Where(c => Math.Abs(Radius(c) - bad.RootRadius) < 1e-4).ToList();
        if (roots.Count == 0) return null;

        var badFrame = Frame(bad.Origin, bad.Out, 0);
        var poses = roots.SelectMany(root => Turns.Select(turn => (Root: root,
            Pose: badFrame.Multiply(Frame(root.Origin, root.CoordinateSystem.BasisZ.Negate(), turn).Inverse)))).ToList();
        // Upright poses only, when there are any: the copy then only turns about vertical.
        if (poses.Any(p => p.Pose.OfVector(XYZ.BasisZ).Z > SameDir)) poses = poses.Where(p => p.Pose.OfVector(XYZ.BasisZ).Z > SameDir).ToList();

        (int Score, Transform Pose)? best = null;
        foreach (var (root, pose) in poses)
        {
            bool onHeader = root.IsConnected && Refs(root).Any(r => !goodIds.Contains(r.Owner.Id));
            int score = goodConnectors.Count(c => badPoints.Any(p => pose.OfPoint(c.Origin).DistanceTo(p) < FitTol)) * 2
                        + (onHeader ? 1 : 0);
            if (best is null || score > best.Value.Score) best = (score, pose);
        }
        return best!.Value.Pose;
    }

    /// <summary>
    /// Every other branch laid out like one of <paramref name="picked"/>, with the transform taking the
    /// picked branch onto it. Candidates start at elements like the picked root (same type, sizes and
    /// piping system type) whose same-size connector is connected to something: their header.
    /// </summary>
    public List<(Branch Match, Transform FromPicked, int Picked)> FindMatches(IReadOnlyList<Branch> picked, IReadOnlyCollection<ElementId> exclude)
    {
        var matches = new List<(Branch, Transform, int)>();
        var taken = new HashSet<ElementId>(exclude.Concat(picked.SelectMany(b => b.Ids)));

        for (int p = 0; p < picked.Count; p++)
        {
            var reference = picked[p];
            var root = doc.GetElement(reference.RootId);
            var key = Key(root);
            var systemType = SystemType(root);
            var frame = Frame(reference.Origin, reference.Out, 0);
            var sameType = new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(
                new ElementId(BuiltInParameter.ELEM_TYPE_PARAM), root.GetTypeId()));

            foreach (var candidate in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(sameType))
            {
                if (taken.Contains(candidate.Id) || Key(candidate) != key || SystemType(candidate) != systemType) continue;

                var found = PhysicalConnectors(candidate)
                    .Where(c => c.IsConnected && Math.Abs(Radius(c) - reference.RootRadius) < 1e-4)
                    .Where(c => reference.OletId is null || Refs(c).Any(r => OletService.IsTap(r.Owner) && !taken.Contains(r.Owner.Id)))
                    .SelectMany(c => Turns.Select(turn => (Root: c, Move: Frame(c.Origin, c.CoordinateSystem.BasisZ.Negate(), turn).Multiply(frame.Inverse))))
                    .Where(h => h.Move.OfVector(XYZ.BasisZ).Z > SameDir) // up stays up: a branch that drops is not one that rises
                    .Select(h => (h.Root, h.Move, Map: Correspond(reference, root, candidate, h.Move, taken)))
                    .FirstOrDefault(h => h.Map is not null && !Refs(h.Root).Any(r => h.Map.Values.Any(e => e.Id == r.Owner.Id)));
                if (found.Map is null) continue;

                var elements = found.Map.Values.ToList();
                var header = Refs(found.Root).Select(r => r.Owner).FirstOrDefault();
                ElementId? oletId = null;
                if (reference.OletId is not null && header is not null)
                {
                    elements.Add(header); // the olet goes with the branch; its header is the pipe it sits on
                    oletId = header.Id;
                    header = OletService.AsOlet(header)?.Main;
                }
                matches.Add((new Branch(elements, candidate.Id, found.Root.Origin, found.Root.CoordinateSystem.BasisZ.Negate(),
                    reference.RootRadius, header, SystemName(candidate), oletId), found.Move, p));
                taken.UnionWith(elements.Select(e => e.Id));
            }
        }
        return matches;
    }

    /// <summary>
    /// Walks the picked branch and a candidate side by side from their roots. Every connector of each picked
    /// element must have a twin on the candidate element at <paramref name="move"/>'s image of it, facing the
    /// same way, and neighbours inside the picked branch must have twins of the same kind there. Returns
    /// picked element id → candidate element, or null at the first difference.
    /// </summary>
    private static Dictionary<ElementId, Element>? Correspond(Branch reference, Element root, Element candidate, Transform move, HashSet<ElementId> taken)
    {
        var inside = reference.Ids.Where(id => id != reference.OletId).ToHashSet();
        var map = new Dictionary<ElementId, Element> { [root.Id] = candidate };
        var queue = new Queue<(Element B, Element C)>([(root, candidate)]);
        while (queue.TryDequeue(out var pair))
        {
            var (b, c) = pair;
            var mine = PhysicalConnectors(b).ToList();
            var theirs = PhysicalConnectors(c).ToList();
            if (mine.Count != theirs.Count) return null;

            foreach (var cb in mine)
            {
                var at = move.OfPoint(cb.Origin);
                var facing = move.OfVector(cb.CoordinateSystem.BasisZ);
                var cc = theirs.FirstOrDefault(x => x.Origin.DistanceTo(at) < Tol && x.CoordinateSystem.BasisZ.DotProduct(facing) > SameDir);
                if (cc is null) return null;
                if (!cb.IsConnected) continue;

                foreach (var nb in Refs(cb).Select(r => r.Owner).Where(o => o.Id != b.Id && inside.Contains(o.Id)))
                {
                    var key = Key(nb);
                    var nc = cc.IsConnected ? Refs(cc).Select(r => r.Owner).FirstOrDefault(o => o.Id != c.Id && Key(o) == key) : null;
                    if (nc is null || taken.Contains(nc.Id)) return null;
                    if (map.TryGetValue(nb.Id, out var seen))
                    {
                        if (seen.Id != nc.Id) return null;
                        continue;
                    }
                    map[nb.Id] = nc;
                    queue.Enqueue((nb, nc));
                }
            }
        }
        return map.Count == inside.Count ? map : null;
    }

    /// <summary>
    /// Replaces each target with a copy of <paramref name="source"/>, one transaction per placement inside a
    /// group (a placement that fails rolls back alone and leaves the old branch in place). Placements run
    /// along the direction the branches spread out most, lowest first, which is also the numbering order.
    /// </summary>
    public ReplaceResult Replace(IReadOnlyList<Target> targets, Source source, Numbering? numbering)
    {
        var warnings = new List<string>();
        var newIds = new List<ElementId>();
        var ordered = AlongHeader(targets);

        using var tg = new TransactionGroup(doc, "Replace Configuration");
        tg.Start();
        int replaced = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var target = ordered[i];
            var label = $"Branch [{target.Old.RootId.Value}]";
            var failures = new Failures();
            using var t = new Transaction(doc, "Replace Configuration - placement");
            t.SetFailureHandlingOptions(t.GetFailureHandlingOptions().SetFailuresPreprocessor(failures).SetClearAfterRollback(true));
            t.Start();
            try
            {
                var placed = Place(target, source, warnings, label);
                if (numbering is not null)
                {
                    var value = ValueSequence.Step(numbering.Start, numbering.Mode, i);
                    if (placed.Count(e => SetValue(e, numbering.Parameter, value)) == 0)
                        warnings.Add($"{label}: no element has a writable \"{numbering.Parameter}\".");
                }
                if (t.Commit() != TransactionStatus.Committed)
                {
                    warnings.Add($"{label}: Revit rejected it ({string.Join("; ", failures.Errors.DefaultIfEmpty("no reason given"))}); the old branch was kept.");
                    continue;
                }
                newIds.AddRange(placed.Select(e => e.Id));
                replaced++;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                warnings.Add($"{label}: {ex.Message} The old branch was kept.");
            }
        }
        if (replaced > 0) tg.Assimilate();
        else tg.RollBack();
        return new ReplaceResult(replaced, newIds, warnings);
    }

    /// <summary>
    /// Deletes the old branch, copies the source into its pose, and remakes each outside connection it can.
    /// With olets, the old one goes with its branch and a new one of the correct branch's olet type (else the
    /// old type) is made on the same header pipe at the same spot; the new branch is nudged onto its outlet.
    /// </summary>
    private List<Element> Place(Target target, Source source, List<string> warnings, string label)
    {
        var old = target.Old;
        var links = ExternalLinks(old.Elements)
            .Where(l => l.Inside.Owner.Id != old.OletId) // the olet's seat on the header is re-made below, not reconnected
            .Select(l => (l.Outside.Owner.Id, At: l.Outside.Origin)).ToList();
        var main = old.OletId is not null ? old.Header as Pipe : null;
        var sourceOlet = RootOlet(source.Elements);
        var oletType = (sourceOlet as FamilyInstance)?.Symbol.Id
                       ?? (old.OletId is { } o ? (doc.GetElement(o) as FamilyInstance)?.Symbol.Id : null);
        doc.Delete(old.Ids.ToList());

        // New olet first, while the spot is clear (Revit builds it off a temporary branch pipe).
        var olet = main is not null && oletType is not null
            ? new OletService(doc, doc.ActiveView).PlaceOn(main, old.Origin, old.Out, old.RootRadius * 2, oletType)
            : null;

        // Copy so a reference point lands where the pose sends it, then turn about that point.
        var body = source.Elements.Where(e => e.Id != sourceOlet?.Id).ToList();
        var pivot = body.SelectMany(PhysicalConnectors).First().Origin;
        var landed = target.Place.OfPoint(pivot);
        var copied = ElementTransformUtils.CopyElements(doc,
            source.AssemblyId is { } a ? [a] : body.Select(e => e.Id).ToList(), landed - pivot);
        var (axis, angle) = RotationOf(target.Place);
        if (angle > 1e-9) ElementTransformUtils.RotateElements(doc, copied, Line.CreateUnbound(landed, axis), angle);
        doc.Regenerate();

        var placed = copied.Select(doc.GetElement).ToList();
        var members = placed.OfType<AssemblyInstance>().SelectMany(ai => ai.GetMemberIds()).Select(doc.GetElement);
        var all = placed.Concat(members).DistinctBy(e => e.Id).ToList();
        if (sourceOlet is not null && source.AssemblyId is not null && RootOlet(all) is { } copiedOlet)
        {
            doc.Delete(copiedOlet.Id); // the assembly brought its olet along; a real one is made on the header instead
            all.Remove(copiedOlet);
        }

        if (olet is not null)
        {
            var outlet = OletService.AsOlet(olet)?.Branch ?? throw new InvalidOperationException("The new olet has no outlet.");
            var start = all.SelectMany(PhysicalConnectors).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(old.Origin))
                        ?? throw new InvalidOperationException("The correct branch has no open end to put on the olet.");
            var nudge = outlet.Origin - start.Origin; // a different olet type or header size moves the outlet a little
            if (nudge.GetLength() > Tol * 6)
                warnings.Add($"{label}: the new olet's outlet is {Format(nudge)} from the branch; check it.");
            if (nudge.GetLength() > 1e-6)
            {
                ElementTransformUtils.MoveElements(doc, copied, nudge);
                doc.Regenerate();
                start = all.SelectMany(PhysicalConnectors).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(outlet.Origin))!;
            }
            start.ConnectTo(outlet);
            all.Add(olet);
        }

        foreach (var (ownerId, at) in links)
        {
            var outside = doc.GetElement(ownerId) is { } owner
                ? PhysicalConnectors(owner).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(at))
                : null;
            var inside = all.SelectMany(PhysicalConnectors).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(at));
            if (outside is null || inside is null || inside.Origin.DistanceTo(at) > Tol || outside.Origin.DistanceTo(at) > Tol)
            {
                warnings.Add($"{label}: the connection at {Format(at)} was left open (the new branch has no open end there).");
                continue;
            }
            if (Math.Abs(Radius(inside) - Radius(outside)) > 1e-4)
                warnings.Add($"{label}: sizes differ at {Format(at)}; check that connection.");
            try
            {
                inside.ConnectTo(outside);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                warnings.Add($"{label}: the connection at {Format(at)} could not be made ({ex.Message}).");
            }
        }
        return all;
    }

    /// <summary>
    /// A branch whose header is its outside connection to piping with the biggest connectors (the header
    /// tee/olet), so equipment, valves or a smaller pipe at the far end are never taken for the header.
    /// </summary>
    private static Branch? AutoDescribe(IReadOnlyList<Element> elements)
    {
        var link = ExternalLinks(elements)
            .OrderByDescending(l => PipingElements.IsPiping(l.Outside.Owner))
            .ThenByDescending(l => MaxRadius(l.Outside.Owner))
            .FirstOrDefault();
        if (link.Inside is null) return null;

        var owner = link.Inside.Owner;
        if (OletService.IsTap(owner) && Outlet(owner, elements) is { } fed) // the olet came with the branch: measure from its outlet
            return new Branch(elements, fed.Owner.Id, fed.Origin, fed.CoordinateSystem.BasisZ.Negate(), Radius(fed),
                link.Outside.Owner, SystemName(fed.Owner), owner.Id);
        return new Branch(elements, owner.Id, link.Inside.Origin, link.Inside.CoordinateSystem.BasisZ.Negate(),
            Radius(link.Inside), link.Outside.Owner, SystemName(owner));
    }

    /// <summary>The connector an olet's outlet feeds, on one of <paramref name="elements"/>.</summary>
    private static Connector? Outlet(Element olet, IReadOnlyCollection<Element> elements)
    {
        var ids = elements.Select(e => e.Id).ToHashSet();
        return PhysicalConnectors(olet).Where(c => c.IsConnected).SelectMany(Refs)
            .FirstOrDefault(r => r.Owner.Id != olet.Id && ids.Contains(r.Owner.Id) && r.ConnectorType == ConnectorType.End);
    }

    /// <summary>
    /// The olet a branch starts from: a tap among <paramref name="elements"/> that isn't sitting on one of their
    /// own pipes (those are gauge/vent olets along the branch). One on an outside pipe wins over a loose one.
    /// </summary>
    private static Element? RootOlet(IReadOnlyCollection<Element> elements)
    {
        var ids = elements.Select(e => e.Id).ToHashSet();
        IEnumerable<Connector> Seats(Element t) => PhysicalConnectors(t).SelectMany(Refs).Where(r => r.ConnectorType == ConnectorType.Curve);
        return elements.Where(OletService.IsTap)
            .Where(t => !Seats(t).Any(r => ids.Contains(r.Owner.Id)))
            .OrderByDescending(t => Seats(t).Any())
            .FirstOrDefault();
    }

    /// <summary>Frame at a root: X out of the header, Y world-up turned <paramref name="turn"/> about X (world X for a vertical branch).</summary>
    private static Transform Frame(XYZ origin, XYZ x, double turn)
    {
        var y0 = Perpendicular(x, XYZ.BasisZ) ?? Perpendicular(x, XYZ.BasisX)!;
        var z0 = x.CrossProduct(y0);
        var y = y0 * Math.Cos(turn) + z0 * Math.Sin(turn);
        var t = Transform.Identity;
        t.Origin = origin;
        t.BasisX = x;
        t.BasisY = y;
        t.BasisZ = x.CrossProduct(y);
        return t;
    }

    private static XYZ? Perpendicular(XYZ x, XYZ hint)
    {
        var v = hint - x * hint.DotProduct(x);
        return v.GetLength() < 0.1 ? null : v.Normalize();
    }

    /// <summary>Axis and angle of the rotation part of <paramref name="t"/> (its basis vectors are the matrix columns).</summary>
    private static (XYZ Axis, double Angle) RotationOf(Transform t)
    {
        XYZ bx = t.BasisX, by = t.BasisY, bz = t.BasisZ;
        double angle = Math.Acos(Math.Clamp((bx.X + by.Y + bz.Z - 1) / 2, -1, 1));
        if (angle < 1e-9) return (XYZ.BasisZ, 0);
        if (Math.PI - angle > 1e-6)
            return (new XYZ(by.Z - bz.Y, bz.X - bx.Z, bx.Y - by.X).Normalize(), angle);

        // Half turn: the axis comes from the diagonal, signs from the largest component's row.
        double x = Math.Sqrt(Math.Max(0, (bx.X + 1) / 2)), y = Math.Sqrt(Math.Max(0, (by.Y + 1) / 2)), z = Math.Sqrt(Math.Max(0, (bz.Z + 1) / 2));
        XYZ axis = x >= y && x >= z ? new XYZ(x, Math.CopySign(y, by.X), Math.CopySign(z, bz.X))
                 : y >= z ? new XYZ(Math.CopySign(x, bx.Y), y, Math.CopySign(z, bz.Y))
                 : new XYZ(Math.CopySign(x, bx.Z), Math.CopySign(y, by.Z), z);
        return (axis.Normalize(), Math.PI);
    }

    /// <summary>Targets ordered along the axis their roots spread out on most.</summary>
    private static List<Target> AlongHeader(IReadOnlyList<Target> targets)
    {
        if (targets.Count < 2) return targets.ToList();
        double Spread(Func<XYZ, double> f) => targets.Max(t => f(t.Old.Origin)) - targets.Min(t => f(t.Old.Origin));
        Func<XYZ, double> axis = Spread(p => p.X) >= Spread(p => p.Y)
            ? (Spread(p => p.X) >= Spread(p => p.Z) ? p => p.X : p => p.Z)
            : (Spread(p => p.Y) >= Spread(p => p.Z) ? p => p.Y : p => p.Z);
        return targets.OrderBy(t => axis(t.Old.Origin)).ToList();
    }

    private static bool SetValue(Element e, string name, string value)
    {
        var p = e.LookupParameter(name);
        if (p is null || p.IsReadOnly) return false;
        return p.StorageType switch
        {
            StorageType.String => p.Set(value),
            StorageType.Integer => int.TryParse(value, out var n) && p.Set(n),
            _ => false,
        };
    }

    private static string Key(Element e) =>
        $"{e.GetTypeId().Value}|{string.Join(",", PhysicalConnectors(e).Select(c => Math.Round(Radius(c), 4)).OrderBy(r => r))}";

    /// <summary>Connections from inside the set to anything outside it (the header, equipment, other branches).</summary>
    public static List<(Connector Inside, Connector Outside)> ExternalLinks(IReadOnlyCollection<Element> elements)
    {
        var ids = elements.Select(e => e.Id).ToHashSet();
        return elements.SelectMany(PhysicalConnectors).Where(c => c.IsConnected)
            .SelectMany(c => Refs(c).Where(r => !ids.Contains(r.Owner.Id)).Select(r => (c, r)))
            .ToList();
    }

    private static IEnumerable<Connector> Refs(Connector c) =>
        c.AllRefs.Cast<Connector>().Where(r => r.ConnectorType != ConnectorType.Logical && r.Owner is not null and not MEPSystem);

    private static IEnumerable<Connector> PhysicalConnectors(Element e) =>
        PipingElements.GetConnectorManager(e)?.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType != ConnectorType.Logical && c.Domain == Domain.DomainPiping) ?? [];

    private static double Radius(Connector c) => c.Shape == ConnectorProfileType.Round ? c.Radius : 0;

    private static double MaxRadius(Element e) => PhysicalConnectors(e).Select(Radius).DefaultIfEmpty(0).Max();

    private static string? SystemName(Element e) => e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString();

    private static ElementId? SystemType(Element e) => e.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();

    private string Format(XYZ p) => $"({UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, p.X, false)}, " +
                                    $"{UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, p.Y, false)}, " +
                                    $"{UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, p.Z, false)})";

    /// <summary>
    /// Keeps a placement's warnings from popping up one dialog per branch, and records why Revit rolls one
    /// back so the report can say so instead of failing silently.
    /// </summary>
    private sealed class Failures : IFailuresPreprocessor
    {
        public List<string> Errors { get; } = [];

        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) accessor.DeleteWarning(f);
                else Errors.Add(f.GetDescriptionText());
            }
            return Errors.Count > 0 ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }
    }
}
