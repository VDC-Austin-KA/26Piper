using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using PipingSuite.Core;
using PipingSuite.Services;

namespace PipingSuite.Commands;

/// <summary>Feature A2 (paste): writes clipboard values onto the selected targets in one undoable step.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class PasteParametersCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        if (ParameterClipboard.IsEmpty)
        {
            TaskDialog.Show("Paste Parameters", "Nothing copied yet. Run Copy Parameters first.");
            return Result.Cancelled;
        }

        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;

        try
        {
            bool sameDocument = doc.CreationGUID == ParameterClipboard.SourceDocumentGuid;
            bool IsTarget(ElementId id) => !(sameDocument && id == ParameterClipboard.SourceElementId);

            var targets = uiDoc.Selection.GetElementIds().Where(IsTarget).Select(doc.GetElement).ToList();
            if (targets.Count == 0)
            {
                targets = uiDoc.Selection
                    .PickObjects(ObjectType.Element, new PipingSelectionFilter(), "Select target elements, then Finish")
                    .Select(r => r.ElementId)
                    .Where(IsTarget)
                    .Select(doc.GetElement)
                    .ToList();
            }

            var outcomes = new Dictionary<PasteOutcome, int>();
            var problems = new SortedDictionary<string, SortedSet<PasteOutcome>>(StringComparer.OrdinalIgnoreCase);

            using (var t = new Transaction(doc, "Paste Parameters"))
            {
                t.Start();
                foreach (var target in targets)
                foreach (var snapshot in ParameterClipboard.Items)
                {
                    var o = ParameterHelper.TryPaste(target, snapshot, sameDocument);
                    outcomes[o] = outcomes.GetValueOrDefault(o) + 1;
                    if (o != PasteOutcome.Applied)
                    {
                        if (!problems.TryGetValue(snapshot.Name, out var set)) problems[snapshot.Name] = set = [];
                        set.Add(o);
                    }
                }
                t.Commit();
            }

            ShowReport(targets.Count, outcomes, problems);
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

    private static void ShowReport(int targetCount, Dictionary<PasteOutcome, int> outcomes,
                                   SortedDictionary<string, SortedSet<PasteOutcome>> problems)
    {
        var details = outcomes
            .Where(kv => kv.Key != PasteOutcome.Applied)
            .Select(kv => $"{kv.Key}: {kv.Value}");

        new TaskDialog("Paste Parameters")
        {
            MainInstruction = $"Applied {outcomes.GetValueOrDefault(PasteOutcome.Applied)} value(s) to {targetCount} element(s).",
            MainContent = $"Source: {ParameterClipboard.SourceDescription}{Environment.NewLine}{string.Join(Environment.NewLine, details)}",
            ExpandedContent = string.Join(Environment.NewLine,
                problems.Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}")),
        }.Show();
    }
}
