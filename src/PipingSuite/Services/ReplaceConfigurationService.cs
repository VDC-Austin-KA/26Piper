using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using PipingSuite.Core;

namespace PipingSuite.Services;

/// <summary>One in-line element on a branch's run, entered at <paramref name="Entry"/> and left at <paramref name="Exit"/>.</summary>
public sealed record Hop(Element Element, XYZ Entry, XYZ Exit, XYZ EntryFacing, XYZ ExitFacing, double Radius)
{
    /// <summary>Goes straight through (pipe, valve, flange, reducer) rather than turning (elbow).</summary>
    public bool Straight => EntryFacing.DotProduct(ExitFacing) < -0.999 && Entry.DistanceTo(Exit) > 1e-6;
}

/// <summary>A straight leg of a route: its direction, size and length between the corners either side of it.</summary>
public sealed record Leg(XYZ Dir, double Radius, double Length, int First, int Last);

/// <summary>
/// The run from a branch's header connection to its far end: in-line elements in order and the straight
/// legs they make. Two branches are "the same configuration" when their legs turn the same way at the
/// same sizes, whatever valves, olets or lengths they have.
/// </summary>
public sealed record Route(IReadOnlyList<Hop> Chain, IReadOnlyList<Leg> Legs, string? TerminalKey);

/// <summary>
/// A branch off a header: its elements and the connector where it meets the header ("root"), which
/// sits at <paramref name="Origin"/> facing back along <paramref name="Out"/> (away from the header).
/// When the branch includes its olet (<paramref name="OletId"/>), the root is the olet's outlet instead
/// and <paramref name="Header"/> is the pipe the olet sits on: the olet is replaced too, re-made on that pipe.
/// </summary>
public sealed record Branch(IReadOnlyList<Element> Elements, ElementId RootId, XYZ Origin, XYZ Out, double RootRadius,
    Element? Header, string? SystemName, Route Route, ElementId? OletId = null)
{
    public IEnumerable<ElementId> Ids => Elements.Select(e => e.Id);
}

/// <summary>The correctly built branch: loose elements, or the members of one assembly (copied as the assembly).</summary>
public sealed record Source(IReadOnlyList<Element> Elements, ElementId? AssemblyId);

/// <summary>An old branch and where the source lands in its place (source pose → this branch's pose).</summary>
public sealed record Target(Branch Old, Transform Place);

/// <summary>How alike a branch must be to a picked one to count as a match.</summary>
/// <param name="LengthTolerance">How far (ft) each leg's length may differ; null = any length (stretched to fit).</param>
/// <param name="AnyDirection">Also match branches facing other ways (other side of a header, another header direction).</param>
public sealed record MatchRules(double? LengthTolerance, bool AnyDirection);

/// <summary>Numbering for each placement: <paramref name="Start"/> on the first, stepped once per placement after.</summary>
public sealed record Numbering(string Parameter, IncrementMode Mode, string Start);

public sealed record ReplaceResult(int Replaced, IReadOnlyList<ElementId> NewIds, IReadOnlyList<string> Warnings);

/// <summary>
/// Replace Configuration. Branches are compared by route: the straight legs from the header connection to
/// the far end, each leg's direction and pipe size, and (by default) its length. Components are free to
/// differ, since those are what is wrong with the bad branches; <see cref="MatchRules"/> decides how strict
/// lengths and directions are. The correct branch is placed on each match's header and every leg is
/// stretched or shortened to that branch's own leg lengths, so it ends exactly where the old one did.
/// </summary>
public sealed class ReplaceConfigurationService(Document doc)
{
    private const double Tol = 0.04;     // ft (~1/2"): connections that count as the same spot
    private const double SameDir = 0.99; // cosine: directions that count as the same
    private const double MinPipe = 0.05; // ft: shortest a stretched pipe may become

    /// <summary>Turns about the root axis to try, nearest "keeps up as up" first (0, ±15°, ±30° … 180°).</summary>
    private static readonly double[] Turns =
        [0, .. Enumerable.Range(1, 11).SelectMany(k => new[] { k, -k }).Select(k => k * Math.PI / 12), Math.PI];

    private static readonly HashSet<PartType> HeaderParts =
    [
        PartType.Tee, PartType.Cross, PartType.TapPerpendicular, PartType.TapAdjustable,
        PartType.SpudPerpendicular, PartType.SpudAdjustable, PartType.LateralTee, PartType.LateralCross,
    ];

    // ---------------------------------------------------------------- picked branches

    /// <summary>
    /// Splits a selection into its separate branches (connected pieces), each with its header found
    /// automatically and its route read from the header out to the end of the selection.
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
            .Select(Describe).OfType<Branch>()
            .ToList();
    }

    /// <summary>
    /// A picked branch. Its header is its outside connection to piping with the biggest connectors (the
    /// header tee/olet); with the olet selected too, the branch starts at the olet's outlet.
    /// </summary>
    private static Branch? Describe(IReadOnlyList<Element> elements)
    {
        var link = ExternalLinks(elements)
            .OrderByDescending(l => PipingElements.IsPiping(l.Outside.Owner))
            .ThenByDescending(l => MaxRadius(l.Outside.Owner))
            .FirstOrDefault();
        if (link.Inside is null) return null;

        var ids = elements.Select(e => e.Id).ToHashSet();
        var root = link.Inside;
        Element header = link.Outside.Owner;
        ElementId? olet = null;
        if (OletService.IsTap(root.Owner) && Outlet(root.Owner, elements) is { } fed)
        {
            olet = root.Owner.Id;
            root = fed;
        }
        var (chain, next) = Walk(root, o => !ids.Contains(o.Id));
        if (chain.Count == 0) return null;
        var route = new Route(chain, Legs(chain), next is not null && PipingElements.IsPiping(next) ? Key(next) : null);
        return new Branch(elements, root.Owner.Id, root.Origin, root.CoordinateSystem.BasisZ.Negate(), Radius(root),
            header, SystemName(root.Owner), route, olet);
    }

    // ---------------------------------------------------------------- matching

    /// <summary>
    /// Every other branch with the same route as one of <paramref name="picked"/>, on any header: it starts
    /// at a header fitting's branch outlet (a tee's side, an olet's outlet) of the same size and system type,
    /// and its legs turn the same way at the same sizes. Also returns a one-line account of what was checked.
    /// </summary>
    public (List<Branch> Matches, string Report) FindMatches(IReadOnlyList<Branch> picked, IReadOnlyCollection<ElementId> exclude, MatchRules rules)
    {
        var matches = new List<Branch>();
        var taken = new HashSet<ElementId>(exclude.Concat(picked.SelectMany(b => b.Ids)));
        var reasons = new Dictionary<string, int>();
        int starts = 0;

        var fittings = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_PipeFitting)
            .WhereElementIsNotElementType().OfType<FamilyInstance>()
            .Where(f => f.MEPModel is MechanicalFitting m && HeaderParts.Contains(m.PartType)).ToList();

        foreach (var reference in picked)
        {
            var systemType = SystemType(doc.GetElement(reference.RootId));
            var frame = Frame(reference.Origin, reference.Out, 0);
            foreach (var fitting in fittings)
            {
                bool tap = OletService.IsTap(fitting);
                if (reference.OletId is not null && !tap) continue; // replacing olets: only branches off olets
                if (taken.Contains(fitting.Id)) continue;

                foreach (var outlet in BranchOutlets(fitting, reference.RootRadius))
                {
                    var root = Refs(outlet).FirstOrDefault(r => r.Owner.Id != fitting.Id);
                    if (root is null || taken.Contains(root.Owner.Id)) continue;
                    var st = SystemType(root.Owner);
                    if (systemType is not null && st is not null && st != systemType) continue;
                    starts++;

                    var (chain, _) = Walk(root, o => taken.Contains(o.Id) || (reference.Route.TerminalKey is { } k && Key(o) == k));
                    if (chain.Count == 0) continue;
                    var route = new Route(chain, Legs(chain), reference.Route.TerminalKey);
                    var why = Check(reference.Route, frame, route, root.Origin, root.CoordinateSystem.BasisZ.Negate(), rules);
                    if (why is not null)
                    {
                        reasons[why] = reasons.GetValueOrDefault(why) + 1;
                        continue;
                    }

                    var elements = chain.Select(h => h.Element).ToList();
                    elements.AddRange(Hanging(elements, taken));
                    Element header = fitting;
                    if (tap && reference.OletId is not null)
                    {
                        elements.Add(fitting);
                        header = OletService.AsOlet(fitting)?.Main ?? (Element)fitting;
                    }
                    matches.Add(new Branch(elements, root.Owner.Id, root.Origin, root.CoordinateSystem.BasisZ.Negate(),
                        reference.RootRadius, header, SystemName(root.Owner), route, tap && reference.OletId is not null ? fitting.Id : null));
                    taken.UnionWith(elements.Select(e => e.Id));
                }
            }
        }

        var report = $"checked {starts} header outlet(s) of the same size and system type: {matches.Count} match" +
                     string.Concat(reasons.OrderByDescending(r => r.Value).Select(r => $"; {r.Value} {r.Key}"));
        return (matches, report);
    }

    /// <summary>
    /// Why route <paramref name="b"/> is not a match for <paramref name="a"/> under <paramref name="rules"/>, or
    /// null when it is: same number of legs and sizes; legs running the same compass directions (or, with
    /// <see cref="MatchRules.AnyDirection"/>, the same way relative to their header, keeping up as up); and
    /// each leg within the length tolerance when there is one.
    /// </summary>
    private static string? Check(Route a, Transform frameA, Route b, XYZ origin, XYZ outward, MatchRules rules)
    {
        if (rules.AnyDirection)
        {
            if (BestTurn(a, frameA, b, origin, outward, out _) is { } why) return why;
        }
        else
        {
            if (a.Legs.Count != b.Legs.Count) return $"have {b.Legs.Count} leg(s), not {a.Legs.Count}";
            if (a.Legs.Zip(b.Legs).Any(p => p.First.Dir.DotProduct(p.Second.Dir) < SameDir)) return "run in other directions";
            if (a.Legs.Zip(b.Legs).Any(p => Math.Abs(p.First.Radius - p.Second.Radius) > 1e-4)) return "differ in pipe size";
        }
        if (rules.LengthTolerance is { } tolerance && a.Legs.Zip(b.Legs).Any(p => Math.Abs(p.First.Length - p.Second.Length) > tolerance))
            return "have different leg lengths";
        return null;
    }

    /// <summary>
    /// The upright turn that lines route <paramref name="b"/> (root at <paramref name="origin"/>, leaving along
    /// <paramref name="outward"/>) up with route <paramref name="a"/> in <paramref name="frameA"/>. Null when
    /// one does; otherwise the reason the closest attempt failed.
    /// </summary>
    private static string? BestTurn(Route a, Transform frameA, Route b, XYZ origin, XYZ outward, out Transform frameB)
    {
        frameB = Transform.Identity;
        if (a.Legs.Count != b.Legs.Count) return $"have {b.Legs.Count} leg(s), not {a.Legs.Count}";
        string worst = "turn differently";
        foreach (var turn in Turns)
        {
            var f = Frame(origin, outward, turn);
            if (frameA.Multiply(f.Inverse).OfVector(XYZ.BasisZ).Z < SameDir) continue; // up stays up
            var why = Compare(a, frameA, b, f);
            if (why is null)
            {
                frameB = f;
                return null;
            }
            if (why == "differ in pipe size") worst = why;
        }
        return worst;
    }

    private static string? Compare(Route a, Transform fa, Route b, Transform fb)
    {
        for (int k = 0; k < a.Legs.Count; k++)
        {
            if (fa.Inverse.OfVector(a.Legs[k].Dir).DotProduct(fb.Inverse.OfVector(b.Legs[k].Dir)) < SameDir) return "turn differently";
            if (Math.Abs(a.Legs[k].Radius - b.Legs[k].Radius) > 1e-4) return "differ in pipe size";
        }
        return null;
    }

    /// <summary>Connectors a branch can leave a header fitting from: an olet's outlet, a tee's side (or any reduced outlet).</summary>
    private static IEnumerable<Connector> BranchOutlets(FamilyInstance fitting, double radius)
    {
        var all = PhysicalConnectors(fitting).ToList();
        double biggest = all.Select(Radius).DefaultIfEmpty(0).Max();
        return all.Where(c => c.IsConnected && Math.Abs(Radius(c) - radius) < 1e-4)
            .Where(c => OletService.IsTap(fitting)
                ? !Refs(c).Any(r => r.ConnectorType == ConnectorType.Curve)                       // not the olet's seat
                : biggest > Radius(c) + 1e-4                                                      // reducing outlet
                  || !all.Any(o => o != c && o.CoordinateSystem.BasisZ.DotProduct(c.CoordinateSystem.BasisZ) < -0.999)); // not on the run
    }

    /// <summary>Things hanging off a branch's pipes (gauge/vent olets and what they carry): they go with it.</summary>
    private static List<Element> Hanging(IReadOnlyList<Element> chain, HashSet<ElementId> taken)
    {
        var seen = chain.Select(e => e.Id).ToHashSet();
        var found = new List<Element>();
        var queue = new Queue<Element>(chain.Where(e => e is MEPCurve)
            .SelectMany(e => PhysicalConnectors(e).Where(c => c.ConnectorType == ConnectorType.Curve).SelectMany(Refs))
            .Select(r => r.Owner).Where(o => !seen.Contains(o.Id) && !taken.Contains(o.Id)));
        while (queue.TryDequeue(out var e) && found.Count < 40)
        {
            if (!seen.Add(e.Id) || !PipingElements.IsPiping(e)) continue;
            found.Add(e);
            foreach (var n in PipingElements.GetConnectedElements(e).Where(n => !seen.Contains(n.Id) && !taken.Contains(n.Id)))
                queue.Enqueue(n);
        }
        return found;
    }

    // ---------------------------------------------------------------- routes

    /// <summary>
    /// Follows a run from <paramref name="entry"/> through elements with two ends (pipes, elbows, valves,
    /// flanges…), stopping at an open end, a split (tee), non-piping, or an element <paramref name="stop"/>
    /// rejects. Returns the hops and the element it stopped at (if any).
    /// </summary>
    private static (List<Hop> Chain, Element? Next) Walk(Connector entry, Func<Element, bool> stop, int limit = 200)
    {
        var hops = new List<Hop>();
        var at = entry;
        while (hops.Count < limit)
        {
            var e = at.Owner;
            var ends = Ends(e).ToList();
            if (ends.Count != 2) return (hops, e);
            var exit = ends.MaxBy(c => c.Origin.DistanceTo(at.Origin))!;
            hops.Add(new Hop(e, at.Origin, exit.Origin, at.CoordinateSystem.BasisZ, exit.CoordinateSystem.BasisZ, Radius(at)));
            var next = exit.IsConnected ? Refs(exit).FirstOrDefault(r => r.Owner.Id != e.Id) : null;
            if (next is null) return (hops, null);
            if (!PipingElements.IsPiping(next.Owner) || stop(next.Owner) || hops.Any(h => h.Element.Id == next.Owner.Id))
                return (hops, next.Owner);
            at = next;
        }
        return (hops, null);
    }

    /// <summary>
    /// Straight legs of a run. Lengths are measured between the corners where neighbouring legs' centrelines
    /// meet, so a long- or short-radius elbow at a corner doesn't change them.
    /// </summary>
    private static List<Leg> Legs(IReadOnlyList<Hop> chain)
    {
        var runs = new List<(XYZ Dir, XYZ Start, XYZ End, double Radius, int First, int Last)>();
        bool open = false;
        for (int i = 0; i < chain.Count; i++)
        {
            var h = chain[i];
            if (!h.Straight)
            {
                open = false;
                continue;
            }
            var dir = (h.Exit - h.Entry).Normalize();
            if (open && runs[^1].Dir.DotProduct(dir) > 0.999) runs[^1] = runs[^1] with { End = h.Exit, Last = i };
            else runs.Add((dir, h.Entry, h.Exit, h.Radius, i, i));
            open = true;
        }

        var corners = runs.Zip(runs.Skip(1), (a, b) => Corner(a.Start, a.Dir, b.Start, b.Dir) ?? a.End).ToList();
        return runs.Select((r, k) =>
        {
            var from = k == 0 ? r.Start : corners[k - 1];
            var to = k == runs.Count - 1 ? r.End : corners[k];
            return new Leg(r.Dir, r.Radius, (to - from).DotProduct(r.Dir), r.First, r.Last);
        }).ToList();
    }

    /// <summary>Where two centrelines meet (midpoint of their closest points); null if they're parallel.</summary>
    private static XYZ? Corner(XYZ p, XYZ u, XYZ q, XYZ v)
    {
        double b = u.DotProduct(v), denom = 1 - b * b;
        if (denom < 1e-9) return null;
        var w = p - q;
        double d = u.DotProduct(w), e = v.DotProduct(w);
        double s = (b * e - d) / denom, t = (e - b * d) / denom;
        return (p + u * s + q + v * t) / 2;
    }

    // ---------------------------------------------------------------- fitting the correct branch

    /// <summary>
    /// How to lay the correct branch onto <paramref name="target"/>'s header connection: its header end is
    /// what its olet feeds (or each end of the right size, one on a header first) and the turn is the upright
    /// one where its legs run the same way as the target's. Null with the reason when its route doesn't fit.
    /// Also returns the correct branch's own legs (as built, before they're stretched to the target's).
    /// </summary>
    public (Transform? Pose, string? Why, IReadOnlyList<Leg>? Legs) Fit(Source good, Branch target)
    {
        var olet = RootOlet(good.Elements);
        var body = good.Elements.Where(e => e.Id != olet?.Id).ToList();
        var ids = body.Select(e => e.Id).ToHashSet();
        var roots = olet is not null && Outlet(olet, body) is { } fed
            ? [fed]
            : body.SelectMany(PhysicalConnectors)
                .Where(c => c.ConnectorType == ConnectorType.End && Math.Abs(Radius(c) - target.RootRadius) < 1e-4)
                .Where(c => !c.IsConnected || Refs(c).Any(r => !ids.Contains(r.Owner.Id)))
                .OrderByDescending(c => c.IsConnected) // an end already on a header first
                .ToList();
        if (roots.Count == 0) return (null, "the correct branch has no end the size of this branch's header connection", null);

        var targetFrame = Frame(target.Origin, target.Out, 0);
        string why = "the correct branch's route is different";
        foreach (var root in roots)
        {
            var (chain, _) = Walk(root, o => !ids.Contains(o.Id));
            var route = new Route(chain, Legs(chain), null);
            var miss = BestTurn(target.Route, targetFrame, route, root.Origin, root.CoordinateSystem.BasisZ.Negate(), out var f);
            if (miss is null) return (targetFrame.Multiply(f.Inverse), null, route.Legs);
            why = $"the correct branch's legs {miss} from this one's";
        }
        return (null, why, null);
    }

    // ---------------------------------------------------------------- review

    /// <summary>
    /// A new isometric showing only the branches to replace (orange), those that can't be (grey) and the correct
    /// branch (green), boxed around them, for the review window's preview.
    /// </summary>
    public View3D CreateReviewView(IEnumerable<Branch> replace, IEnumerable<Branch> skip, Source good)
    {
        var groups = new (List<ElementId> Ids, Color Color)[]
        {
            (replace.SelectMany(b => b.Ids).ToList(), new Color(232, 135, 30)),
            (skip.SelectMany(b => b.Ids).ToList(), new Color(160, 160, 160)),
            (good.Elements.Select(e => e.Id).ToList(), new Color(63, 155, 90)),
        };
        var all = groups.SelectMany(g => g.Ids).Distinct().Where(id => doc.GetElement(id) is not null).ToList();

        View3D view;
        using (var t = new Transaction(doc, "Replace Configuration - review view"))
        {
            t.Start();
            var type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .First(v => v.ViewFamily == ViewFamily.ThreeDimensional);
            view = View3D.CreateIsometric(doc, type.Id);
            var taken = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = "REPLACE CONFIG - REVIEW";
            for (int i = 2; taken.Contains(name); i++) name = $"REPLACE CONFIG - REVIEW ({i})";
            view.Name = name;
            if (view.ViewTemplateId == ElementId.InvalidElementId)
            {
                view.DetailLevel = ViewDetailLevel.Fine;
                view.DisplayStyle = DisplayStyle.ShadingWithEdges;
            }
            var boxes = all.Select(id => doc.GetElement(id).get_BoundingBox(null)).OfType<BoundingBoxXYZ>().ToList();
            if (boxes.Count > 0)
            {
                var pad = new XYZ(1, 1, 1);
                view.SetSectionBox(new BoundingBoxXYZ
                {
                    Min = new XYZ(boxes.Min(b => b.Min.X), boxes.Min(b => b.Min.Y), boxes.Min(b => b.Min.Z)) - pad,
                    Max = new XYZ(boxes.Max(b => b.Max.X), boxes.Max(b => b.Max.Y), boxes.Max(b => b.Max.Z)) + pad,
                });
                view.SetCategoryHidden(new ElementId(BuiltInCategory.OST_SectionBox), true);
            }
            view.IsolateElementsTemporary(all);
            view.ConvertTemporaryHideIsolateToPermanent();
            t.Commit();
        }
        new ColorSplasherService(doc, view).Apply(groups.Where(g => g.Ids.Count > 0).Select(g => ((IReadOnlyList<ElementId>)g.Ids, g.Color)), colorLines: false);
        return view;
    }

    /// <summary>Names the grid intersection nearest a point in plan, e.g. "C-4 (2'-3")"; null when the model has no two crossing grids.</summary>
    public Func<XYZ, string?> GridLocator()
    {
        var grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(g => g.Curve is Line).Select(g => (g.Name, Line: (Line)g.Curve)).ToList();
        var points = new List<(string Name, XYZ At)>();
        for (int i = 0; i < grids.Count; i++)
        for (int j = i + 1; j < grids.Count; j++)
        {
            XYZ p0 = grids[i].Line.GetEndPoint(0), d0 = grids[i].Line.Direction, p1 = grids[j].Line.GetEndPoint(0), d1 = grids[j].Line.Direction;
            double cross = d0.X * d1.Y - d0.Y * d1.X;
            if (Math.Abs(cross) < 1e-6) continue; // parallel
            double u = ((p1.X - p0.X) * d1.Y - (p1.Y - p0.Y) * d1.X) / cross;
            var (a, b) = (grids[i].Name, grids[j].Name);
            var label = !a.Any(char.IsLetter) && b.Any(char.IsLetter) ? $"{b}-{a}" : $"{a}-{b}"; // letters first, as in "C-4"
            points.Add((label, new XYZ(p0.X + d0.X * u, p0.Y + d0.Y * u, 0)));
        }
        var units = doc.GetUnits();
        return p =>
        {
            if (points.Count == 0) return null;
            var flat = new XYZ(p.X, p.Y, 0);
            var near = points.MinBy(x => x.At.DistanceTo(flat));
            return $"{near.Name} ({UnitFormatUtils.Format(units, SpecTypeId.Length, near.At.DistanceTo(flat), false)})";
        };
    }

    // ---------------------------------------------------------------- replacing

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
            var failures = new FailureCollector();
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
            catch (Exception ex) // whatever went wrong, this placement alone is undone and the rest carry on
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
    /// Deletes the old branch, copies the source into its pose, stretches each leg to the old branch's
    /// length, then remakes the header connection (re-making the olet when it was replaced too) and every
    /// other outside connection the old branch had.
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

        StretchToFit(all, old, warnings, label);

        if (olet is not null)
        {
            var outlet = OletService.AsOlet(olet)?.Branch ?? throw new InvalidOperationException("The new olet has no outlet.");
            var start = OpenNear(all, old.Origin) ?? throw new InvalidOperationException("The correct branch has no open end to put on the olet.");
            var nudge = outlet.Origin - start.Origin; // a different olet type or header size moves the outlet a little
            if (nudge.GetLength() > Tol * 6)
                warnings.Add($"{label}: the new olet's outlet is {Format(nudge)} from the branch; check it.");
            if (nudge.GetLength() > 1e-6)
            {
                ElementTransformUtils.MoveElements(doc, copied, nudge); // top-level copies only: an assembly carries its members
                doc.Regenerate();
                start = OpenNear(all, outlet.Origin)!;
            }
            start.ConnectTo(outlet);
            all.Add(olet);
        }

        foreach (var (ownerId, at) in links)
        {
            var outside = doc.GetElement(ownerId) is { } owner
                ? PhysicalConnectors(owner).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(at))
                : null;
            var inside = OpenNear(all, at);
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
    /// Makes each leg of the placed copy as long as the old branch's: the longest pipe in the leg changes
    /// length and everything beyond it moves with it, so valves keep their distance from the header and the
    /// far end lands where the old branch ended.
    /// </summary>
    private void StretchToFit(List<Element> placed, Branch old, List<string> warnings, string label)
    {
        var ids = placed.Select(e => e.Id).ToHashSet();
        for (int k = 0; k < old.Route.Legs.Count; k++)
        {
            var root = OpenNear(placed, old.Origin);
            if (root is null) return;
            var (chain, _) = Walk(root, e => !ids.Contains(e.Id));
            var legs = Legs(chain);
            if (legs.Count != old.Route.Legs.Count)
            {
                warnings.Add($"{label}: the copy has {legs.Count} leg(s), the old branch {old.Route.Legs.Count}; lengths were left as built.");
                return;
            }
            var leg = legs[k];
            double change = old.Route.Legs[k].Length - leg.Length;
            if (Math.Abs(change) < 1e-4) continue;

            int at = Enumerable.Range(leg.First, leg.Last - leg.First + 1)
                .Where(i => chain[i].Element is MEPCurve { Location: LocationCurve { Curve: Line } })
                .OrderByDescending(i => chain[i].Entry.DistanceTo(chain[i].Exit)).DefaultIfEmpty(-1).First();
            if (at < 0)
            {
                warnings.Add($"{label}: leg {k + 1} has no straight pipe to stretch by {Format(leg.Dir * change)}.");
                continue;
            }
            var pipe = (MEPCurve)chain[at].Element;
            var (entry, exit) = (chain[at].Entry, chain[at].Exit);
            if (entry.DistanceTo(exit) + change < MinPipe)
                throw new InvalidOperationException($"leg {k + 1} would need a pipe shorter than nothing ({Format(leg.Dir * change)}).");

            // Everything past this pipe: the rest of the run and whatever hangs off it.
            var beyond = Downstream(chain, at, placed);
            var exitConnector = Ends(pipe).MinBy(c => c.Origin.DistanceTo(exit))!;
            var neighbour = exitConnector.IsConnected ? Refs(exitConnector).FirstOrDefault(r => r.Owner.Id != pipe.Id) : null;
            if (neighbour is not null) exitConnector.DisconnectFrom(neighbour);
            var shift = leg.Dir * change;
            if (beyond.Count > 0) ElementTransformUtils.MoveElements(doc, beyond, shift);
            ((LocationCurve)pipe.Location).Curve = Line.CreateBound(entry, exit + shift);
            doc.Regenerate();
            if (neighbour is not null)
            {
                var mine = Ends(pipe).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(exit + shift));
                var theirs = doc.GetElement(neighbour.Owner.Id) is { } n
                    ? Ends(n).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(exit + shift))
                    : null;
                if (mine is not null && theirs is not null) mine.ConnectTo(theirs);
            }
        }
    }

    /// <summary>Ids of the run past hop <paramref name="at"/>, plus elements hanging off those (gauge olets…), not off earlier hops.</summary>
    private static List<ElementId> Downstream(IReadOnlyList<Hop> chain, int at, IReadOnlyList<Element> placed)
    {
        var position = chain.Select((h, i) => (h.Element.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var placedIds = placed.Select(e => e.Id).ToHashSet();
        var result = chain.Skip(at + 1).Select(h => h.Element.Id).ToList();

        // Hanging elements belong to the hop they are (eventually) attached to.
        var owner = new Dictionary<ElementId, int>();
        var queue = new Queue<(Element E, int Hop)>(chain.Select((h, i) => (h.Element, i)));
        while (queue.TryDequeue(out var item))
            foreach (var n in PipingElements.GetConnectedElements(item.E))
                if (placedIds.Contains(n.Id) && !position.ContainsKey(n.Id) && owner.TryAdd(n.Id, item.Hop))
                    queue.Enqueue((n, item.Hop));
        result.AddRange(owner.Where(o => o.Value > at).Select(o => o.Key));
        return result;
    }

    // ---------------------------------------------------------------- helpers

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

    /// <summary>Targets ordered along the axis their roots spread out on most: the placement and numbering order.</summary>
    public static List<Target> AlongHeader(IReadOnlyList<Target> targets)
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

    private Connector? OpenNear(IEnumerable<Element> elements, XYZ at) =>
        elements.Where(e => e.IsValidObject).SelectMany(PhysicalConnectors).Where(c => !c.IsConnected).MinBy(c => c.Origin.DistanceTo(at));

    private static IEnumerable<Connector> Refs(Connector c) =>
        c.AllRefs.Cast<Connector>().Where(r => r.ConnectorType != ConnectorType.Logical && r.Owner is not null and not MEPSystem);

    private static IEnumerable<Connector> PhysicalConnectors(Element e) =>
        PipingElements.GetConnectorManager(e)?.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType != ConnectorType.Logical && c.Domain == Domain.DomainPiping) ?? [];

    private static IEnumerable<Connector> Ends(Element e) => PhysicalConnectors(e).Where(c => c.ConnectorType == ConnectorType.End);

    private static double Radius(Connector c) => c.Shape == ConnectorProfileType.Round ? c.Radius : 0;

    private static double MaxRadius(Element e) => PhysicalConnectors(e).Select(Radius).DefaultIfEmpty(0).Max();

    private static string? SystemName(Element e) => e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString();

    private static ElementId? SystemType(Element e) => e.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId();

    private string Format(XYZ p) => $"({UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, p.X, false)}, " +
                                    $"{UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, p.Y, false)}, " +
                                    $"{UnitFormatUtils.Format(doc.GetUnits(), SpecTypeId.Length, p.Z, false)})";
}
