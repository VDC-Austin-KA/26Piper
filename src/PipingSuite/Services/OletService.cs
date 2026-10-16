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
            .OrderBy(s => $"{s.FamilyName}: {s.Name}", StringComparer.OrdinalIgnoreCase)
            .ToList();

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
        var olet = CreateOlet(main, center, Direction(line.Direction, s.AngleDegrees), s.BranchDiameter, s.SymbolId);
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
        var created = CreateOlet(main, center, direction, s.BranchDiameter, symbolId);

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

    private FamilyInstance CreateOlet(Pipe main, XYZ center, XYZ direction, double diameter, ElementId? symbolId)
    {
        var systemTypeId = main.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM).AsElementId();
        var temp = Pipe.Create(doc, systemTypeId, main.GetTypeId(), main.ReferenceLevel.Id,
            center, center + direction * (main.Diameter / 2 + 1.0));
        temp.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM).Set(diameter);
        doc.Regenerate();

        var rpm = main.PipeType.RoutingPreferenceManager;
        bool typeHasTap = rpm.PreferredJunctionType == PreferredJunctionType.Tap
                          && rpm.GetNumberOfRules(RoutingPreferenceRuleGroupType.Junctions) > 0;

        // Use the pipe type's own tap only when nothing specific was asked for and it has one;
        // otherwise drive the takeoff with a chosen/loaded olet family via a temporary rule.
        var oletSymbol = symbolId is null && typeHasTap ? null : ResolveOletSymbol(symbolId);
        FamilyInstance Build()
        {
            var created = doc.Create.NewTakeoffFitting(Nearest(temp, center), main)
                          ?? throw new InvalidOperationException("Revit could not create a takeoff on this pipe.");
            doc.Delete(temp.Id); // the olet stays on the main with an open branch connector
            doc.Regenerate();
            return created;
        }

        var olet = oletSymbol is null ? Build() : WithTemporaryTapRule(rpm, oletSymbol, Build);
        if (!olet.IsValidObject)
            throw new InvalidOperationException("Revit removed the olet together with the temporary branch pipe.");
        return olet;
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

    /// <summary>Chosen type, else the first loaded olet type, else an olet family from the Revit library.</summary>
    private FamilySymbol ResolveOletSymbol(ElementId? symbolId)
    {
        var symbol = (symbolId is not null ? doc.GetElement(symbolId) as FamilySymbol : null)
                     ?? GetOletTypes().FirstOrDefault()
                     ?? LoadOletFromLibrary()
                     ?? throw new InvalidOperationException(
                         "No olet family is loaded. Load a tap/olet pipe fitting family (e.g. Weldolet) and try again.");
        if (!symbol.IsActive) symbol.Activate();
        return symbol;
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
        var reference = Perpendicular(XYZ.BasisZ, axis);
        if (reference is null) reference = Perpendicular(view.RightDirection, axis) ?? Perpendicular(XYZ.BasisX, axis)!;
        double a = degrees * Math.PI / 180;
        return (reference * Math.Cos(a) + axis.CrossProduct(reference) * Math.Sin(a)).Normalize();
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
