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

/// <summary>Resizes the selected piping, or the one-size branch through a picked element, to a chosen size.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class ResizeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;

        try
        {
            var selected = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(PipingElements.IsPiping).ToList();
            var scope = selected.Count > 0
                ? ResizeService.FromSelection(selected)
                : ResizeService.Branch(doc.GetElement(uiDoc.Selection.PickObject(ObjectType.Element, new PipingSelectionFilter(),
                    "Pick a pipe, fitting or valve on the branch to resize (everything connected to it in the same size, up to its tee or olet)")));
            uiDoc.Selection.SetElementIds(scope.Select(e => e.Id).ToList()); // show what will change

            var current = scope.Select(ResizeService.SizeOf).OfType<double>().GroupBy(d => Math.Round(d, 6)).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
            if (current is null) return Fail("Nothing in the selection has a single size to change.");
            var units = doc.GetUnits();
            var sizes = ResizeService.Sizes(scope).Select(d => new LabeledValue<double>(UnitFormatUtils.Format(units, SpecTypeId.PipeSize, d, false), d)).ToList();
            int pipes = scope.Count(PipingElements.IsPipe), parts = scope.Count - pipes;
            var vm = new ResizeViewModel(
                $"{pipes} pipe(s) and {parts} fitting(s) or accessory(ies), now {UnitFormatUtils.Format(units, SpecTypeId.PipeSize, current.Value, false)} (selected in Revit).", sizes, current.Value);
            if (!WindowHelper.ShowModal(new ResizeWindow(vm), commandData.Application)) return Result.Cancelled;
            var (size, keepBottom) = vm.ToOptions();

            var result = new ResizeService(doc, doc.ActiveView).Resize(scope, size, keepBottom);
            new TaskDialog("Resize")
            {
                MainInstruction = $"Resized {result.Pipes} pipe(s) and {result.Parts} fitting(s) or accessory(ies) to {UnitFormatUtils.Format(units, SpecTypeId.PipeSize, size, false)}" +
                                  (result.Swapped > 0 ? $" ({result.Swapped} by changing type)." : "."),
                MainContent = string.Join(Environment.NewLine, result.Notes),
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

        static Result Fail(string text)
        {
            TaskDialog.Show("Resize", text);
            return Result.Cancelled;
        }
    }
}
