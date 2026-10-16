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
            var targets = uiDoc.Selection.GetElementIds().Select(doc.GetElement).ToList();
            if (targets.Count == 0)
            {
                targets = uiDoc.Selection
                    .PickObjects(ObjectType.Element, new PipingSelectionFilter(), "Select target elements, then Finish")
                    .Select(doc.GetElement)
                    .ToList();
            }

            bool sameDocument = doc.CreationGUID == ParameterClipboard.SourceDocumentGuid;
            var outcomes = new Dictionary<PasteOutcome, int>();

            using (var t = new Transaction(doc, "Paste Parameters"))
            {
                t.Start();
                foreach (var target in targets)
                foreach (var snapshot in ParameterClipboard.Items)
                {
                    var o = ParameterHelper.TryPaste(target, snapshot, sameDocument);
                    outcomes[o] = outcomes.GetValueOrDefault(o) + 1;
                }
                t.Commit();
            }

            ShowReport(targets.Count, outcomes);
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

    private static void ShowReport(int targetCount, Dictionary<PasteOutcome, int> outcomes)
    {
        var details = outcomes
            .Where(kv => kv.Key != PasteOutcome.Applied)
            .Select(kv => $"{kv.Key}: {kv.Value}");

        new TaskDialog("Paste Parameters")
        {
            MainInstruction = $"Applied {outcomes.GetValueOrDefault(PasteOutcome.Applied)} value(s) to {targetCount} element(s).",
            MainContent = $"Source: {ParameterClipboard.SourceDescription}{Environment.NewLine}{string.Join(Environment.NewLine, details)}",
        }.Show();
    }
}
