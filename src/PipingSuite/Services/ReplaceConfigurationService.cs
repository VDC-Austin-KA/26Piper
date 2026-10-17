using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;

namespace PipingSuite.Services;

/// <summary>
/// A branch off a header: its elements and the connector where it meets the header ("root").
/// <paramref name="Out"/> points away from the header; <paramref name="Across"/> is the direction the
/// branch heads off sideways (towards its far end), or null when it runs straight out.
/// </summary>
public sealed record Branch(IReadOnlyList<Element> Elements, ElementId RootId, XYZ Origin, XYZ Out, XYZ? Across,
    double RootRadius, Element? Header, ElementId? AssemblyId = null)
{
    public IEnumerable<ElementId> Ids => Elements.Select(e => e.Id);
}

/// <summary>Numbering for each placement: <paramref name="Start"/> on the first, stepped once per placement after.</summary>
public sealed record Numbering(string Parameter, IncrementMode Mode, string Start);

public sealed record ReplaceResult(int Replaced, IReadOnlyList<ElementId> NewIds, IReadOnlyList<string> Warnings);

/// <summary>
/// Replace Configuration: finds every branch with the same layout as a picked one (same system, same
/// element types and sizes at the same spots relative to where it leaves the header) and swaps each for
/// a correctly built branch or assembly, turned to face the way the old one did, then reconnects it to
/// the header and to whatever else the old branch was connected to.
/// </summary>
public sealed class ReplaceConfigurationService(Document doc)
{
    private const double Tol = 0.02; // ft (~1/4"): layout match and reconnect distance

    /// <summary>
    /// Describes a selection as a branch. The root is the selection's one outside connection, or the one
    /// to <paramref name="headerId"/>, or the connector nearest <paramref name="rootNear"/>; null when that
    /// is ambiguous so the caller can ask. With <paramref name="rootRadius"/>, an outside connection only
    /// counts if it is that size (a loose source branch may be connected at its far end instead).
    /// </summary>
    public Branch? Describe(IReadOnlyList<Element> elements, ElementId? headerId = null, XYZ? rootNear = null,
        ElementId? assemblyId = null, double? rootRadius = null)
    {
        var links = ExternalLinks(elements)
            .Where(l => headerId is null || l.Outside.Owner.Id == headerId // the tee/olet, or the header pipe it sits on
                        || PipingElements.GetConnectedElements(l.Outside.Owner).Any(n => n.Id == headerId))
            .Where(l => rootRadius is null || Math.Abs(Radius(l.Inside) - rootRadius.Value) < 1e-4)
            .ToList();
        if (rootNear is null && links.Count == 1) return Build(elements, links[0].Inside, links[0].Outside.Owner, assemblyId);
        if (rootNear is null) return null;

        var root = elements.SelectMany(PhysicalConnectors).MinBy(c => c.Origin.DistanceTo(rootNear))!;
        var header = ExternalLinks(elements).FirstOrDefault(l => l.Inside.Origin.IsAlmostEqualTo(root.Origin)).Outside?.Owner;
        return Build(elements, root, header, assemblyId);
    }

    /// <summary>Other branches on the same system, off the same kind of header fitting, laid out like <paramref name="bad"/>.</summary>
    public List<Branch> FindMatches(Branch bad, IReadOnlyCollection<ElementId> exclude)
    {
        var matches = new List<Branch>();
        if (bad.Header is null) return matches;

        var rootElement = doc.GetElement(bad.RootId);
        var system = SystemName(rootElement);
        var headerType = bad.Header.GetTypeId();
        var taken = new HashSet<ElementId>(exclude.Concat(bad.Ids));

        var sameType = new ElementParameterFilter(ParameterFilterRuleFactory.CreateEqualsRule(
            new ElementId(BuiltInParameter.ELEM_TYPE_PARAM), rootElement.GetTypeId()));
        foreach (var candidate in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(sameType))
        {
            if (taken.Contains(candidate.Id) || SystemName(candidate) != system) continue;
            foreach (var c in PhysicalConnectors(candidate).Where(c => c.IsConnected))
            {
                var header = Refs(c).Select(r => r.Owner).FirstOrDefault(o => o.Id != candidate.Id && o.GetTypeId() == headerType);
                if (header is null || Grow(candidate, header.Id, bad.Elements.Count) is not { } elements) continue;
                if (elements.Any(e => taken.Contains(e.Id))) continue;

                var branch = Build(elements, c, header, null);
                if (!SameLayout(bad, branch)) continue;
                matches.Add(branch);
                taken.UnionWith(branch.Ids);
                break;
            }
        }
        return matches;
    }

    /// <summary>
    /// Replaces each target with a copy of <paramref name="source"/>, one transaction per placement inside a
    /// group (a placement that fails rolls back alone and leaves the old branch in place). Placements run
    /// along the header, lowest coordinate first, which is also the numbering order.
    /// </summary>
    public ReplaceResult Replace(IReadOnlyList<Branch> targets, Branch source, Numbering? numbering)
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
            var label = $"Branch [{target.RootId.Value}]";
            using var t = new Transaction(doc, "Replace Configuration - placement");
            t.Start();
            try
            {
                var placed = Place(target, source, warnings, label);
                if (numbering is not null)
                {
                    var value = ValueSequence.Step(numbering.Start, numbering.Mode, i);
                    int set = placed.Count(e => SetValue(e, numbering.Parameter, value));
                    if (set == 0) warnings.Add($"{label}: no element has a writable \"{numbering.Parameter}\".");
                }
                if (t.Commit() != TransactionStatus.Committed)
                {
                    warnings.Add($"{label}: Revit rejected the change; the old branch was kept.");
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

    /// <summary>Deletes the target, copies the source onto its root facing its way, and reconnects every outside link.</summary>
    private List<Element> Place(Branch target, Branch source, List<string> warnings, string label)
    {
        var links = ExternalLinks(target.Elements).Select(l => (l.Outside.Owner.Id, At: l.Outside.Origin)).ToList();
        var (fs, ft) = Frames(source, target);
        var move = ft.Multiply(fs.Inverse);

        doc.Delete(target.Ids.ToList());

        var copied = ElementTransformUtils.CopyElements(doc,
            source.AssemblyId is { } a ? [a] : source.Ids.ToList(), target.Origin - source.Origin);
        if (RotationOf(move) is var (axis, angle) && angle > 1e-9)
            ElementTransformUtils.RotateElements(doc, copied, Line.CreateUnbound(target.Origin, axis), angle);
        doc.Regenerate();

        var placed = copied.Select(doc.GetElement).ToList();
        var members = placed.OfType<AssemblyInstance>().SelectMany(ai => ai.GetMemberIds()).Select(doc.GetElement);
        var all = placed.Concat(members).DistinctBy(e => e.Id).ToList();

        foreach (var (ownerId, at) in links)
        {
            var outside = doc.GetElement(ownerId) is { } owner
                ? PhysicalConnectors(owner).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(at))
                : null;
            var inside = all.SelectMany(PhysicalConnectors).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(at));
            if (outside is null || inside is null || inside.Origin.DistanceTo(at) > Tol || outside.Origin.DistanceTo(at) > Tol)
            {
                warnings.Add($"{label}: the connection at {Format(at)} was not remade (the new branch has no open end there).");
                continue;
            }
            if (Math.Abs(Radius(inside) - Radius(outside)) > 1e-4)
                warnings.Add($"{label}: sizes differ at {Format(at)}; connected anyway, check it.");
            inside.ConnectTo(outside);
        }
        return all;
    }

    private static Branch Build(IReadOnlyList<Element> elements, Connector root, Element? header, ElementId? assemblyId)
    {
        var origin = root.Origin;
        var outward = root.CoordinateSystem.BasisZ.Negate(); // a connector faces what it connects to: the header
        var far = elements.SelectMany(PhysicalConnectors).Select(c => c.Origin - origin)
            .Select(v => v - outward * v.DotProduct(outward))
            .MaxBy(v => v.GetLength());
        var across = far is not null && far.GetLength() > 0.05 ? far.Normalize() : null;
        return new Branch(elements, root.Owner.Id, origin, outward, across, Radius(root), header, assemblyId);
    }

    /// <summary>
    /// Same count, and every element matches one in the other branch by type, connector sizes and connector
    /// positions in each branch's own frame (so a mirror-side or rotated branch still matches).
    /// </summary>
    private static bool SameLayout(Branch a, Branch b)
    {
        if (a.Elements.Count != b.Elements.Count || Math.Abs(a.RootRadius - b.RootRadius) > 1e-4) return false;
        var (fa, fb) = Frames(a, b);
        var la = a.Elements.Select(e => (Key: Key(e), Points: Local(fa, e))).ToList();
        var lb = b.Elements.Select(e => (Key: Key(e), Points: Local(fb, e))).ToList();
        var used = new bool[lb.Count];
        foreach (var (key, points) in la)
        {
            int j = Enumerable.Range(0, lb.Count).FirstOrDefault(j => !used[j] && lb[j].Key == key && Matches(points, lb[j].Points), -1);
            if (j < 0) return false;
            used[j] = true;
        }
        return true;
    }

    /// <summary>
    /// Frames for comparing or mapping two branches: X straight out of the header, Y towards the far end
    /// when both branches turn, else world up (or world X for a vertical branch) for both.
    /// </summary>
    private static (Transform A, Transform B) Frames(Branch a, Branch b)
    {
        bool turn = a.Across is not null && b.Across is not null;
        return (Frame(a, turn ? a.Across : null), Frame(b, turn ? b.Across : null));
    }

    private static Transform Frame(Branch b, XYZ? across)
    {
        var y = across ?? Perpendicular(b.Out, XYZ.BasisZ) ?? Perpendicular(b.Out, XYZ.BasisX)!;
        var t = Transform.Identity;
        t.Origin = b.Origin;
        t.BasisX = b.Out;
        t.BasisY = y;
        t.BasisZ = b.Out.CrossProduct(y);
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

    /// <summary>Branch elements reachable from <paramref name="start"/> without passing the header; null past <paramref name="limit"/>.</summary>
    private static List<Element>? Grow(Element start, ElementId headerId, int limit)
    {
        var found = new Dictionary<ElementId, Element> { [start.Id] = start };
        var queue = new Queue<Element>([start]);
        while (queue.TryDequeue(out var e))
        {
            foreach (var n in PhysicalConnectors(e).Where(c => c.IsConnected).SelectMany(Refs).Select(r => r.Owner))
            {
                if (n.Id == headerId || !PipingElements.IsPiping(n) || !found.TryAdd(n.Id, n)) continue;
                if (found.Count > limit) return null;
                queue.Enqueue(n);
            }
        }
        return found.Values.ToList();
    }

    /// <summary>Targets ordered along the axis their roots spread out on most (the header's run).</summary>
    private static List<Branch> AlongHeader(IReadOnlyList<Branch> targets)
    {
        if (targets.Count < 2) return targets.ToList();
        double Spread(Func<XYZ, double> f) => targets.Max(t => f(t.Origin)) - targets.Min(t => f(t.Origin));
        Func<XYZ, double> axis = Spread(p => p.X) >= Spread(p => p.Y)
            ? (Spread(p => p.X) >= Spread(p => p.Z) ? p => p.X : p => p.Z)
            : (Spread(p => p.Y) >= Spread(p => p.Z) ? p => p.Y : p => p.Z);
        return targets.OrderBy(t => axis(t.Origin)).ToList();
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

    private static List<XYZ> Local(Transform frame, Element e)
    {
        var inverse = frame.Inverse;
        return PhysicalConnectors(e).Select(c => inverse.OfPoint(c.Origin)).ToList();
    }

    private static bool Matches(List<XYZ> a, List<XYZ> b)
    {
        if (a.Count != b.Count) return false;
        var used = new bool[b.Count];
        foreach (var p in a)
        {
            int j = Enumerable.Range(0, b.Count).FirstOrDefault(j => !used[j] && p.DistanceTo(b[j]) < Tol, -1);
            if (j < 0) return false;
            used[j] = true;
        }
        return true;
    }

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

    private static string? SystemName(Element e) => e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString();

    private string Format(XYZ p) => $"({UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, p.X, false)}, " +
                                    $"{UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, p.Y, false)}, " +
                                    $"{UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, p.Z, false)})";
}
