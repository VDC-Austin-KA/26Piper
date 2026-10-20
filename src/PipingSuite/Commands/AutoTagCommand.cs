using System;
using System.Collections.Generic;
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
/// Tags the selected piping, or all piping visible in the active view, once per unique line number (or other
/// parameter value): a header and its identical branches get one tag each, not one per pipe.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class AutoTagCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;
        var view = doc.ActiveView;

        try
        {
            if (view is not (View3D { IsPerspective: false } or ViewPlan or ViewSection) || view.IsTemplate)
                return Fail("Open an ISO view (or another isometric 3D, plan or section view) to tag.");

            var piping = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(PipingElements.IsPiping).ToList();
            var scope = piping.Count > 0 ? "selected" : "visible";
            if (piping.Count == 0)
                piping = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().WherePasses(AutoDimensionCommand.Piping).ToList();
            if (piping.Count == 0) return Fail("There is no piping in this view.");

            var symbols = AutoTagService.TagTypes(doc);
            if (symbols.Count == 0) return Fail("No pipe tag family is loaded. Load one (e.g. \"ISO Tag\") and run again.");

            // The line number, then every text parameter that has a value on this piping's pipes.
            var parameters = new List<string> { AutoTagService.LineNumber };
            parameters.AddRange(piping.Where(PipingElements.IsPipe)
                .SelectMany(e => e.Parameters.Cast<Parameter>())
                .Where(p => p.StorageType == StorageType.String && p.HasValue && !string.IsNullOrWhiteSpace(p.AsString()))
                .Select(p => p.Definition.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            var values = parameters.Distinct().ToDictionary(p => p,
                p => (IReadOnlyList<string>)AutoTagService.Groups(piping, p).Select(g => g.Key!).ToList());

            var vm = new AutoTagViewModel($"{piping.Count} {scope} piping element(s) in \"{view.Name}\".",
                symbols.Select(s => new LabeledValue<ElementId>($"{s.FamilyName} : {s.Name}", s.Id)).ToList(), values);
            if (!WindowHelper.ShowModal(new AutoTagWindow(vm), commandData.Application)) return Result.Cancelled;
            var (tagType, parameter) = vm.ToOptions();

            AutoTagResult result;
            using (var t = new Transaction(doc, "Auto Tag"))
            {
                t.Start();
                result = new AutoTagService(doc).Tag(view, piping, parameter, (FamilySymbol)doc.GetElement(tagType));
                t.Commit();
            }

            new TaskDialog("Auto Tag")
            {
                MainInstruction = $"Placed {result.Tags} tag(s) for {result.Values} unique value(s).",
                MainContent = (result.Replaced > 0 ? $"Replaced {result.Replaced} existing tag(s) of this type.\n" : string.Empty)
                              + string.Join(Environment.NewLine, result.Notes),
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
            TaskDialog.Show("Auto Tag", text);
            return Result.Cancelled;
        }
    }
}
