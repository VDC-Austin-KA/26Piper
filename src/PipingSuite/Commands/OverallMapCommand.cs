using System;
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
/// Overall package key plan from the active plan view: a duplicate with a translucent box per spool,
/// coloured by package, and a legend. Works from a preselection, or prompts for the elements.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class OverallMapCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;
        static bool IsModel(Element e) => e.Category is { CategoryType: CategoryType.Model } && e is not ElementType;

        if (uiDoc.ActiveView is not ViewPlan plan || plan.IsTemplate)
        {
            TaskDialog.Show("Overall ISO Map", "Open the floor plan to base the key map on first.");
            return Result.Cancelled;
        }

        try
        {
            var selected = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(IsModel).ToList();
            if (selected.Count == 0)
            {
                selected = uiDoc.Selection.PickObjects(ObjectType.Element, new PredicateSelectionFilter(IsModel),
                        "Select the piping for the key map (window select works), then Finish")
                    .Select(doc.GetElement).ToList();
            }

            var packages = OverallMapService.GetPackages(selected);
            if (packages.Count == 0)
            {
                TaskDialog.Show("Overall ISO Map", "None of the selected elements has a PACKAGE or BC_PACKAGE value.");
                return Result.Cancelled;
            }

            var vm = new OverallMapViewModel($"{selected.Count} element(s) in {packages.Count} package(s):", packages);
            if (!WindowHelper.ShowModal(new OverallMapWindow(vm), commandData.Application)) return Result.Cancelled;

            var result = new OverallMapService(doc).Create(plan, selected, vm.ToOptions());
            uiDoc.ActiveView = result.View;

            new TaskDialog("Overall ISO Map")
            {
                MainInstruction = $"Created \"{result.View.Name}\".",
                MainContent = $"{result.Boxes} spool box(es) across {result.Packages.Count} package(s).",
                ExpandedContent = string.Join(Environment.NewLine, result.Warnings),
            }.Show();
            return Result.Succeeded;
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
    }
}
