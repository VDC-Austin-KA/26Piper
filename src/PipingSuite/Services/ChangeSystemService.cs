using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using PipingSuite.Core;

namespace PipingSuite.Services;

public sealed record SystemChoice(ElementId Id, string Name, string Classification);

public sealed record ChangeSystemResult(int Changed, IReadOnlyList<string> Notes);

/// <summary>
/// Change System: moves piping to another piping system type (Condensate Water Supply → Condensate Water Return).
/// Pipes take the new type directly; fittings and accessories follow the pipes they connect. Optionally the
/// change spreads to everything connected on the same system type, so a whole run moves together.
/// </summary>
public sealed class ChangeSystemService(Document doc)
{
    public List<SystemChoice> SystemTypes() =>
        new FilteredElementCollector(doc).OfClass(typeof(PipingSystemType)).Cast<PipingSystemType>()
            .Select(t => new SystemChoice(t.Id, t.Name, t.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM)?.AsString() ?? t.SystemClassification.ToString()))
            .OrderBy(t => t.Name, ColorSplasherService.NaturalComparer.Instance).ToList();

    public static ElementId? SystemTypeOf(Element e) =>
        e.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() is { } id && id != ElementId.InvalidElementId ? id : null;

    /// <summary>The selection, plus (when <paramref name="connected"/>) all piping joined to it on one of its system types.</summary>
    public static List<Element> Scope(IEnumerable<Element> selected, bool connected)
    {
        var found = selected.Where(PipingElements.IsPiping).DistinctBy(e => e.Id).ToDictionary(e => e.Id);
        if (!connected) return [.. found.Values];
        var types = found.Values.Select(SystemTypeOf).OfType<ElementId>().ToHashSet();
        var queue = new Queue<Element>(found.Values);
        while (queue.Count > 0)
            foreach (var n in PipingElements.GetConnectedElements(queue.Dequeue()))
                if (!found.ContainsKey(n.Id) && PipingElements.IsPiping(n) && SystemTypeOf(n) is { } t && types.Contains(t))
                {
                    found[n.Id] = n;
                    queue.Enqueue(n);
                }
        return [.. found.Values];
    }

    public ChangeSystemResult Change(IReadOnlyList<Element> scope, ElementId target)
    {
        var notes = new List<string>();
        var errors = new List<string>();
        int changed = 0;

        using var t = new Transaction(doc, "Change System");
        var options = t.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(new Lenient(errors));
        t.SetFailureHandlingOptions(options);
        t.Start();
        foreach (var curve in scope.OfType<MEPCurve>())
        {
            if (SystemTypeOf(curve) == target) continue;
            try
            {
                if (curve is Pipe pipe) pipe.SetSystemType(target);
                else if (curve.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM) is not { IsReadOnly: false } p || !p.Set(target))
                    throw new InvalidOperationException("its system type can't be changed");
                changed++;
            }
            catch (Exception ex)
            {
                notes.Add($"Pipe [{curve.Id.Value}]: {ex.Message.Split('\n')[0]}");
            }
        }
        doc.Regenerate();

        // Fittings and accessories take their system from their pipes; flag any that didn't follow.
        var stuck = scope.Where(e => e is not MEPCurve && e.IsValidObject && SystemTypeOf(e) is { } s && s != target).ToList();
        if (stuck.Count > 0)
            notes.Add($"{stuck.Count} fitting(s) or accessory(ies) still show another system (they follow their pipes; " +
                      $"select the pipes on both sides of them too): {string.Join(", ", stuck.Take(6).Select(e => $"[{e.Id.Value}]"))}");

        // Where the change stops against piping still on another system, Revit treats the two as separate systems.
        var ids = scope.Select(e => e.Id).ToHashSet();
        var edges = scope.SelectMany(PipingElements.GetConnectedElements)
            .Where(n => !ids.Contains(n.Id) && PipingElements.IsPiping(n) && SystemTypeOf(n) is { } s && s != target)
            .DistinctBy(n => n.Id).ToList();
        if (edges.Count > 0)
            notes.Add($"The changed piping meets {edges.Count} element(s) still on another system: " +
                      string.Join(", ", edges.Take(6).Select(e => $"[{e.Id.Value}] {doc.GetElement(SystemTypeOf(e)!)?.Name}")) +
                      ". Tick \"connected piping\" or select those too to move the whole run.");

        if (t.Commit() != TransactionStatus.Committed) return new ChangeSystemResult(0, ["Revit rolled the change back: " + string.Join("; ", errors.Take(3))]);
        if (errors.Count > 0) notes.Add("Revit reported: " + string.Join("; ", errors.Distinct().Take(4)));
        return new ChangeSystemResult(changed, notes);
    }

    /// <summary>Warnings dismissed; errors (e.g. a connector classification that doesn't match) take Revit's default fix and are reported.</summary>
    private sealed class Lenient(List<string> errors) : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var f in accessor.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning) { accessor.DeleteWarning(f); continue; }
                errors.Add(f.GetDescriptionText());
                if (f.HasResolutions()) accessor.ResolveFailure(f);
            }
            return FailureProcessingResult.ProceedWithCommit;
        }
    }
}
