using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI.Selection;
using PipingSuite.Core;

namespace PipingSuite.Services;

/// <param name="BranchDiameter">Nominal branch size (internal feet).</param>
/// <param name="SymbolId">Specific olet family type; null = whatever the pipe type's routing preferences give.</param>
/// <param name="AngleDegrees">Around the main's axis, 0 = top of pipe (or view-right on vertical mains).</param>
public enum OletCopyMode { FromStart, FromEnd, SamePosition }

public sealed record OletSettings(double BranchDiameter, ElementId? SymbolId, double AngleDegrees);

/// <summary>
/// Inserts olets (taps) on a main without leaving a branch pipe: a temporary branch is drawn,
/// Revit's takeoff logic builds the olet from it, and the temporary pipe is deleted. The olet
/// is always placed on the main's centreline, so resizing keeps it centred.
/// </summary>
public sealed class OletService(Document doc, View view)
{
    private static readonly HashSet<PartType> TapTypes =
        [PartType.TapPerpendicular, PartType.TapAdjustable, PartType.SpudPerpendicular, PartType.SpudAdjustable];

    /// <summary>Sizes the main's pipe segment offers, up to the main's own size.</summary>
    public static IReadOnlyList<double> GetBranchSizes(Pipe main)
    {
        var sizes = main.PipeSegment?.GetSizes()
            .Where(s => s.UsedInSizeLists && s.NominalDiameter <= main.Diameter + 1e-6)
            .Select(s => s.NominalDiameter)
            .OrderBy(d => d)
            .ToList();
        return sizes is { Count: > 0 } ? sizes : [main.Diameter];
    }

    /// <summary>Loaded pipe fitting types whose family is a tap/spud (olets).</summary>
    public IReadOnlyList<FamilySymbol> GetOletTypes() =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_PipeFitting)
            .Cast<FamilySymbol>()
            .Where(s => s.Family.get_Parameter(BuiltInParameter.FAMILY_CONTENT_PART_TYPE)?.AsInteger() is int pt
                        && TapTypes.Contains((PartType)pt))
            .OrderBy(Rank)
            .ThenBy(s => $"{s.FamilyName}: {s.Name}", StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Elbolets/latrolets etc. only fit elbows or 45° laterals, so they must never win "Auto".
    private static readonly string[] SpecialOlets = ["elbolet", "latrolet", "sweepolet", "nipolet", "flangeolet", "coupolet"];

    private static int Rank(FamilySymbol s)
    {
        var name = $"{s.FamilyName} {s.Name}";
        bool Has(string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (SpecialOlets.Any(Has)) return 5;
        if (Has("weldolet")) return 0;
        if (Has("threadolet") || Has("sockolet")) return 1;
        if (Has("olet")) return 2;
        return 3;
    }

    /// <summary>Type that last placed successfully under "Auto"; tried first next time this session.</summary>
    private static ElementId? _lastWorkingAuto;

    /// <summary>Whether <paramref name="e"/> is a tap/olet fitting, on a pipe or loose.</summary>
    public static bool IsTap(Element e) => e is FamilyInstance { MEPModel: MechanicalFitting fitting } && TapTypes.Contains(fitting.PartType);

    /// <summary>
    /// A new olet of exactly <paramref name="symbolId"/> on <paramref name="main"/>, centred under
    /// <paramref name="outlet"/> with its branch facing <paramref name="direction"/>. Runs inside the caller's transaction.
    /// </summary>
    public FamilyInstance PlaceOn(Pipe main, XYZ outlet, XYZ direction, double diameter, ElementId symbolId) =>
        CreateOlet(main, MainLine(main).Project(outlet).XYZPoint, direction, diameter, symbolId, strict: true);

    /// <summary>Like <see cref="PlaceOn"/>, with whichever olet type works (pipe type's tap, last used, loaded olets, library).</summary>
    public FamilyInstance PlaceAuto(Pipe main, XYZ outlet, XYZ direction, double diameter) =>
        CreateOlet(main, MainLine(main).Project(outlet).XYZPoint, direction, diameter, null, strict: false);

    /// <summary>The main pipe and open branch connector, when <paramref name="e"/> is an olet on a pipe.</summary>
    public static (Pipe Main, Connector Branch)? AsOlet(Element e)
    {
        if (e is not FamilyInstance { MEPModel: MechanicalFitting fitting } fi || !TapTypes.Contains(fitting.PartType)) return null;

        Pipe? main = null;
        Connector? branch = null;
        foreach (Connector c in fitting.ConnectorManager.Connectors)
        {
            var onMain = c.AllRefs.Cast<Connector>().FirstOrDefault(r => r.Owner is Pipe && r.ConnectorType == ConnectorType.Curve);
            if (onMain is not null) main = (Pipe)onMain.Owner;
            else if (c.ConnectorType == ConnectorType.End) branch = c;
        }
        return main is not null && branch is not null ? (main, branch) : null;
    }

    public FamilyInstance Insert(Pipe main, XYZ pickedPoint, OletSettings s)
    {
        var line = MainLine(main);
        var center = line.Project(pickedPoint).XYZPoint;
        double margin = Math.Max(main.Diameter, s.BranchDiameter);
        if (center.DistanceTo(line.GetEndPoint(0)) < margin || center.DistanceTo(line.GetEndPoint(1)) < margin)
            throw new InvalidOperationException("Too close to the end of the pipe for an olet.");

        using var t = new Transaction(doc, "Insert Olet");
        t.Start();
        var olet = CreateOlet(main, center, Direction(line.Direction, s.AngleDegrees), s.BranchDiameter, s.SymbolId, strict: s.SymbolId is not null);
        t.Commit();
        return olet;
    }

    /// <summary>Re-creates the olet at the same centre and direction with a new size; a connected pipe follows.</summary>
    public FamilyInstance Resize(FamilyInstance olet, OletSettings s)
    {
        var (main, branch) = AsOlet(olet) ?? throw new InvalidOperationException("Not an olet on a pipe.");
        var center = MainLine(main).Project(branch.Origin).XYZPoint;
        var direction = branch.CoordinateSystem.BasisZ;
        var symbolId = s.SymbolId ?? olet.Symbol.Id;

        var attached = branch.AllRefs.Cast<Connector>()
            .FirstOrDefault(r => r.ConnectorType == ConnectorType.End && r.Owner is Pipe && r.Owner.Id != olet.Id);
        var attachedPipe = attached?.Owner as Pipe;
        var attachedEnd = attached?.Origin;

        using var t = new Transaction(doc, "Resize Olet");
        t.Start();
        doc.Delete(olet.Id);
        var created = CreateOlet(main, center, direction, s.BranchDiameter, symbolId, strict: s.SymbolId is not null);

        if (attachedPipe is not null && attachedEnd is not null)
        {
            attachedPipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(s.BranchDiameter);
            var newBranch = OpenConnector(created);
            var lc = (LocationCurve)attachedPipe.Location;
            var p0 = lc.Curve.GetEndPoint(0);
            var p1 = lc.Curve.GetEndPoint(1);
            lc.Curve = p0.DistanceTo(attachedEnd) < p1.DistanceTo(attachedEnd)
                ? Line.CreateBound(newBranch.Origin, p1)
                : Line.CreateBound(p0, newBranch.Origin);
            doc.Regenerate();
            Nearest(attachedPipe, newBranch.Origin).ConnectTo(newBranch);
        }
        t.Commit();
        return created;
    }

    private const string TempRuleMarker = "PipingSuite temporary olet rule";

    /// <param name="preferred">Olet type to try first; null = Auto.</param>
    /// <param name="strict">Only try <paramref name="preferred"/> (the user picked it explicitly).</param>
    private FamilyInstance CreateOlet(Pipe main, XYZ center, XYZ direction, double diameter, ElementId? preferred, bool strict)
    {
        var systemTypeId = main.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM).AsElementId();
        var temp = Pipe.Create(doc, systemTypeId, main.GetTypeId(), main.ReferenceLevel.Id,
            center, center + direction * (main.Diameter / 2 + 1.0));
        temp.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(diameter);
        doc.Regenerate();

        var rpm = main.PipeType.RoutingPreferenceManager;
        bool typeHasTap = rpm.PreferredJunctionType == PreferredJunctionType.Tap
                          && rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) > 0;

        FamilyInstance Build()
        {
            var created = doc.Create.NewTakeoffFitting(Nearest(temp, center), main)
                          ?? throw new InvalidOperationException("Revit could not create a takeoff on this pipe.");
            doc.Delete(temp.Id); // the olet stays on the main with an open branch connector
            doc.Regenerate();
            if (!created.IsValidObject)
                throw new InvalidOperationException("Revit removed the olet together with the temporary branch pipe.");
            return created;
        }

        // Each candidate runs in a sub-transaction, so a refused family leaves no trace before the next try.
        var tried = new List<string>();
        string? lastError = null;
        foreach (var symbol in Candidates(preferred, strict, typeHasTap))
        {
            tried.Add(symbol is null ? "pipe type's tap" : $"{symbol.FamilyName}: {symbol.Name}");
            using var st = new SubTransaction(doc);
            st.Start();
            try
            {
                if (symbol is not null && !symbol.IsActive) symbol.Activate();
                var olet = symbol is null ? Build() : WithTemporaryTapRule(rpm, symbol, Build);
                st.Commit();
                if (!strict) _lastWorkingAuto = symbol?.Id;
                return olet;
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or InvalidOperationException)
            {
                st.RollBack();
                lastError = ex.Message;
            }
        }

        if (tried.Count == 0)
            throw new InvalidOperationException("No olet family is loaded. Load a tap/olet pipe fitting family (e.g. Weldolet) and try again.");
        throw new InvalidOperationException($"{lastError} Tried: {string.Join(", ", tried)}.");
    }

    /// <summary>Order of olet types to try: explicit choice, last success, pipe type's tap, ranked loaded olets, library.</summary>
    private IEnumerable<FamilySymbol?> Candidates(ElementId? preferred, bool strict, bool typeHasTap)
    {
        var seen = new HashSet<ElementId>();
        var first = preferred ?? _lastWorkingAuto;
        if (first is not null && doc.GetElement(first) is FamilySymbol p && seen.Add(p.Id)) yield return p;
        if (strict) yield break;

        if (typeHasTap) yield return null;

        var loaded = GetOletTypes();
        foreach (var s in loaded.Where(s => seen.Add(s.Id))) yield return s;

        if (loaded.Count == 0 && LoadOletFromLibrary() is { } fromLibrary) yield return fromLibrary;
    }

    /// <summary>
    /// Puts the olet first in the pipe type's junction rules (all sizes) and prefers taps, runs
    /// <paramref name="create"/>, then restores the routing preferences exactly. The pipe type
    /// is therefore unchanged once the transaction commits; only the olet remains.
    /// </summary>
    private static FamilyInstance WithTemporaryTapRule(RoutingPreferenceManager rpm, FamilySymbol symbol, Func<FamilyInstance> create)
    {
        var group = RoutingPreferenceRuleGroupType.Junctions;
        var originalJunction = rpm.PreferredJunctionType;
        var rule = new RoutingPreferenceRule(symbol.Id, TempRuleMarker);
        rule.AddCriterion(PrimarySizeCriterion.All());
        rpm.AddRule(group, rule, 0);
        rpm.PreferredJunctionType = PreferredJunctionType.Tap;
        try
        {
            return create();
        }
        finally
        {
            for (int i = rpm.GetNumberOfRules(group) - 1; i >= 0; i--)
                if (rpm.GetRule(group, i).Description == TempRuleMarker) rpm.RemoveRule(group, i);
            rpm.PreferredJunctionType = originalJunction;
        }
    }

    private FamilySymbol? LoadOletFromLibrary()
    {
        foreach (var root in doc.Application.GetLibraryPaths().Values.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*olet*.rfa", SearchOption.AllDirectories)
                    .OrderBy(f => f.Contains("weldolet", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (!doc.LoadFamily(file, out Family family)) continue;
                var loaded = family.GetFamilySymbolIds().Select(doc.GetElement).OfType<FamilySymbol>().FirstOrDefault();
                if (loaded is not null && GetOletTypes().Any(s => s.Id == loaded.Id)) return loaded;
            }
        }
        return null;
    }

    /// <summary>Unit vector perpendicular to the main, rotated <paramref name="degrees"/> from "top".</summary>
    private XYZ Direction(XYZ axis, double degrees)
    {
        var reference = TopReference(axis);
        double a = degrees * Math.PI / 180;
        return (reference * Math.Cos(a) + axis.CrossProduct(reference) * Math.Sin(a)).Normalize();
    }

    /// <summary>Inverse of <see cref="Direction"/>: the angle of <paramref name="dir"/> around the axis.</summary>
    private double AngleOf(XYZ axis, XYZ dir)
    {
        var reference = TopReference(axis);
        double deg = Math.Atan2(dir.DotProduct(axis.CrossProduct(reference)), dir.DotProduct(reference)) * 180 / Math.PI;
        return (deg + 360) % 360;
    }

    /// <summary>"Top of pipe": world up, or view-right on vertical pipes.</summary>
    private XYZ TopReference(XYZ axis) =>
        Perpendicular(XYZ.BasisZ, axis) ?? Perpendicular(view.RightDirection, axis) ?? Perpendicular(XYZ.BasisX, axis)!;

    // ---------- batch copy ----------

    /// <summary>Where an olet sits on its main, independent of that main's position.</summary>
    public sealed record OletPlacement(
        double OffsetFromStart, double OffsetFromEnd, double AngleDegrees, double BranchDiameter,
        ElementId SymbolId, XYZ SourceAxis, XYZ SourceCenter, ElementId SourceMainId);

    public OletPlacement Describe(FamilyInstance olet)
    {
        var (main, branch) = AsOlet(olet) ?? throw new InvalidOperationException("Not an olet on a pipe.");
        var line = MainLine(main);
        var axis = line.Direction;
        var center = line.Project(branch.Origin).XYZPoint;
        double fromStart = (center - line.GetEndPoint(0)).DotProduct(axis);
        return new OletPlacement(fromStart, line.Length - fromStart, AngleOf(axis, branch.CoordinateSystem.BasisZ),
            branch.Radius * 2, olet.Symbol.Id, axis, center, main.Id);
    }

    /// <summary>
    /// Places a copy of <paramref name="p"/> on <paramref name="target"/>. The target is oriented like
    /// the source main so "start" means the same end. A pipe too short for the olet is extended at
    /// the needed end when <paramref name="extend"/> is set and that end is unconnected.
    /// </summary>
    /// <returns>The new olet and whether the target was lengthened.</returns>
    public (FamilyInstance Olet, bool Extended) PlaceCopy(Pipe target, OletPlacement p, OletCopyMode mode, bool extend)
    {
        var line = MainLine(target);
        bool flipped = line.Direction.DotProduct(p.SourceAxis) < 0;
        var axis = flipped ? line.Direction.Negate() : line.Direction;
        var start = line.GetEndPoint(flipped ? 1 : 0);
        double length = line.Length;

        if (p.BranchDiameter > target.Diameter + 1e-6)
            throw new InvalidOperationException("Olet is larger than this pipe.");

        double along = mode switch
        {
            OletCopyMode.FromStart => p.OffsetFromStart,
            OletCopyMode.FromEnd => length - p.OffsetFromEnd,
            _ => (p.SourceCenter - start).DotProduct(axis), // same position, projected onto this pipe
        };
        double margin = Math.Max(target.Diameter, p.BranchDiameter);

        using var t = new Transaction(doc, "Copy Olet");
        t.Start();

        bool extended = false;
        if (along < margin || along > length - margin)
        {
            if (!extend) throw new InvalidOperationException("Pipe is too short for the olet at this position.");

            var end = start + axis * length;
            if (along < margin)
            {
                // Grow backwards past the start; positions shift so "along" stays measured from the new start.
                var newStart = start - axis * (margin - along);
                EnsureOpenEnd(target, start);
                SetEnds(target, flipped, newStart, end);
                start = newStart;
                along = margin;
            }
            else
            {
                EnsureOpenEnd(target, end);
                SetEnds(target, flipped, start, start + axis * (along + margin));
            }
            extended = true;
        }

        var olet = CreateOlet(target, start + axis * along, Direction(axis, p.AngleDegrees), p.BranchDiameter, p.SymbolId, strict: false);
        t.Commit();
        return (olet, extended);
    }

    private static void EnsureOpenEnd(Pipe pipe, XYZ end)
    {
        if (Nearest(pipe, end).IsConnected)
            throw new InvalidOperationException("Pipe is too short and the end that would be extended is connected.");
    }

    /// <summary>Sets the pipe's curve from oriented start/end, keeping its original drawing direction.</summary>
    private void SetEnds(Pipe pipe, bool flipped, XYZ orientedStart, XYZ orientedEnd)
    {
        ((LocationCurve)pipe.Location).Curve = flipped
            ? Line.CreateBound(orientedEnd, orientedStart)
            : Line.CreateBound(orientedStart, orientedEnd);
        doc.Regenerate();
    }

    private static XYZ? Perpendicular(XYZ v, XYZ axis)
    {
        var p = v - axis * v.DotProduct(axis);
        return p.GetLength() < 1e-6 ? null : p.Normalize();
    }

    private static Line MainLine(Pipe main) =>
        main.Location is LocationCurve { Curve: Line line } ? line : throw new InvalidOperationException("Main must be a straight pipe.");

    private static Connector Nearest(Element e, XYZ point) =>
        PipingElements.GetConnectorManager(e)!.Connectors.Cast<Connector>()
            .Where(c => c.ConnectorType == ConnectorType.End)
            .OrderBy(c => c.Origin.DistanceTo(point))
            .First();

    private static Connector OpenConnector(FamilyInstance olet) =>
        AsOlet(olet)?.Branch ?? throw new InvalidOperationException("New olet has no open branch connector.");
}

/// <summary>Point picks on pipes (insert) or on existing olets (resize).</summary>
public sealed class OletPickFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) => elem is Pipe || OletService.AsOlet(elem) is not null;
    public bool AllowReference(Reference reference, XYZ position) => true;
}
