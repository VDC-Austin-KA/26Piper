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
    /// <summary>An unnumbered branch built like numbered ones: give it their BC_LINE SEGMENT.</summary>
    Share,
    /// <summary>Elements with an empty BC_ value: give them their line's.</summary>
    Fill,
    /// <summary>A few elements carrying a different number than the rest of their line.</summary>
    Outlier,
    /// <summary>A numbered branch built like a DETAIL branch: give it the DETAIL value.</summary>
    MatchDetail,
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
/// Identical branches sharing one BC_LINE SEGMENT are intended (one EZ-ISO serves them all), so they aren't
/// duplicates, and an unnumbered branch built like numbered ones gets their number. A BC_LINE SEGMENT containing
/// "DETAIL" is the reference for an EZ-ISO and is never overwritten.
/// </summary>
public sealed class EzIsoService(Document doc)
{
    public const string SystemParam = "BC_SYSTEM", PackageParam = "BC_PACKAGE", SegmentParam = "BC_LINE SEGMENT";
    private static readonly string[] Params = [SystemParam, PackageParam, SegmentParam];
    private const double Touching = 0.125 / 12; // ft
    private const int NearlyPerFoot = 24;        // branches whose pipe lengths agree to 1/2" are built the same

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
        // A DETAIL value on any of a line's elements is its number: it's the reference the EZ-ISO is made from.
        foreach (var line in lines)
            for (int i = 0; i < 3; i++)
                line.Value[i] = (i == 2 ? Majority(line.Elements.Select(e => values[e.Id][i]).Where(IsDetail)) : null)
                                ?? Majority(line.Elements.Select(e => values[e.Id][i]));
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
                    else if (!Same(have, want) && !(i == 2 && IsDetail(have)))
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

        // Branches (lines touching only the one they come off) built alike are one configuration and may share a
        // number; every other line is its own.
        var build = lines.ToDictionary(l => l, l => IsoViewsService.Signature(l.Elements, NearlyPerFoot));
        var config = lines.ToDictionary(l => l, l => adjacent[l].Distinct().Count() <= 1 ? $"{Pkg(l)}|{build[l]}" : $"#{l.FirstId}");
        var sized = lines.Where(l => l.Value[0] is not null && l.Value[1] is not null).ToList();
        var handled = new HashSet<Line>();

        // DETAIL lines are references: never renumbered. Flag one whose lines aren't all built alike.
        foreach (var group in sized.Where(l => l.Numbered && IsDetail(l.Value[2])).GroupBy(l => l.Key, StringComparer.OrdinalIgnoreCase))
        {
            handled.UnionWith(group);
            int builds = group.Select(l => build[l]).Distinct().Count();
            if (builds < 2) continue;
            issues.Add(new EzIsoIssue(EzIsoKind.Review, $"{group.Key} ({group.Count()} lines)",
                $"{builds} different builds share this DETAIL; give the ones that differ their own number by hand", Ids(group.SelectMany(l => l.Elements)), []));
            fixing.UnionWith(group);
        }

        // A numbered branch built like a DETAIL branch, but numbered differently: offer the DETAIL (unticked; it changes a number).
        var detailOf = sized.Where(l => l.Numbered && IsDetail(l.Value[2])).GroupBy(l => config[l])
            .ToDictionary(g => g.Key, g => Majority(g.Select(l => l.Value[2]))!);
        foreach (var line in sized.Where(l => l.Numbered && !handled.Contains(l)))
        {
            if (!detailOf.TryGetValue(config[line], out var detail)) continue;
            issues.Add(Write(EzIsoKind.MatchDetail, [line], detail, $"Built like the {detail} branches: number as {line.Value[0]}-{line.Value[1]}-{detail}"));
            handled.Add(line);
        }

        // Duplicates: separate configurations with one number. The longest keeps it; each other configuration
        // gets the next free one, shared by its identical branches.
        foreach (var group in sized.Where(l => l.Numbered && !handled.Contains(l)).GroupBy(l => l.Key, StringComparer.OrdinalIgnoreCase))
        {
            var configs = group.GroupBy(l => config[l])
                .OrderByDescending(c => c.Max(l => l.Length)).ThenByDescending(c => c.Max(l => l.Elements.Count)).ThenBy(c => c.Min(l => l.FirstId))
                .Skip(1).ToList();
            foreach (var c in configs)
            {
                var first = c.First();
                var number = next(first.Value[0]!, first.Value[1]!);
                issues.Add(Write(EzIsoKind.Renumber, [.. c], number, $"Duplicate of another line numbered {first.Key}: number as {first.Value[0]}-{first.Value[1]}-{number}"));
            }
        }

        // Unnumbered: identical branches get the number their numbered twins carry (a DETAIL first), else one new number between them.
        var numberOf = sized.Where(l => l.Numbered && !config[l].StartsWith('#')).GroupBy(l => config[l])
            .ToDictionary(g => g.Key, g => g.Select(l => l.Value[2]).Where(IsDetail).FirstOrDefault() ?? Majority(g.Select(l => l.Value[2]))!);
        foreach (var c in sized.Where(l => l.Value[2] is null).GroupBy(l => config[l]).OrderBy(c => c.Min(l => l.FirstId)))
        {
            var first = c.First();
            if (numberOf.TryGetValue(c.Key, out var twin))
                issues.Add(Write(EzIsoKind.Share, [.. c], twin, $"Unnumbered, built like the {twin} branches: number as {first.Value[0]}-{first.Value[1]}-{twin}"));
            else
            {
                var number = next(first.Value[0]!, first.Value[1]!);
                issues.Add(Write(EzIsoKind.Renumber, [.. c], number, $"Unnumbered: number as {first.Value[0]}-{first.Value[1]}-{number}"));
            }
        }

        EzIsoIssue Write(EzIsoKind kind, List<Line> group, string number, string what)
        {
            fixing.UnionWith(group);
            var elements = group.SelectMany(l => l.Elements).ToList();
            var label = group.Count == 1 ? $"{group[0].Key} ({elements.Count})" : $"{group[0].Key} ({group.Count} branches)";
            return new EzIsoIssue(kind, label, what + (group.Count > 1 ? $" ({group.Count} identical branches)" : string.Empty), Ids(elements),
                elements.Where(e => !IsDetail(values[e.Id][2])).Select(e => new ParamWrite(e.Id, SegmentParam, number)).ToList());
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

        var writes = issues.Where(i => i.Writes.Count > 0).OrderBy(i => i.Kind switch
        {
            EzIsoKind.Fill => 0,
            EzIsoKind.Outlier or EzIsoKind.MatchDetail or EzIsoKind.Share => 1,
            _ => 2,
        }).ToList();
        if (writes.Count > 0)
        {
            using var t = new Transaction(doc, "EZ-ISO line numbers");
            t.Start();
            foreach (var issue in writes)
            {
                // A DETAIL segment is never overwritten, whatever the scan proposed.
                var kept = issue.Writes.Where(w => w.Name == SegmentParam && doc.GetElement(w.Id) is { } e
                                                   && IsDetail(ParameterHelper.GetValueByName(e, SegmentParam)) && !Same(w.Value, ParameterHelper.GetValueByName(e, SegmentParam)!)).ToList();
                if (kept.Count > 0) notes.Add($"{issue.Line}: left {kept.Count} DETAIL value(s) as they are.");
                var failed = issue.Writes.Except(kept).Where(w => !Set(doc.GetElement(w.Id), w.Name, w.Value)).ToList();
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
            if (ParameterHelper.GetValueByName(e, SegmentParam) is not { } seg || !seg.Any(char.IsDigit) // DETAIL A isn't a number
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

    /// <summary>A "DETAIL A"-style segment: the reference for an EZ-ISO, never overwritten.</summary>
    public static bool IsDetail(string? v) => v?.Contains("DETAIL", StringComparison.OrdinalIgnoreCase) == true;

    private static string Pkg(Line l) => $"{l.Value[0]}-{l.Value[1]}".ToUpperInvariant();

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static List<ElementId> Ids(IEnumerable<Element> elements) => elements.Select(e => e.Id).ToList();

    private static List<ElementId> Ids(IEnumerable<ElementId> ids) => ids.Distinct().ToList();

    private static IEnumerable<Connector> PhysicalConnectors(Element e) =>
        PipingElements.GetConnectorManager(e)?.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType != ConnectorType.Logical && c.Domain == Domain.DomainPiping) ?? [];

    private static double Radius(Connector c) => c.Shape == ConnectorProfileType.Round ? c.Radius : 0;
}
