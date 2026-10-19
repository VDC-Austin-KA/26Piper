using System;
using System.Collections.Generic;
using System.IO;
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
/// Dimensions the piping in the active view (or just the selected piping) the way a spool iso does: one chained
/// dimension along each straight leg, centre to centre of its fittings and valves. Works in 3D views too.
/// The chosen dimension type is remembered as the default in %AppData%\PipingSuite.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class AutoDimensionCommand : IExternalCommand
{
    private static readonly ElementMulticategoryFilter Piping = new(new List<BuiltInCategory>
    {
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
    });

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;
        var view = doc.ActiveView;

        try
        {
            if (view is not (View3D or ViewPlan or ViewSection) || view.IsTemplate)
                return Fail("Open a 3D, plan or section view to dimension.");

            var piping = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(PipingElements.IsPiping).ToList();
            var scope = piping.Count > 0 ? "selected" : "visible";
            if (piping.Count == 0)
                piping = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().WherePasses(Piping).ToList();
            if (piping.Count == 0) return Fail("There is no piping in this view.");

            var types = new FilteredElementCollector(doc).OfClass(typeof(DimensionType)).Cast<DimensionType>()
                .Where(t => t.StyleType == DimensionStyleType.Linear && t.Name.Length > 0)
                .OrderBy(t => t.Name, ColorSplasherService.NaturalComparer.Instance)
                .Select(t => new LabeledValue<long>(t.Name, t.Id.Value)).ToList();
            if (types.Count == 0) return Fail("This project has no linear dimension types.");
            var vm = new AutoDimensionViewModel($"Dimension {piping.Count} {scope} piping element(s) in \"{view.Name}\".", types, ReadDefault());
            if (!WindowHelper.ShowModal(new AutoDimensionWindow(vm), commandData.Application) || vm.SelectedType is not { } chosen)
                return Result.Cancelled;
            SaveDefault(chosen.Label);

            DimensionResult result;
            using (var t = new Transaction(doc, "Auto Dimension"))
            {
                t.Start();
                result = new DimensionService(doc).Dimension(view, piping, doc.GetElement(new ElementId(chosen.Value)) as DimensionType);
                if (result.Dimensions.Count == 0)
                {
                    t.RollBack();
                    return Fail($"No legs could be dimensioned.\n\n{string.Join("\n", result.Notes)}");
                }
                t.Commit();
            }

            new TaskDialog("Auto Dimension")
            {
                MainInstruction = $"Dimensioned {result.Dimensions.Count} of {result.Legs} straight leg(s).",
                MainContent = $"{piping.Count} {scope} piping element(s)." + (result.Notes.Count > 0 ? " Expand for notes." : string.Empty),
                ExpandedContent = string.Join(Environment.NewLine, result.Notes),
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
            TaskDialog.Show("Auto Dimension", text);
            return Result.Cancelled;
        }
    }

    private static readonly string DefaultFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PipingSuite", "AutoDimensionType.txt");

    private static string? ReadDefault()
    {
        try { return File.Exists(DefaultFile) ? File.ReadAllText(DefaultFile).Trim() : null; }
        catch (IOException) { return null; }
    }

    private static void SaveDefault(string name)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultFile)!);
            File.WriteAllText(DefaultFile, name);
        }
        catch (IOException) { } // only a convenience
        catch (UnauthorizedAccessException) { }
    }
}
