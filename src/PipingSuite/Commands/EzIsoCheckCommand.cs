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
/// Checks the selection's line numbers ({BC_SYSTEM}-{BC_PACKAGE}-{BC_LINE SEGMENT}, as EZ-ISO's Auto Line Nos
/// reads them) and connections, and fixes the ticked items. The window is modal, so its buttons run inside
/// this command's API context.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class EzIsoCheckCommand : IExternalCommand
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
                        "Select the piping to check for EZ-ISO (a whole package or system, so duplicates are found), then Finish")
                    .Select(doc.GetElement).ToList();

            var scope = new DisconnectService(doc).Scope(picked);
            if (scope.Count == 0) return Result.Cancelled;
            var vm = new EzIsoViewModel(
                $"{scope.Count} element(s), numbered {{{EzIsoService.SystemParam}}}-{{{EzIsoService.PackageParam}}}-{{{EzIsoService.SegmentParam}}}. " +
                "Correct, unique lines are left alone; only the ticked items are changed.",
                new Host(new EzIsoService(doc), scope, uiDoc));
            WindowHelper.ShowModal(new EzIsoWindow(vm), commandData.Application);
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

    private sealed class Host(EzIsoService service, List<Element> scope, UIDocument uiDoc) : IEzIsoHost
    {
        public EzIsoScan Scan()
        {
            // Olets re-made by a connect drop out; their replacements are found again on the next scope.
            scope = new DisconnectService(uiDoc.Document).Scope(scope.Where(e => e.IsValidObject));
            return service.Scan(scope);
        }

        public void Show(EzIsoIssue issue)
        {
            var ids = issue.Ids.Where(id => uiDoc.Document.GetElement(id) is not null).ToList();
            if (ids.Count == 0) return;
            uiDoc.Selection.SetElementIds(ids);
            uiDoc.ShowElements(ids);
            uiDoc.RefreshActiveView();
        }

        public (int Done, IReadOnlyList<string> Notes) Apply(IReadOnlyList<EzIsoIssue> issues)
        {
            var (done, notes) = service.Apply(issues);
            uiDoc.RefreshActiveView();
            return (done, notes);
        }

        public void SelectForEzIso() =>
            uiDoc.Selection.SetElementIds(scope.Where(e => e.IsValidObject && PipingElements.IsPiping(e)).Select(e => e.Id).ToList());
    }
}
