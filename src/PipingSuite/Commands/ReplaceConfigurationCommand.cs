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
/// out like them, on any header section, is found and listed for review (location, legs, stretch, beside a
/// preview of them all), then the ticked ones are swapped for the correct one on their own header,
/// reconnected, and optionally numbered.
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

            // 3. Every branch with the same route (legs, sizes and, by the chosen rules, directions and lengths), on any header.
            var options = new MatchOptionsViewModel($"Picked {picked.Count} branch(es) to fix. Find the others like them:");
            if (!WindowHelper.ShowModal(new MatchOptionsWindow(options), commandData.Application)) return Result.Cancelled;
            var (matches, report) = service.FindMatches(picked, goodIds, options.ToRules());
            var fits = picked.Concat(matches).DistinctBy(b => b.RootId).Select(b => (Branch: b, Fit: service.Fit(good, b))).ToList();
            var targets = ReplaceConfigurationService.AlongHeader(fits.Where(f => f.Fit.Pose is not null).Select(f => new Target(f.Branch, f.Fit.Pose!)).ToList());
            var misfits = fits.Where(f => f.Fit.Pose is null).ToList();
            if (targets.Count == 0)
                return Fail($"The correct branch can't be fitted onto the picked one: {misfits.FirstOrDefault().Fit.Why}\n\nMatching {report}.");

            // 4. Review: every branch with its location and legs, beside a preview of them all and the correct one.
            var units = doc.GetUnits();
            string Len(double ft) => UnitFormatUtils.Format(units, SpecTypeId.Length, ft, false);
            string Legs(IEnumerable<Leg> legs) => string.Join(" / ", legs.Select(l => Len(l.Length)));
            var locate = service.GridLocator();
            var goodLegs = fits.Select(f => f.Fit.Legs).FirstOrDefault(l => l is not null) ?? [];
            ReplaceRow Row(int no, Branch b, Target? t, string status, IReadOnlyList<Leg>? correct) => new(no, t, b.Ids.ToList(), status,
                locate(b.Origin) ?? "(no grids)", b.Header is { } h ? $"[{h.Id.Value}] {b.SystemName}" : "(none)", Legs(b.Route.Legs),
                correct is null ? string.Empty : string.Join(" / ", b.Route.Legs.Zip(correct, (o, c) => o.Length - c.Length)
                    .Select(d => Math.Abs(d) < 1.0 / 192 ? "0" : (d > 0 ? "+" : "−") + Len(Math.Abs(d)))),
                Len(b.Elements.OfType<MEPCurve>().Sum(p => (p.Location as LocationCurve)?.Curve.Length ?? 0)));
            var legsOf = fits.ToDictionary(f => f.Branch, f => f.Fit.Legs);
            var rows = targets.Select((t, i) => Row(i + 1, t.Old, t, "Will be replaced", legsOf[t.Old]))
                .Concat(misfits.Select((m, i) => Row(targets.Count + i + 1, m.Branch, null, $"Can't fit: {m.Fit.Why}", null))).ToList();

            View3D? review = null;
            Autodesk.Revit.UI.PreviewControl? preview = null;
            try
            {
                review = service.CreateReviewView(targets.Select(t => t.Old), misfits.Select(m => m.Branch), good);
                preview = new Autodesk.Revit.UI.PreviewControl(doc, review.Id);
            }
            catch (Exception) { /* the list still works without the preview */ }

            var headers = targets.Select(t => t.Old.Header?.Id).Distinct().Count();
            var vm = new ReplaceConfigurationViewModel(
                $"{targets.Count} branch(es) on {headers} header connection(s) can take the correct branch, each leg stretched to land where the old one did" +
                (misfits.Count > 0 ? $"; {misfits.Count} can't" : string.Empty) + $". Matching {report}.",
                SourceValues(doc, good), rows, Legs(goodLegs),
                ids => { uiDoc.Selection.SetElementIds(ids.Where(id => doc.GetElement(id) is not null).ToList()); uiDoc.ShowElements(ids.Where(id => doc.GetElement(id) is not null).ToList()); });
            bool go;
            try { go = WindowHelper.ShowModal(new ReplaceConfigurationWindow(vm, preview), commandData.Application); }
            finally
            {
                preview?.Dispose();
                if (review is not null && !vm.KeepReviewView && doc.GetElement(review.Id) is not null)
                {
                    using var t = new Transaction(doc, "Replace Configuration - remove review view");
                    t.Start();
                    doc.Delete(review.Id);
                    t.Commit();
                }
            }
            if (!go) return Result.Cancelled;
            targets = vm.Chosen();

            // 5. Replace.
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
