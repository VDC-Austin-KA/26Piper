using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace PipingSuite.Core;

/// <summary>Category tests and connector-graph traversal shared by all piping features.</summary>
public static class PipingElements
{
    private static readonly HashSet<BuiltInCategory> PipingCategories =
    [
        BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_PipeFitting,
        BuiltInCategory.OST_PipeAccessory,
    ];

    public static bool IsPiping(Element e) =>
        e.Category is { } c && PipingCategories.Contains(c.BuiltInCategory);

    public static bool IsPipe(Element e) =>
        e.Category is { } c && c.BuiltInCategory is BuiltInCategory.OST_PipeCurves or BuiltInCategory.OST_FlexPipeCurves;

    public static ConnectorManager? GetConnectorManager(Element e) => e switch
    {
        MEPCurve curve => curve.ConnectorManager,
        FamilyInstance fi => fi.MEPModel?.ConnectorManager,
        _ => null,
    };

    /// <summary>
    /// Elements physically connected to <paramref name="e"/>. Logical connectors and
    /// system owners (PipingSystem) are skipped so only real neighbours are returned.
    /// </summary>
    public static IEnumerable<Element> GetConnectedElements(Element e)
    {
        var cm = GetConnectorManager(e);
        if (cm is null) yield break;

        foreach (Connector c in cm.Connectors)
        {
            if (c.ConnectorType == ConnectorType.Logical || !c.IsConnected) continue;

            foreach (Connector r in c.AllRefs)
            {
                if (r.ConnectorType == ConnectorType.Logical) continue;
                var owner = r.Owner;
                if (owner is null || owner is MEPSystem || owner.Id == e.Id) continue;
                yield return owner;
            }
        }
    }
}

/// <summary>Restricts interactive picking to pipes, fittings and pipe accessories.</summary>
public sealed class PipingSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) => PipingElements.IsPiping(elem);
    public bool AllowReference(Reference reference, XYZ position) => false;
}
