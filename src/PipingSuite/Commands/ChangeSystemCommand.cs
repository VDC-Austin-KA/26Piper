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

/// <summary>Moves the selected piping (optionally with its connected run) to another piping system type.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class ChangeSystemCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;

        try
        {
            var selected = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(PipingElements.IsPiping).ToList();
            if (selected.Count == 0)
                selected = uiDoc.Selection.PickObjects(ObjectType.Element, new PipingSelectionFilter(), "Select the piping to move to another system, then Finish")
                    .Select(doc.GetElement).ToList();
            if (selected.Count == 0) return Result.Cancelled;

            var service = new ChangeSystemService(doc);
            var systems = service.SystemTypes();
            if (systems.Count < 2) return Fail("This project has only one piping system type.");
            string Name(ElementId? id) => id is null ? "(none)" : systems.FirstOrDefault(s => s.Id == id)?.Name ?? "(none)";
            string Describe(bool connected)
            {
                var scope = ChangeSystemService.Scope(selected, connected);
                var now = scope.GroupBy(e => Name(ChangeSystemService.SystemTypeOf(e))).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})");
                return $"{scope.Count} element(s){(connected ? ", with connected piping" : string.Empty)}, now on: {string.Join(", ", now)}.";
            }
            var current = selected.Select(ChangeSystemService.SystemTypeOf).OfType<ElementId>().GroupBy(id => id.Value).OrderByDescending(g => g.Count()).FirstOrDefault()?.First();
            var vm = new ChangeSystemViewModel(systems.Select(s => new LabeledValue<ElementId>($"{s.Name}  ({s.Classification})", s.Id)).ToList(), current, Describe);
            if (!WindowHelper.ShowModal(new ChangeSystemWindow(vm), commandData.Application)) return Result.Cancelled;
            var (target, connected) = vm.ToOptions();

            var scope = ChangeSystemService.Scope(selected, connected);
            var result = service.Change(scope, target);
            uiDoc.Selection.SetElementIds(scope.Where(e => e.IsValidObject).Select(e => e.Id).ToList());
            new TaskDialog("Change System")
            {
                MainInstruction = $"Moved {result.Changed} pipe(s) to {Name(target)}.",
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
            TaskDialog.Show("Change System", text);
            return Result.Cancelled;
        }
    }
}
