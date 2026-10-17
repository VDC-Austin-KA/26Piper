using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using PipingSuite.Core;
using PipingSuite.Services;
using PipingSuite.UI.ViewModels;
using PipingSuite.UI.Views;

namespace PipingSuite.Commands;

/// <summary>
/// Pick a wrongly built branch and a correctly built one (or its assembly); every branch laid out like
/// the wrong one on the same system is swapped for the correct one, reconnected, and optionally numbered.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ReplaceConfigurationCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;
        var service = new ReplaceConfigurationService(doc);

        try
        {
            // 1. The wrong branch, and where it meets the header.
            var badElements = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(PipingElements.IsPiping).ToList();
            if (badElements.Count == 0)
                badElements = Pick(uiDoc, new PipingSelectionFilter(),
                    "Select the INCORRECT branch (its pipes, fittings, valves; not the header tee/olet), then Finish");
            if (ReplaceConfigurationService.ExternalLinks(badElements).Count == 0)
                return Fail("The selected branch isn't connected to a header.");
            var bad = service.Describe(badElements)
                      ?? service.Describe(badElements, headerId: uiDoc.Selection.PickObject(ObjectType.Element,
                          "Pick the header pipe or the tee/olet this branch comes off").ElementId)
                      ?? throw new InvalidOperationException("The picked element isn't connected to that branch.");

            // 2. The correct branch or assembly, and its header end.
            var picked = Pick(uiDoc, new PredicateSelectionFilter(e => PipingElements.IsPiping(e) || e is AssemblyInstance),
                "Select the CORRECT branch or its assembly, then Finish");
            var assemblies = picked.OfType<AssemblyInstance>().ToList();
            var goodElements = picked.Where(e => e is not AssemblyInstance)
                .Concat(assemblies.SelectMany(a => a.GetMemberIds()).Select(doc.GetElement))
                .DistinctBy(e => e.Id).ToList();
            if (goodElements.Any(e => badElements.Any(b => b.Id == e.Id)))
                return Fail("The correct branch overlaps the incorrect one.");
            var assemblyId = assemblies.Count == 1 && picked.Count == 1 ? assemblies[0].Id : null;
            var good = service.Describe(goodElements, assemblyId: assemblyId, rootRadius: bad.RootRadius)
                       ?? service.Describe(goodElements, rootNear: uiDoc.Selection.PickObject(ObjectType.PointOnElement,
                           "Click the end of the CORRECT branch that connects to the header").GlobalPoint, assemblyId: assemblyId);

            // 3. Everything laid out like the wrong branch, shown highlighted behind the dialog.
            var matches = service.FindMatches(bad!, goodElements.Select(e => e.Id).ToList());
            uiDoc.Selection.SetElementIds(bad!.Ids.Concat(matches.SelectMany(m => m.Ids)).ToList());

            var system = doc.GetElement(bad.RootId).get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? "(no system)";
            var vm = new ReplaceConfigurationViewModel(
                $"Found {matches.Count} other branch(es) on \"{system}\" laid out like the picked one ({bad.Elements.Count} elements each).",
                matches.Count, SourceValues(doc, good!, goodElements));
            if (!WindowHelper.ShowModal(new ReplaceConfigurationWindow(vm), commandData.Application)) return Result.Cancelled;

            var targets = vm.ReplaceAll ? [bad, .. matches] : new List<Branch> { bad };
            var result = service.Replace(targets, good!, vm.ToNumbering());
            uiDoc.Selection.SetElementIds(result.NewIds.ToList());

            new TaskDialog("Replace Configuration")
            {
                MainInstruction = $"Replaced {result.Replaced} of {targets.Count} branch(es).",
                MainContent = result.Warnings.Count > 0 ? $"{result.Warnings.Count} note(s); expand for details." : "All connections were remade.",
                ExpandedContent = string.Join(Environment.NewLine, result.Warnings),
            }.Show();
            return result.Replaced > 0 ? Result.Succeeded : Result.Cancelled;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }

        static Result Fail(string text)
        {
            TaskDialog.Show("Replace Configuration", text);
            return Result.Cancelled;
        }
    }

    private static List<Element> Pick(UIDocument uiDoc, ISelectionFilter filter, string prompt) =>
        uiDoc.Selection.PickObjects(ObjectType.Element, filter, prompt).Select(uiDoc.Document.GetElement).ToList();

    /// <summary>Writable text/integer parameters on the correct branch (and its assembly), with their first value found.</summary>
    private static Dictionary<string, string> SourceValues(Document doc, Branch good, IEnumerable<Element> elements)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var owners = good.AssemblyId is { } a ? elements.Prepend(doc.GetElement(a)) : elements;
        foreach (var e in owners)
        foreach (Parameter p in e.Parameters)
        {
            if (p.IsReadOnly || p.StorageType is not (StorageType.String or StorageType.Integer)) continue;
            var v = p.StorageType == StorageType.String ? p.AsString() : p.AsInteger().ToString();
            if (!values.TryGetValue(p.Definition.Name, out var have) || string.IsNullOrEmpty(have))
                values[p.Definition.Name] = v ?? string.Empty;
        }
        return values;
    }
}
