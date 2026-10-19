using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using PipingSuite.Core;

namespace PipingSuite.Services;

public enum EzIsoKind
{
    /// <summary>A duplicate or unnumbered line: give it the next free BC_LINE SEGMENT.</summary>
    Renumber,
    /// <summary>Elements with an empty BC_ value: give them their line's.</summary>
    Fill,
    /// <summary>A few elements carrying a different number than the rest of their line.</summary>
    Outlier,
    /// <summary>A tap or open end that touches what it should be connected to.</summary>
    Connect,
    /// <summary>Needs a person: shown, not fixed.</summary>
    Review,
}

public sealed record ParamWrite(ElementId Id, string Name, string Value);

/// <param name="Line">The line's number as EZ-ISO will read it ({BC_SYSTEM}-{BC_PACKAGE}-{BC_LINE SEGMENT}).</param>
/// <param name="Ids">What to zoom to.</param>
public sealed record EzIsoIssue(EzIsoKind Kind, string Line, string What, IReadOnlyList<ElementId> Ids,
    IReadOnlyList<ParamWrite> Writes, Disconnect? Disconnect = null)
{
    public bool CanFix => Kind != EzIsoKind.Review;
}

/// <param name="Correct">Lines already numbered uniquely and consistently; nothing in them is touched.</param>
public sealed record EzIsoScan(int Lines, int Correct, IReadOnlyList<EzIsoIssue> Issues);

/// <summary>
/// EZ-ISO Check: splits the selected piping into lines (each branch from its header tee/olet to its far end,
/// each header run between branch points; runs and branches already sharing one number stay one line), reads
/// each line's {BC_SYSTEM}-{BC_PACKAGE}-{BC_LINE SEGMENT}, and suggests the smallest fixes: fill empty values,
/// pull outliers back to their line, give duplicate and unnumbered lines the next free number, and reconnect
/// taps and ends that touch without being connected. Correct, unique lines are never renumbered.
/// </summary>
public sealed class EzIsoService(Document doc)
{
    public const string SystemParam = "BC_SYSTEM", PackageParam = "BC_PACKAGE", SegmentParam = "BC_LINE SEGMENT";
    private static readonly string[] Params = [SystemParam, PackageParam, SegmentParam];
    private const double Touching = 0.125 / 12; // ft

    private static readonly HashSet<PartType> BranchParts =
    [
        PartType.Tee, PartType.Cross, PartType.LateralTee, PartType.LateralCross,
        PartType.TapPerpendicular, PartType.TapAdjustable, PartType.SpudPerpendicular, PartType.SpudAdjustable,
    ];

    private sealed class Line(List<Element> elements)
    {
        public List<Element> Elements { get; } = elements;
        public string?[] Value { get; } = new string?[3]; // system, package, segment
        public string Key => string.Join("-", Value.Select(v => v ?? "?"));
        public bool Numbered => Value.All(v => v is not null);
        public double Length => Elements.OfType<MEPCurve>().Sum(c => (c.Location as LocationCurve)?.Curve.Length ?? 0);
        public long FirstId => Elements.Min(e => e.Id.Value);
    }

    public EzIsoScan Scan(IReadOnlyList<Element> scope)
    {
        var piping = scope.Where(e => e.IsValidObject && PipingElements.IsPiping(e)).DistinctBy(e => e.Id).ToList();
        var values = piping.ToDictionary(e => e.Id, e => Params.Select(p => ParameterHelper.GetValueByName(e, p)).ToArray());
        var (lines, adjacent) = Lines(piping, values);

        // Each line's value is the most common one among its elements; a line with none borrows from the line it comes off.
        foreach (var line in lines)
            for (int i = 0; i < 3; i++)
                line.Value[i] = Majority(line.Elements.Select(e => values[e.Id][i]));
        foreach (var line in lines)
            for (int i = 0; i < 2; i++)
                line.Value[i] ??= adjacent[line].Select(n => n.Value[i]).FirstOrDefault(v => v is not null);

        var issues = new List<EzIsoIssue>();
        var fixing = new HashSet<Line>();
        var next = NextFree();

        foreach (var line in lines)
        {
            var label = $"{line.Key} ({line.Elements.Count})";
            if (line.Value[0] is null || line.Value[1] is null)
            {
                issues.Add(new EzIsoIssue(EzIsoKind.Review, label,
                    $"No {(line.Value[0] is null ? SystemParam : PackageParam)} on this line or the line it comes off: set it, then Rescan",
                    Ids(line.Elements), []));
                fixing.Add(line);
                continue;
            }

            var fills = new List<ParamWrite>();
            var outliers = new List<ParamWrite>();
            var odd = new HashSet<ElementId>();
            foreach (var e in line.Elements)
                for (int i = 0; i < 3; i++)
                {
                    if (line.Value[i] is not { } want) continue;
                    var have = values[e.Id][i];
                    if (have is null) fills.Add(new ParamWrite(e.Id, Params[i], want));
                    else if (!Same(have, want))
                    {
                        outliers.Add(new ParamWrite(e.Id, Params[i], want));
                        odd.Add(e.Id);
                    }
                }
            if (fills.Count > 0)
                issues.Add(new EzIsoIssue(EzIsoKind.Fill, label,
                    $"{fills.Select(w => w.Id).Distinct().Count()} element(s) with an empty BC_ value get the line's ({string.Join(", ", fills.Select(w => w.Name).Distinct())})",
                    Ids(fills.Select(w => w.Id)), fills));
            if (outliers.Count > 0)
                issues.Add(new EzIsoIssue(EzIsoKind.Outlier, label,
                    $"{odd.Count} of {line.Elements.Count} element(s) carry a different number (e.g. {Key(values[odd.First()])}); set to {line.Key}",
                    [.. odd], outliers));
            if (fills.Count > 0 || outliers.Count > 0) fixing.Add(line);
        }

        // Duplicates: separate lines with one number. The longest keeps it; the others get the next free one.
        var renumber = lines.Where(l => l.Value[0] is not null && l.Value[1] is not null && l.Value[2] is null).ToList();
        foreach (var group in lines.Where(l => l.Numbered).GroupBy(l => l.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var keep = group.OrderByDescending(l => l.Length).ThenByDescending(l => l.Elements.Count).ThenBy(l => l.FirstId).First();
            renumber.AddRange(group.Where(l => l != keep));
        }
        foreach (var line in renumber.OrderBy(l => l.FirstId))
        {
            var number = next(line.Value[0]!, line.Value[1]!);
            var was = line.Value[2] is null ? "Unnumbered" : $"Duplicate of another line numbered {line.Key}";
            issues.Add(new EzIsoIssue(EzIsoKind.Renumber, $"{line.Key} ({line.Elements.Count})",
                $"{was}: number as {line.Value[0]}-{line.Value[1]}-{number}", Ids(line.Elements),
                line.Elements.Select(e => new ParamWrite(e.Id, SegmentParam, number)).ToList()));
            fixing.Add(line);
        }

        // Taps and ends touching what they should be connected to.
        var lineOf = lines.SelectMany(l => l.Elements.Select(e => (e.Id, l))).ToDictionary(x => x.Id, x => x.l);
        var disconnects = new DisconnectService(doc);
        foreach (var d in disconnects.Find(disconnects.Scope(scope), Touching, sameSystemType: true))
            issues.Add(new EzIsoIssue(d.CanFix ? EzIsoKind.Connect : EzIsoKind.Review,
                lineOf.TryGetValue(d.Owner, out var l) ? $"{l.Key} ({l.Elements.Count})" : string.Empty,
                d.What, [.. new[] { d.Owner, d.Other }.OfType<ElementId>()], [], d));

        return new EzIsoScan(lines.Count, lines.Count(l => !fixing.Contains(l)), issues.OrderBy(i => i.Kind).ThenBy(i => i.Line, ColorSplasherService.NaturalComparer.Instance).ToList());
    }

    /// <summary>Applies the chosen fixes as one undo: values first (fills, outliers, then new numbers), then connections.</summary>
    public (int Done, List<string> Notes) Apply(IReadOnlyList<EzIsoIssue> issues)
    {
        var notes = new List<string>();
        int done = 0;
        using var group = new TransactionGroup(doc, "EZ-ISO Check");
        group.Start();

        var writes = issues.Where(i => i.Kind is EzIsoKind.Fill or EzIsoKind.Outlier or EzIsoKind.Renumber).OrderBy(i => i.Kind switch
        {
            EzIsoKind.Fill => 0,
            EzIsoKind.Outlier => 1,
            _ => 2,
        }).ToList();
        if (writes.Count > 0)
        {
            using var t = new Transaction(doc, "EZ-ISO line numbers");
            t.Start();
            foreach (var issue in writes)
            {
                var failed = issue.Writes.Where(w => !Set(doc.GetElement(w.Id), w.Name, w.Value)).ToList();
                if (failed.Count == 0) done++;
                else notes.Add($"{issue.Line}: {failed.Count} value(s) couldn't be written (no writable instance parameter {failed[0].Name} on [{failed[0].Id.Value}]).");
            }
            t.Commit();
        }

        var connects = issues.Select(i => i.Disconnect).OfType<Disconnect>().ToList();
        if (connects.Count > 0)
        {
            var (connected, more) = new DisconnectService(doc).Fix(connects);
            done += connected;
            notes.AddRange(more);
        }

        group.Assimilate();
        return (done, notes);
    }

    /// <summary>
    /// Lines in the selection, and which lines touch which. Every connection joins two elements into one line
    /// except at a branch fitting, which belongs to one side only (the side carrying its number, else an olet goes
    /// with its branch and a tee with its run). Lines that touch and already share one number are then one line.
    /// </summary>
    private static (List<Line> Lines, Dictionary<Line, List<Line>> Adjacent) Lines(List<Element> piping, Dictionary<ElementId, string?[]> values)
    {
        var byId = piping.ToDictionary(e => e.Id);
        var links = piping.SelectMany(e => PipingElements.GetConnectedElements(e).Where(n => byId.ContainsKey(n.Id))
                .Select(n => e.Id.Value < n.Id.Value ? (A: e.Id, B: n.Id) : (A: n.Id, B: e.Id)))
            .Distinct().ToList();

        var chosen = new Dictionary<ElementId, ElementId?>();
        foreach (var e in piping.Where(IsBranchFitting))
            chosen[e.Id] = Side(e, byId, values);
        bool Joins((ElementId A, ElementId B) l)
        {
            bool fa = chosen.TryGetValue(l.A, out var ca), fb = chosen.TryGetValue(l.B, out var cb);
            return (!fa && !fb) || (fa && ca == l.B) || (fb && cb == l.A);
        }

        var uf = new UnionFind(byId.Keys);
        foreach (var l in links.Where(Joins)) uf.Union(l.A, l.B);

        // Same number on both sides of a split: one line (a header numbered straight through its tees, a vent on its pipe).
        var number = byId.Keys.GroupBy(uf.Find).ToDictionary(g => g.Key, g => Majority(g.Select(id => Key(values[id]))));
        foreach (var (a, b) in links)
        {
            var (ra, rb) = (uf.Find(a), uf.Find(b));
            if (ra == rb || number[ra] is not { } na || !Same(na, number[rb] ?? string.Empty)) continue;
            uf.Union(ra, rb);
            number[uf.Find(a)] = na;
        }

        var lines = byId.Keys.GroupBy(uf.Find).ToDictionary(g => g.Key, g => new Line(g.Select(id => byId[id]).ToList()));
        var adjacent = lines.Values.ToDictionary(l => l, _ => new List<Line>());
        foreach (var (a, b) in links)
        {
            var (la, lb) = (lines[uf.Find(a)], lines[uf.Find(b)]);
            if (la == lb) continue;
            adjacent[la].Add(lb);
            adjacent[lb].Add(la);
        }
        return (lines.Values.OrderBy(l => l.FirstId).ToList(), adjacent);
    }

    private static bool IsBranchFitting(Element e) =>
        OletService.IsTap(e) || e is FamilyInstance { MEPModel: MechanicalFitting f } && BranchParts.Contains(f.PartType);

    /// <summary>The neighbour a branch fitting stays with.</summary>
    private static ElementId? Side(Element fitting, Dictionary<ElementId, Element> byId, Dictionary<ElementId, string?[]> values)
    {
        var mine = values[fitting.Id][2];
        var sides = PhysicalConnectors(fitting).Where(c => c.IsConnected)
            .SelectMany(c => c.AllRefs.Cast<Connector>().Where(r => r.ConnectorType != ConnectorType.Logical && r.Owner is not null and not MEPSystem && byId.ContainsKey(r.Owner.Id))
                .Select(r => (Mine: c, Ref: r)))
            .ToList();
        if (sides.Count == 0) return null;
        if (mine is not null && sides.FirstOrDefault(s => values[s.Ref.Owner.Id][2] is { } v && Same(v, mine)) is { Ref: not null } match)
            return match.Ref.Owner.Id;

        if (OletService.IsTap(fitting)) // the outlet: the connector not seated on a pipe's side
            return (sides.FirstOrDefault(s => s.Ref.ConnectorType != ConnectorType.Curve).Ref ?? sides[0].Ref).Owner.Id;

        // A tee's run: one of the two connectors facing opposite ways, the bigger first.
        var all = PhysicalConnectors(fitting).ToList();
        var run = sides.Where(s => all.Any(o => o.Id != s.Mine.Id && o.CoordinateSystem.BasisZ.DotProduct(s.Mine.CoordinateSystem.BasisZ) < -0.999))
            .OrderByDescending(s => Radius(s.Mine)).ToList();
        return (run.Count > 0 ? run[0] : sides[0]).Ref.Owner.Id;
    }

    /// <summary>Next unused segment for a system and package, after the highest anywhere in the model, keeping its zero padding.</summary>
    private Func<string, string, string> NextFree()
    {
        var highest = new Dictionary<(string, string), string>();
        var categories = new ElementMulticategoryFilter(new List<BuiltInCategory>
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
        });
        var cmp = ColorSplasherService.NaturalComparer.Instance;
        foreach (var e in new FilteredElementCollector(doc).WhereElementIsNotElementType().WherePasses(categories))
        {
            if (ParameterHelper.GetValueByName(e, SegmentParam) is not { } seg
                || ParameterHelper.GetValueByName(e, SystemParam) is not { } sys
                || ParameterHelper.GetValueByName(e, PackageParam) is not { } pkg) continue;
            var k = (sys.ToUpperInvariant(), pkg.ToUpperInvariant());
            if (!highest.TryGetValue(k, out var have) || cmp.Compare(seg, have) > 0) highest[k] = seg;
        }
        return (sys, pkg) =>
        {
            var k = (sys.ToUpperInvariant(), pkg.ToUpperInvariant());
            var n = highest.TryGetValue(k, out var have) && have.Any(char.IsDigit) ? ValueSequence.Step(have, IncrementMode.Number, 1) : "01";
            highest[k] = n;
            return n;
        };
    }

    private static bool Set(Element? e, string name, string value)
    {
        var p = e?.LookupParameter(name);
        if (p is null || p.IsReadOnly) return false;
        return p.StorageType switch
        {
            StorageType.String => p.Set(value),
            StorageType.Integer => int.TryParse(value, out var n) && p.Set(n),
            _ => false,
        };
    }

    private static string? Majority(IEnumerable<string?> values) =>
        values.OfType<string>().GroupBy(v => v, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

    private static string? Key(string?[] v) => v.All(x => x is not null) ? string.Join("-", v) : null;

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<ElementId> Ids(IEnumerable<Element> elements) => elements.Select(e => e.Id).ToList();

    private static List<ElementId> Ids(IEnumerable<ElementId> ids) => ids.Distinct().ToList();

    private static IEnumerable<Connector> PhysicalConnectors(Element e) =>
        PipingElements.GetConnectorManager(e)?.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType != ConnectorType.Logical && c.Domain == Domain.DomainPiping) ?? [];

    private static double Radius(Connector c) => c.Shape == ConnectorProfileType.Round ? c.Radius : 0;
}
