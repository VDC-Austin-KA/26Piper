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

/// <summary>Feature A2 (copy): captures chosen parameter values from one source element.</summary>
[Transaction(TransactionMode.ReadOnly)]
public sealed class CopyParametersCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;

        try
        {
            var selected = uiDoc.Selection.GetElementIds();
            var source = selected.Count == 1
                ? doc.GetElement(selected.First())
                : doc.GetElement(uiDoc.Selection.PickObject(ObjectType.Element, new PipingSelectionFilter(), "Pick the source pipe"));

            var snapshots = ParameterHelper.GetCopyableParameters(source).Select(ParameterHelper.Snapshot).ToList();
            if (snapshots.Count == 0)
            {
                TaskDialog.Show("Copy Parameters", "The source element has no writable parameters with values.");
                return Result.Cancelled;
            }

            var description = $"{source.Category?.Name}: {source.Name} [{source.Id.Value}]";
            var vm = new ParameterPickerViewModel(description, snapshots);
            if (!WindowHelper.ShowModal(new ParameterPickerWindow(vm), commandData.Application))
                return Result.Cancelled;

            ParameterClipboard.Set(vm.SelectedSnapshots, doc.CreationGUID, description);
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
