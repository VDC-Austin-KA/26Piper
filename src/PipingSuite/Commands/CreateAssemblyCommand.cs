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

/// <summary>Feature A1: groups the selected piping into one assembly per logical run.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class CreateAssemblyCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;

        try
        {
            var ids = uiDoc.Selection.GetElementIds()
                .Where(id => PipingElements.IsPiping(doc.GetElement(id)))
                .ToList();

            if (ids.Count == 0)
            {
                ids = uiDoc.Selection
                    .PickObjects(Autodesk.Revit.UI.Selection.ObjectType.Element, new PipingSelectionFilter(), "Select pipes to assemble, then Finish")
                    .Select(r => r.ElementId)
                    .ToList();
            }

            var vm = new AssemblyOptionsViewModel(ids.Count, GetGroupableParameterNames(doc, ids));
            if (!WindowHelper.ShowModal(new AssemblyOptionsWindow(vm), commandData.Application))
                return Result.Cancelled;

            var options = vm.ToOptions();
            var service = new AssemblyService(doc);
            var groups = service.BuildGroups(ids, options, out int alreadyAssembled);

            if (groups.Count == 0)
            {
                TaskDialog.Show("Piping Assemblies", alreadyAssembled > 0
                    ? "All selected elements already belong to an assembly."
                    : "No piping elements to assemble.");
                return Result.Cancelled;
            }

            var result = service.CreateAssemblies(groups, options, alreadyAssembled);
            ShowReport(result);
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

    /// <summary>Text/number parameters present on the selection, offered as group-by keys.</summary>
    private static IEnumerable<string> GetGroupableParameterNames(Document doc, IEnumerable<ElementId> ids) =>
        ids.Select(doc.GetElement)
           .SelectMany(e => e.Parameters.Cast<Parameter>())
           .Where(p => p.StorageType is StorageType.String or StorageType.Integer && p.HasValue)
           .Select(p => p.Definition.Name)
           .Distinct(StringComparer.OrdinalIgnoreCase)
           .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    private static void ShowReport(AssemblyResult r)
    {
        var lines = new List<string> { $"Created {r.CreatedNames.Count} assembly(ies)." };
        if (r.SkippedGroups > 0) lines.Add($"{r.SkippedGroups} group(s) skipped: Revit rejected the element combination.");
        if (r.AlreadyAssembled > 0) lines.Add($"{r.AlreadyAssembled} element(s) skipped: already in an assembly.");

        new TaskDialog("Piping Assemblies")
        {
            MainInstruction = lines[0],
            MainContent = string.Join(Environment.NewLine, lines.Skip(1)),
            ExpandedContent = string.Join(Environment.NewLine, r.CreatedNames),
        }.Show();
    }
}
