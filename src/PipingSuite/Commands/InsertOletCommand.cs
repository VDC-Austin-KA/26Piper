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
/// Click a pipe to drop a centred olet (no branch pipe left behind), or click an existing
/// olet to resize it in place. Optionally keeps placing with the same settings until Esc.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class InsertOletCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;
        var service = new OletService(doc, uiDoc.ActiveView);
        var filter = new OletPickFilter();

        OletSettings? settings = null;
        bool keepPlacing = false;
        int done = 0;

        try
        {
            while (true)
            {
                var reference = uiDoc.Selection.PickObject(ObjectType.PointOnElement, filter,
                    keepPlacing ? "Pick the next point on a pipe or olet (Esc to finish)" : "Pick a point on a pipe, or an existing olet to resize");
                var picked = doc.GetElement(reference);
                var olet = OletService.AsOlet(picked);
                var main = olet?.Main ?? (Pipe)picked;

                if (settings is null || !keepPlacing)
                {
                    var vm = BuildDialog(service, doc, picked, main, olet is not null);
                    if (!WindowHelper.ShowModal(new OletOptionsWindow(vm), commandData.Application)) break;
                    settings = vm.ToSettings();
                    keepPlacing = vm.KeepPlacing;
                }

                try
                {
                    if (olet is not null) service.Resize((FamilyInstance)picked, settings);
                    else service.Insert(main, reference.GlobalPoint, settings);
                    done++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Autodesk.Revit.Exceptions.ApplicationException)
                {
                    TaskDialog.Show("Olet", $"Not placed: {ex.Message}");
                }

                if (!keepPlacing) break;
            }
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            // Esc ends the placing loop.
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }

        return done > 0 ? Result.Succeeded : Result.Cancelled;
    }

    private static OletOptionsViewModel BuildDialog(OletService service, Document doc, Element picked, Pipe main, bool isResize)
    {
        var units = doc.GetUnits();
        string Size(double d) => UnitFormatUtils.Format(units, SpecTypeId.PipeSize, d, false);

        var sizes = OletService.GetBranchSizes(main).Select(d => new LabeledValue<double>(Size(d), d)).ToList();
        var types = new List<LabeledValue<ElementId?>> { new("Auto (pipe type's tap, else first loaded olet)", null) };
        types.AddRange(service.GetOletTypes().Select(s => new LabeledValue<ElementId?>($"{s.FamilyName}: {s.Name}", s.Id)));

        var target = isResize
            ? $"Resize {picked.Name} on {main.Name} Ø{Size(main.Diameter)}"
            : $"Insert on {main.Name} Ø{Size(main.Diameter)}";
        return new OletOptionsViewModel(target, isResize, sizes, types);
    }
}
