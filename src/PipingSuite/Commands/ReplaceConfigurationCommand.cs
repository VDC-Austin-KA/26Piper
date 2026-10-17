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
/// Pick one or more wrongly built branches and a correctly built one (or its assembly); every branch laid
/// out like them, on any header section, is found and shown for review, then swapped for the correct one
/// on its own header, reconnected, and optionally numbered.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ReplaceConfigurationCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        var doc = uiDoc.Document;
        var service = new ReplaceConfigurationService(doc);

        try
        {
            // 1. The wrong branch(es): one or several, each on whatever header it comes off.
            var badElements = uiDoc.Selection.GetElementIds().Select(doc.GetElement).Where(PipingElements.IsPiping).ToList();
            if (badElements.Count == 0)
                badElements = Pick(uiDoc, new PipingSelectionFilter(),
                    "Select one or more INCORRECT branches (pipes, fittings, valves; include the olet to replace it too), then Finish");
            var picked = service.Split(badElements);
            if (picked.Count == 0) return Fail("None of the selected branches is connected to a header.");

            // 2. The correct branch or assembly. Its header end is found by fitting it over the wrong branch.
            var goodPick = Pick(uiDoc, new PredicateSelectionFilter(e => PipingElements.IsPiping(e) || e is AssemblyInstance),
                "Select the CORRECT branch (with its olet if it has one) or its assembly, then Finish");
            var assemblies = goodPick.OfType<AssemblyInstance>().ToList();
            var good = new Source(
                goodPick.Where(e => e is not AssemblyInstance)
                    .Concat(assemblies.SelectMany(a => a.GetMemberIds()).Select(doc.GetElement))
                    .Where(PipingElements.IsPiping).DistinctBy(e => e.Id).ToList(),
                assemblies.Count == 1 && goodPick.Count == 1 ? assemblies[0].Id : null);
            var goodIds = good.Elements.Select(e => e.Id).ToHashSet();
            if (good.Elements.Count == 0) return Fail("The correct selection has no piping in it.");
            if (badElements.Any(b => goodIds.Contains(b.Id))) return Fail("The correct branch overlaps an incorrect one.");

            // 3. Every branch with the same route (legs turning the same way at the same sizes), on any header.
            var (matches, report) = service.FindMatches(picked, goodIds);
            var targets = new List<Target>();
            var misfits = new List<string>();
            void Add(Branch b)
            {
                var (pose, why) = service.Fit(good, b);
                if (pose is not null) targets.Add(new Target(b, pose));
                else misfits.Add($"Branch [{b.RootId.Value}]: {why}");
            }
            foreach (var b in picked.Concat(matches)) Add(b);
            if (targets.Count == 0)
                return Fail($"The correct branch can't be fitted onto the picked one: {misfits.FirstOrDefault()}\n\nMatching {report}.");

            // 4. Review in the model: the user can orbit/zoom, Ctrl+click to add branches, Shift+click to drop them.
            var all = targets.SelectMany(t => t.Old.Ids).ToList();
            uiDoc.ShowElements(all);
            var reviewed = uiDoc.Selection.PickObjects(ObjectType.Element, new PipingSelectionFilter(),
                    $"{targets.Count} branch(es) will be replaced ({report}): Shift+click to drop one, Ctrl+click to add one, then Finish",
                    all.Select(id => new Reference(doc.GetElement(id))).ToList())
                .Select(r => r.ElementId).ToHashSet();
            targets = targets.Where(t => reviewed.Contains(t.Old.RootId)).ToList();
            var known = targets.SelectMany(t => t.Old.Ids).ToHashSet();
            foreach (var b in service.Split(reviewed.Where(id => !known.Contains(id) && !goodIds.Contains(id)).Select(doc.GetElement).ToList()))
                Add(b);
            if (targets.Count == 0) return Result.Cancelled;

            // 5. Numbering, then replace.
            var headers = targets.Select(t => t.Old.Header?.Id).Distinct().Count();
            var vm = new ReplaceConfigurationViewModel(
                $"Replace {targets.Count} branch(es) on {headers} header connection(s) with the correct branch, each leg stretched to fit." +
                $"\nMatching {report}." + (misfits.Count > 0 ? $"\n{misfits.Count} matched branch(es) skipped: {misfits[0]}" : string.Empty),
                SourceValues(doc, good));
            if (!WindowHelper.ShowModal(new ReplaceConfigurationWindow(vm), commandData.Application)) return Result.Cancelled;

            var result = service.Replace(targets, good, vm.ToNumbering());
            uiDoc.Selection.SetElementIds(result.NewIds.ToList());

            new TaskDialog("Replace Configuration")
            {
                MainInstruction = $"Replaced {result.Replaced} of {targets.Count} branch(es).",
                MainContent = result.Warnings.Count > 0 ? $"{result.Warnings.Count} note(s); expand for details." : "All connections were remade.",
                ExpandedContent = string.Join(Environment.NewLine, result.Warnings),
            }.Show();
            return result.Replaced > 0 ? Result.Succeeded : Result.Cancelled;
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
            TaskDialog.Show("Replace Configuration", text);
            return Result.Cancelled;
        }
    }

    private static List<Element> Pick(UIDocument uiDoc, ISelectionFilter filter, string prompt) =>
        uiDoc.Selection.PickObjects(ObjectType.Element, filter, prompt).Select(uiDoc.Document.GetElement).ToList();

    /// <summary>Writable text/integer parameters on the correct branch (and its assembly), with their first value found.</summary>
    private static Dictionary<string, string> SourceValues(Document doc, Source good)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var owners = good.AssemblyId is { } a ? good.Elements.Prepend(doc.GetElement(a)) : good.Elements;
        foreach (var e in owners)
        foreach (Parameter p in e.Parameters)
        {
            if (p.IsReadOnly || p.StorageType is not (StorageType.String or StorageType.Integer)) continue;
            var v = p.StorageType == StorageType.String ? p.AsString() : p.AsInteger().ToString();
            if (!values.TryGetValue(p.Definition.Name, out var have) || string.IsNullOrEmpty(have))
                values[p.Definition.Name] = v ?? string.Empty;
        }
        return values;
    }
}
