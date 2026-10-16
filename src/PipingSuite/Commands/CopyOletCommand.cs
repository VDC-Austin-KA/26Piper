using System;
using System.Collections.Generic;
using System.Linq;
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
/// Copies one olet's placement (offset along the main, rotation, size, type) onto many pipes.
/// Works from a preselection of the olet + target pipes, or prompts for them.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class CopyOletCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;
        var units = doc.GetUnits();
        string Len(double ft) => UnitFormatUtils.Format(units, SpecTypeId.Length, ft, false);

        try
        {
            var selected = uiDoc.Selection.GetElementIds().Select(doc.GetElement).ToList();
            var source = selected.OfType<FamilyInstance>().FirstOrDefault(e => OletService.AsOlet(e) is not null)
                         ?? (FamilyInstance)doc.GetElement(uiDoc.Selection.PickObject(ObjectType.Element,
                             new PredicateSelectionFilter(e => OletService.AsOlet(e) is not null), "Pick the source olet"));

            var service = new OletService(doc, uiDoc.ActiveView);
            var placement = service.Describe(source);

            var targets = selected.OfType<Pipe>().Where(p => p.Id != placement.SourceMainId).ToList();
            if (targets.Count == 0)
            {
                targets = uiDoc.Selection.PickObjects(ObjectType.Element,
                        new PredicateSelectionFilter(e => e is Pipe && e.Id != placement.SourceMainId),
                        "Select the pipes to receive the olet, then Finish")
                    .Select(r => (Pipe)doc.GetElement(r)).ToList();
            }
            if (targets.Count == 0) return Result.Cancelled;

            var vm = new CopyOletViewModel(
                $"Copy {source.Symbol.FamilyName}: {source.Name} ({Len(placement.OffsetFromStart)} from start, " +
                $"{placement.AngleDegrees:0}°) to {targets.Count} pipe(s).");
            if (!WindowHelper.ShowModal(new CopyOletWindow(vm), commandData.Application)) return Result.Cancelled;

            int placed = 0, extended = 0;
            var failures = new List<string>();
            using (var tg = new TransactionGroup(doc, "Copy Olet"))
            {
                tg.Start();
                foreach (var target in targets)
                {
                    try
                    {
                        var (_, wasExtended) = service.PlaceCopy(target, placement, vm.Mode, vm.ExtendShortPipes);
                        placed++;
                        if (wasExtended) extended++;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        failures.Add($"{target.Name} [{target.Id.Value}]: {ex.Message}");
                    }
                }
                if (placed > 0) tg.Assimilate();
                else tg.RollBack();
            }

            new TaskDialog("Copy Olet")
            {
                MainInstruction = $"Placed {placed} of {targets.Count} olet(s).",
                MainContent = extended > 0 ? $"{extended} short pipe(s) were extended to fit." : string.Empty,
                ExpandedContent = string.Join(Environment.NewLine, failures),
            }.Show();
            return placed > 0 ? Result.Succeeded : Result.Cancelled;
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
