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

/// <summary>Select configurations; one tagged, dimensioned isometric is made per DETAIL type (or per configuration).</summary>
[Transaction(TransactionMode.Manual)]
public sealed class IsoViewsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;

        try
        {
            var selected = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(PipingElements.IsPiping).ToList();
            if (selected.Count == 0)
                selected = uiDoc.Selection.PickObjects(ObjectType.Element, new PipingSelectionFilter(),
                        "Select the configurations to make ISO views of (window select works), then Finish")
                    .Select(doc.GetElement).ToList();
            if (selected.Count == 0) return Result.Cancelled;

            var detail = IsoViewsService.FindDetailParameter(selected) ?? string.Empty;
            var configurations = IsoViewsService.Configurations(selected, detail);
            var (types, notes) = IsoViewsService.Types(configurations);
            var tagStatus = new IsoMapService(doc).FindTagType() is null
                ? $"\"{IsoMapService.TagFamily} : {IsoMapService.TagType}\" is not loaded; load it to tag line segments."
                : $"Uses \"{IsoMapService.TagFamily} : {IsoMapService.TagType}\".";

            var vm = new IsoViewsViewModel(
                $"{configurations.Count} configuration(s) of {types.Count} type(s)" +
                (detail.Length > 0 ? $", typed by {detail}:" : " (no DETAIL values found; typed by how they're built):"),
                types.Select(t => $"{t.Name}: {t.Members.Count} configuration(s)").Concat(notes).ToList(), detail, tagStatus);
            if (!WindowHelper.ShowModal(new IsoViewsWindow(vm), commandData.Application)) return Result.Cancelled;

            var options = vm.ToOptions();
            if (!string.Equals(options.DetailParameter, detail, StringComparison.OrdinalIgnoreCase))
                (types, _) = IsoViewsService.Types(IsoViewsService.Configurations(selected, options.DetailParameter));

            var result = new IsoViewsService(doc).Create(types, options);
            if (result.Views.Count > 0) uiDoc.ActiveView = result.Views[0];

            new TaskDialog("ISO Views")
            {
                MainInstruction = $"Created {result.Views.Count} ISO view(s).",
                MainContent = string.Join(Environment.NewLine, result.Views.Select(v => v.Name).Take(12)) + (result.Views.Count > 12 ? Environment.NewLine + "…" : string.Empty),
                ExpandedContent = string.Join(Environment.NewLine, result.Notes),
            }.Show();
            return result.Views.Count > 0 ? Result.Succeeded : Result.Cancelled;
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
