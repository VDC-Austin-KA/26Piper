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
/// Turns a selection into a locked, coloured and tagged isometric named after its package.
/// Works from a preselection, or prompts for the elements.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class IsoMapCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;
        static bool IsModel(Element e) => e.Category is { CategoryType: CategoryType.Model } && e is not ElementType;

        try
        {
            var selected = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(IsModel).ToList();
            if (selected.Count == 0)
            {
                selected = uiDoc.Selection.PickObjects(ObjectType.Element, new PredicateSelectionFilter(IsModel),
                        "Select the elements for the ISO map, then Finish")
                    .Select(doc.GetElement).ToList();
            }
            if (selected.Count == 0) return Result.Cancelled;

            var service = new IsoMapService(doc);
            var packages = selected.Select(IsoMapService.GetPackage).OfType<string>()
                .GroupBy(p => p).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
            var tagStatus = service.FindTagType() is null
                ? $"\"{IsoMapService.TagFamily} : {IsoMapService.TagType}\" is not loaded; load it to tag line segments."
                : $"Uses \"{IsoMapService.TagFamily} : {IsoMapService.TagType}\".";

            var vm = new IsoMapViewModel(
                $"{selected.Count} element(s) selected" + (packages.Count > 1 ? $" across {packages.Count} packages." : "."),
                packages, tagStatus);
            if (!WindowHelper.ShowModal(new IsoMapWindow(vm), commandData.Application)) return Result.Cancelled;

            var result = service.Create(selected, vm.ToOptions());
            uiDoc.ActiveView = result.View;

            new TaskDialog("ISO Map")
            {
                MainInstruction = $"Created \"{result.View.Name}\".",
                MainContent = $"{selected.Count} element(s), {result.ColoredValues} color(s), {result.Tags} tag(s).",
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
