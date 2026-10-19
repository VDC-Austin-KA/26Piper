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

/// <summary>
/// Adds a bill of materials table to the active view (an ISO view, or any locked 3D, plan or section view) for the
/// selected piping, or all piping visible in it. Re-running replaces the table, e.g. at another size or side.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class IsoBomCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;
        var view = doc.ActiveView;

        try
        {
            if (view is not (View3D { IsPerspective: false } or ViewPlan or ViewSection) || view.IsTemplate)
                return Fail("Open an ISO view (or another isometric 3D, plan or section view) to add a BOM to.");

            var piping = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(PipingElements.IsPiping).ToList();
            var scope = piping.Count > 0 ? "selected" : "visible";
            if (piping.Count == 0)
                piping = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().WherePasses(AutoDimensionCommand.Piping).ToList();
            if (piping.Count == 0) return Fail("There is no piping in this view.");

            var rows = BomService.Rows(doc, piping).Count;
            var vm = new BomViewModel($"{rows} BOM item(s) from {piping.Count} {scope} piping element(s) in \"{view.Name}\".");
            if (!WindowHelper.ShowModal(new BomWindow(vm), commandData.Application)) return Result.Cancelled;

            BomResult result;
            using (var t = new Transaction(doc, "ISO BOM"))
            {
                t.Start();
                result = new BomService(doc).Place(view, piping, vm.ToOptions());
                t.Commit();
            }

            new TaskDialog("ISO BOM")
            {
                MainInstruction = $"Added a {result.Rows}-item BOM at {BomService.Fraction(result.TextSize)} text.",
                MainContent = string.Join(Environment.NewLine, result.Notes),
            }.Show();
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }

        static Result Fail(string text)
        {
            TaskDialog.Show("ISO BOM", text);
            return Result.Cancelled;
        }
    }
}
