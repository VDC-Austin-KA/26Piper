using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using PipingSuite.Core;

namespace PipingSuite.Services;

public sealed record ResizeResult(int Pipes, int Parts, int Swapped, IReadOnlyList<string> Notes);

/// <summary>
/// Resize: changes a branch (or selection) of one-size piping to another size. Pipes take the new diameter;
/// fittings, flanges and accessories are resized through their connectors, or swapped to another type of the same
/// family in the new size. Where the branch comes off a tee or olet, that outlet is resized too. Connections Revit
/// drops are made again, stretching pipes to the larger fittings. The bottom of pipe is kept where the branch can
/// rise or drop freely (it hangs from a riser, or touches nothing else); otherwise centrelines are kept and the
/// report gives the change in bottom of pipe. One undo reverts it all.
/// </summary>
public sealed class ResizeService(Document doc, View view)
{
    private const double Tol = 1.0 / 384; // ft (1/32")

    private static readonly HashSet<PartType> BranchParts =
    [
        PartType.Tee, PartType.Cross, PartType.LateralTee, PartType.LateralCross,
        PartType.TapPerpendicular, PartType.TapAdjustable, PartType.SpudPerpendicular, PartType.SpudAdjustable,
    ];

    private sealed record Link(ElementId A, int ConnA, ElementId B, int ConnB);

    /// <summary>The element's one size: a pipe's diameter, or a fitting's when all its round connectors share it.</summary>
    public static double? SizeOf(Element e)
    {
        if (e is MEPCurve curve) return curve.Diameter;
        var sizes = Round(e).Select(c => c.Radius * 2).ToList();
        return sizes.Count > 0 && sizes.All(s => Math.Abs(s - sizes[0]) < Tol) ? sizes[0] : null;
    }

    /// <summary>
    /// The branch through <paramref name="start"/>: everything connected to it in the same size, up to (not
    /// including) size changes and the tees, crosses and olets it comes off or that come off it.
    /// </summary>
    public static List<Element> Branch(Element start)
    {
        if (SizeOf(start) is not { } size) return [start];
        var found = new Dictionary<ElementId, Element> { [start.Id] = start };
        var queue = new Queue<Element>([start]);
        while (queue.Count > 0)
            foreach (var n in PipingElements.GetConnectedElements(queue.Dequeue()))
                if (!found.ContainsKey(n.Id) && PipingElements.IsPiping(n) && !IsBranchFitting(n) && SizeOf(n) is { } s && Math.Abs(s - size) < Tol)
                {
                    found[n.Id] = n;
                    queue.Enqueue(n);
                }
        return [.. found.Values];
    }

    /// <summary>The selected piping, plus fittings and accessories between selected pieces (all their neighbours selected).</summary>
    public static List<Element> FromSelection(IEnumerable<Element> selected)
    {
        var set = selected.Where(PipingElements.IsPiping).ToDictionary(e => e.Id);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var n in set.Values.SelectMany(PipingElements.GetConnectedElements).Where(n => n is FamilyInstance && !set.ContainsKey(n.Id)).DistinctBy(n => n.Id).ToList())
                if (PipingElements.GetConnectedElements(n).All(m => set.ContainsKey(m.Id)))
                {
                    set[n.Id] = n;
                    grew = true;
                }
        }
        return [.. set.Values];
    }

    /// <summary>Sizes the pipes' segment offers.</summary>
    public static IReadOnlyList<double> Sizes(IEnumerable<Element> scope)
    {
        var pipe = scope.OfType<Pipe>().FirstOrDefault();
        var sizes = pipe?.PipeSegment?.GetSizes().Where(s => s.UsedInSizeLists).Select(s => s.NominalDiameter).OrderBy(d => d).ToList();
        return sizes is { Count: > 0 } ? sizes : scope.Select(SizeOf).OfType<double>().Distinct().ToList();
    }

    public ResizeResult Resize(IReadOnlyList<Element> scope, double size, bool keepBottom)
    {
        var notes = new List<string>();
        var units = doc.GetUnits();
        string Len(double ft) => UnitFormatUtils.Format(units, SpecTypeId.Length, ft, false);
        string Size(double d) => UnitFormatUtils.Format(units, SpecTypeId.PipeSize, d, false);

        var ids = scope.Select(e => e.Id).ToHashSet();
        var links = Links(scope);
        var boundary = links.Where(l => !ids.Contains(l.B)).ToList();
        int pipes = 0, parts = 0, swapped = 0;
        var oldBottom = Bottom(scope.OfType<MEPCurve>());
        var oldOuter = scope.OfType<MEPCurve>().Select(Outer).DefaultIfEmpty(0).Max();

        using var group = new TransactionGroup(doc, "Resize");
        group.Start();

        // Olets the branch comes off: re-made at the new outlet size (their own transaction).
        foreach (var l in boundary.ToList())
        {
            if (doc.GetElement(l.B) is not FamilyInstance olet || OletService.AsOlet(olet) is null) continue;
            try
            {
                new OletService(doc, view).Resize(olet, new OletSettings(size, null, 0));
                notes.Add($"Olet [{l.B.Value}] re-made with a {Size(size)} outlet.");
            }
            catch (Exception ex)
            {
                notes.Add($"Olet [{l.B.Value}] couldn't be resized ({ex.Message}); its outlet still needs a transition.");
            }
            boundary.Remove(l);
        }
        links = Links(scope.Where(e => e.IsValidObject).ToList()); // the olet's connectors are new
        boundary = links.Where(l => !ids.Contains(l.B)).ToList();

        var live = scope.Where(e => e.IsValidObject).ToList();

        var errors = new List<string>();
        using (var t = new Transaction(doc, "Resize - sizes"))
        {
            Lenient(t, errors);
            t.Start();
            foreach (var p in live.OfType<MEPCurve>())
            {
                if (Math.Abs(p.Diameter - size) < Tol) continue;
                if (p.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM) is { IsReadOnly: false } d && d.Set(size)) pipes++;
                else notes.Add($"Pipe [{p.Id.Value}] wouldn't take {Size(size)}.");
            }
            doc.Regenerate();

            foreach (var fi in live.OfType<FamilyInstance>())
            {
                if (Round(fi).All(c => Math.Abs(c.Radius * 2 - size) < Tol)) { parts++; continue; } // resized with its pipes
                if (SetSize(fi, size)) { parts++; continue; }
                if (SwapType(fi, size) is { } type)
                {
                    parts++;
                    swapped++;
                    notes.Add($"[{fi.Id.Value}] {fi.Symbol?.FamilyName}: changed to type \"{type}\" for {Size(size)}.");
                }
                else notes.Add($"[{fi.Id.Value}] {fi.Symbol?.FamilyName} has no {Size(size)} size or type in its family; it was left as it was.");
            }

            // Tees (and other fittings) the branch comes off: resize just the outlet the branch joins.
            foreach (var l in boundary)
            {
                var other = doc.GetElement(l.B);
                if (other is MEPCurve) { notes.Add($"The resized piping meets pipe [{l.B.Value}], which stays its size: put a transition there."); continue; }
                if (other is not FamilyInstance fi || Connector(fi, l.ConnB) is not { } outlet || Math.Abs(outlet.Radius * 2 - size) < Tol) continue;
                var rest = Round(fi).Where(c => c.Id != outlet.Id).Select(c => (c, c.Radius)).ToList();
                double was = outlet.Radius;
                try
                {
                    outlet.Radius = size / 2;
                    doc.Regenerate();
                    if (rest.Any(x => Math.Abs(x.c.Radius - x.Radius) > Tol / 2)) // the whole fitting followed: put it back
                    {
                        outlet.Radius = was;
                        doc.Regenerate();
                        notes.Add($"[{fi.Id.Value}] {fi.Symbol.FamilyName} can't take a different outlet size; swap it for a reducing fitting or add a transition.");
                    }
                    else notes.Add($"[{fi.Id.Value}] {fi.Symbol.FamilyName}: outlet resized to {Size(size)}.");
                }
                catch (Exception)
                {
                    notes.Add($"[{fi.Id.Value}] {fi.Symbol.FamilyName}'s outlet can't be resized; add a transition there.");
                }
            }
            if (t.Commit() != TransactionStatus.Committed) notes.Add("Revit rolled the size change back: " + string.Join("; ", errors.Take(3)));
        }

        // Bottom of pipe: raise (or drop) the branch by the change in outside radius where it's free to move.
        live = scope.Where(e => e.IsValidObject).ToList();
        double delta = live.OfType<MEPCurve>().Select(Outer).DefaultIfEmpty(0).Max() - oldOuter;
        if (Math.Abs(delta) > Tol)
        {
            var anchors = boundary.Select(l => doc.GetElement(l.A)).OfType<Element>().DistinctBy(e => e.Id).ToList();
            bool free = anchors.Count == 0, risers = anchors.Count > 0 && anchors.All(IsVertical);
            if (keepBottom && (free || risers))
            {
                using var t = new Transaction(doc, "Resize - bottom of pipe");
                Lenient(t, errors);
                t.Start();
                var move = live.Where(e => anchors.All(a => a.Id != e.Id)).Select(e => e.Id).ToList();
                if (move.Count > 0) ElementTransformUtils.MoveElements(doc, move, XYZ.BasisZ * delta);
                t.Commit();
                notes.Add($"Bottom of pipe kept: the branch was {(delta > 0 ? "raised" : "lowered")} {Len(Math.Abs(delta))}" +
                          (risers ? ", and the riser(s) it hangs from stretched to suit." : "."));
            }
            else
                notes.Add($"Centrelines kept, so the bottom of pipe is now {Len(Math.Abs(delta))} {(delta > 0 ? "lower" : "higher")}" +
                          (keepBottom ? $": the branch joins {string.Join(", ", anchors.Select(a => $"[{a.Id.Value}]").Take(3))} on a level or sloped connection, so it can't move without breaking it." : "."));
        }

        // Connections Revit dropped: made again, stretching pipes to the resized fittings.
        var broken = Reconnect(links, notes);
        if (broken > 0) notes.Add($"{broken} connection(s) couldn't be made again; run Connect Disconnects on the branch.");

        var newBottom = Bottom(scope.Where(e => e.IsValidObject).OfType<MEPCurve>());
        if (oldBottom is { } b0 && newBottom is { } b1)
            notes.Add($"Lowest bottom of pipe: {Len(b0)} before, {Len(b1)} after ({(Math.Abs(b1 - b0) < Tol ? "unchanged" : $"{(b1 > b0 ? "up" : "down")} {Len(Math.Abs(b1 - b0))}")}).");
        if (errors.Count > 0) notes.Add("Revit reported: " + string.Join("; ", errors.Distinct().Take(4)));

        group.Assimilate();
        return new ResizeResult(pipes, parts, swapped, notes);
    }

    /// <summary>Resizes a fitting or accessory through its connectors, else through a pipe-size instance parameter.</summary>
    private bool SetSize(FamilyInstance fi, double size)
    {
        bool Done() => Round(fi).All(c => Math.Abs(c.Radius * 2 - size) < Tol);
        try
        {
            foreach (var c in Round(fi).Where(c => Math.Abs(c.Radius * 2 - size) >= Tol)) c.Radius = size / 2;
            doc.Regenerate();
            if (Done()) return true;
        }
        catch (Exception) { /* not connector-driven: try its size parameter */ }

        foreach (var p in fi.Parameters.Cast<Parameter>().Where(p => !p.IsReadOnly && p.StorageType == StorageType.Double && p.Definition.GetDataType() == SpecTypeId.PipeSize))
        {
            try
            {
                p.Set(p.Definition.Name.Contains("Radius", StringComparison.OrdinalIgnoreCase) ? size / 2 : size);
                doc.Regenerate();
                if (Done()) return true;
            }
            catch (Exception) { }
        }
        return false;
    }

    /// <summary>Another type of the same family that comes in the new size (types named for the size tried first); null when none does.</summary>
    private string? SwapType(FamilyInstance fi, double size)
    {
        var original = fi.Symbol;
        var label = UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.PipeSize, size, false);
        var candidates = original.Family.GetFamilySymbolIds().Where(id => id != original.Id).Select(doc.GetElement).OfType<FamilySymbol>()
            .OrderByDescending(s => s.Name.Contains(label.Trim('"', ' '), StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var s in candidates)
        {
            try
            {
                if (!s.IsActive) s.Activate();
                fi.ChangeTypeId(s.Id);
                doc.Regenerate();
                if (Round(fi).All(c => Math.Abs(c.Radius * 2 - size) < Tol) || SetSize(fi, size)) return s.Name;
            }
            catch (Exception) { }
        }
        if (fi.Symbol.Id != original.Id) fi.ChangeTypeId(original.Id);
        return null;
    }

    /// <summary>Re-makes dropped connections: touching ends are joined, a pipe end short of (or past) its fitting is moved onto it first.</summary>
    private int Reconnect(IReadOnlyList<Link> links, List<string> notes)
    {
        int broken = 0;
        using var t = new Transaction(doc, "Resize - connections");
        var errors = new List<string>();
        Lenient(t, errors);
        t.Start();
        foreach (var l in links)
        {
            if (doc.GetElement(l.A) is not { } a || doc.GetElement(l.B) is not { } b || Connector(a, l.ConnA) is not { } ca || Connector(b, l.ConnB) is not { } cb) continue;
            if (ca.IsConnectedTo(cb)) continue;
            try
            {
                if (ca.Origin.DistanceTo(cb.Origin) > Tol && !(Stretch(a, ca, cb.Origin) || Stretch(b, cb, ca.Origin)))
                {
                    broken++;
                    continue;
                }
                ca.ConnectTo(cb);
            }
            catch (Exception ex)
            {
                broken++;
                notes.Add($"[{l.A.Value}] to [{l.B.Value}]: {ex.Message.Split('\n')[0]}");
            }
        }
        t.Commit();
        return broken;
    }

    /// <summary>Moves a straight pipe's end at <paramref name="end"/> to <paramref name="to"/> when that keeps its direction.</summary>
    private static bool Stretch(Element e, Connector end, XYZ to)
    {
        if (e is not MEPCurve { Location: LocationCurve { Curve: Line line } lc }) return false;
        bool first = line.GetEndPoint(0).DistanceTo(end.Origin) <= line.GetEndPoint(1).DistanceTo(end.Origin);
        var keep = line.GetEndPoint(first ? 1 : 0);
        if (keep.DistanceTo(to) < 0.01 || (to - keep).Normalize().DotProduct((end.Origin - keep).Normalize()) < 0.9999) return false;
        lc.Curve = first ? Line.CreateBound(to, keep) : Line.CreateBound(keep, to);
        return true;
    }

    /// <summary>Every connection from the scope: to each other and to what lies outside it.</summary>
    private static List<Link> Links(IReadOnlyList<Element> scope) =>
        scope.SelectMany(e => Round(e).Where(c => c.IsConnected)
                .SelectMany(c => c.AllRefs.Cast<Connector>()
                    .Where(r => r.ConnectorType != ConnectorType.Logical && r.Owner is { } o && o is not MEPSystem && o.Id != e.Id)
                    .Select(r => new Link(e.Id, c.Id, r.Owner.Id, r.Id))))
            .ToList();

    private static Connector? Connector(Element e, int id)
    {
        try { return PipingElements.GetConnectorManager(e)?.Lookup(id); }
        catch (Exception) { return null; }
    }

    private static IEnumerable<Connector> Round(Element e) =>
        PipingElements.GetConnectorManager(e)?.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType != ConnectorType.Logical && c.Domain == Domain.DomainPiping && c.Shape == ConnectorProfileType.Round) ?? [];

    private static bool IsBranchFitting(Element e) =>
        OletService.IsTap(e) || e is FamilyInstance { MEPModel: MechanicalFitting f } && BranchParts.Contains(f.PartType);

    private static bool IsVertical(Element e) =>
        e is MEPCurve { Location: LocationCurve { Curve: Line l } } && Math.Abs(l.Direction.Normalize().Z) > 0.999;

    private static double Outer(MEPCurve p) =>
        (p.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER)?.AsDouble() is > 0 and var od ? od : p.Diameter) / 2;

    /// <summary>Lowest bottom of pipe among the level and sloped pipes (not risers).</summary>
    private static double? Bottom(IEnumerable<MEPCurve> pipes) =>
        pipes.Where(p => !IsVertical(p) && p.Location is LocationCurve { Curve: Line })
            .Select(p => ((LocationCurve)p.Location).Curve is var c ? (double?)(Math.Min(c.GetEndPoint(0).Z, c.GetEndPoint(1).Z) - Outer(p)) : null)
            .Min();

    /// <summary>Warnings are dismissed; errors take Revit's default resolution (e.g. disconnect) and are reported.</summary>
    private static void Lenient(Transaction t, List<string> errors)
    {
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new LenientFailures(errors));
        options.SetClearAfterRollback(true);
        t.SetFailureHandlingOptions(options);
    }

    private sealed class LenientFailures(List<string> errors) : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) { accessor.DeleteWarning(f); continue; }
                errors.Add(f.GetDescriptionText());
                if (f.HasResolutions()) accessor.ResolveFailure(f);
            }
            return FailureProcessingResult.ProceedWithCommit;
        }
    }
}
