using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using PipingSuite.Core;
using PipingSuite.Services;
using PipingSuite.UI.ViewModels;
using PipingSuite.UI.Views;

namespace PipingSuite.Commands;

/// <summary>
/// Pick the element to connect, then what it connects to. Small offsets are corrected by
/// moving only the smaller pipe (by default), then the pair is joined: direct, union,
/// transition, elbow, or by splitting the main and inserting a tee.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class AlignConnectCommand : IExternalCommand
{
    private const double DefaultToleranceFeet = 0.5; // 6"

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;
        var units = doc.GetUnits();

        try
        {
            var filter = new PipingSelectionFilter();
            var first = doc.GetElement(uiDoc.Selection.PickObject(ObjectType.Element, filter,
                "Pick the pipe/fitting to connect (usually the smaller branch)"));
            var second = doc.GetElement(uiDoc.Selection.PickObject(ObjectType.Element, filter,
                "Pick what it connects to (tee, fitting, or main pipe)"));
            if (first.Id == second.Id) return Result.Cancelled;

            var vm = new AlignOptionsViewModel(Describe(first, units), Describe(second, units),
                UnitFormatUtils.Format(units, SpecTypeId.Length, DefaultToleranceFeet, false));
            if (!WindowHelper.ShowModal(new AlignOptionsWindow(vm), commandData.Application))
                return Result.Cancelled;

            if (!UnitFormatUtils.TryParse(units, SpecTypeId.Length, vm.ToleranceText, out double tolerance) || tolerance <= 0)
            {
                TaskDialog.Show("Align & Connect", $"\"{vm.ToleranceText}\" is not a valid length.");
                return Result.Cancelled;
            }

            var service = new PipeAlignService(doc);
            var plan = service.Plan(first, second, vm.Mover);
            var summary = service.Execute(plan, new AlignOptions(vm.AlignX, vm.AlignY, vm.AlignZ, vm.Mover, tolerance));

            TaskDialog.Show("Align & Connect", $"{summary}{Environment.NewLine}Moved element: {Describe(plan.Mover, units)}");
            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Transaction already rolled back by its using-scope; nothing changed in the model.
            TaskDialog.Show("Align & Connect", $"Not connected: {ex.Message}");
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static string Describe(Element e, Units units)
    {
        var size = e is Pipe pipe ? $" Ø{UnitFormatUtils.Format(units, SpecTypeId.PipeSize, pipe.Diameter, false)}" : "";
        return $"{e.Category?.Name}: {e.Name}{size} [{e.Id.Value}]";
    }
}
