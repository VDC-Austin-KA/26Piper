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
/// Lists the selection's open ends that touch something they aren't connected to, and connects the ones
/// that should be joined. The window is modal, so its buttons run inside this command's API context.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ConnectDisconnectsCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;

        try
        {
            var picked = uiDoc.Selection.GetElementIds().Select(doc.GetElement)
                .Where(e => PipingElements.IsPiping(e) || e is Autodesk.Revit.DB.Plumbing.PipingSystem).ToList();
            if (picked.Count == 0)
                picked = uiDoc.Selection.PickObjects(ObjectType.Element, new PipingSelectionFilter(),
                        "Select the piping to check for disconnects (window select works), then Finish")
                    .Select(doc.GetElement).ToList();

            var service = new DisconnectService(doc);
            var scope = service.Scope(picked);
            if (scope.Count == 0) return Result.Cancelled;
            var vm = new DisconnectsViewModel(
                $"{scope.Count} selected element(s): their open ends that touch something they aren't connected to",
                new Host(service, scope, uiDoc));
            WindowHelper.ShowModal(new DisconnectsWindow(vm), commandData.Application);
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

    private sealed class Host(DisconnectService service, List<Element> scope, UIDocument uiDoc) : IDisconnectHost
    {
        public IReadOnlyList<Disconnect> Scan(double toleranceFeet, bool sameSystemType)
        {
            // Elements replaced by an earlier fix (re-attached olets) drop out.
            scope = scope.Where(e => e.IsValidObject).ToList();
            return service.Find(scope, toleranceFeet, sameSystemType);
        }

        public void Show(Disconnect item)
        {
            var ids = new[] { item.Owner, item.Other }.OfType<ElementId>()
                .Where(id => uiDoc.Document.GetElement(id) is not null).ToList();
            if (ids.Count == 0) return;
            uiDoc.Selection.SetElementIds(ids);
            uiDoc.ShowElements(ids);
            uiDoc.RefreshActiveView();
        }

        public (int Fixed, IReadOnlyList<string> Notes) Fix(IReadOnlyList<Disconnect> items)
        {
            var (done, notes) = service.Fix(items);
            uiDoc.RefreshActiveView();
            return (done, notes);
        }
    }
}
