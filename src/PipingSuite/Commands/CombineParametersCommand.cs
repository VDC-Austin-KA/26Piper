using System;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PipingSuite.Core;
using PipingSuite.Services;
using PipingSuite.UI.ViewModels;
using PipingSuite.UI.Views;

namespace PipingSuite.Commands;

/// <summary>Creates a shared parameter whose value is two existing parameters joined, e.g. PKG9-1.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class CombineParametersCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var doc = commandData.Application.ActiveUIDocument.Document;
        try
        {
            var service = new CombinedParameterService(doc);
            var names = service.GetBoundParameters().Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (names.Count < 2)
            {
                TaskDialog.Show("Combine Parameters", "The project needs at least two project or shared parameters.");
                return Result.Cancelled;
            }

            var vm = new CombineParametersViewModel(names);
            if (!WindowHelper.ShowModal(new CombineParametersWindow(vm), commandData.Application))
                return Result.Cancelled;

            var request = vm.ToRequest();
            var r = service.Combine(request);
            TaskDialog.Show("Combine Parameters",
                $"{(r.CreatedParameter ? "Created" : "Updated")} \"{request.NewName}\" on {r.CategoryCount} category(ies).{Environment.NewLine}" +
                $"{r.UpdatedElements} element value(s) written.");
            return Result.Succeeded;
        }
        catch (InvalidOperationException ex)
        {
            TaskDialog.Show("Combine Parameters", ex.Message);
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }
}
