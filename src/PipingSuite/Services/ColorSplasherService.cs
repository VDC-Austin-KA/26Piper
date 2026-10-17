using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;

namespace PipingSuite.Services;

public sealed record SplashCategory(string Name, ElementId Id, IReadOnlyList<string> ParameterNames);

public sealed record SplashValue(string Value, IReadOnlyList<ElementId> ElementIds);

/// <summary>
/// Colour-by-value for the active view (Color Splasher workflow): list categories visible in the
/// view, the parameters they carry, and the distinct values; then override each element's graphics
/// with its value's colour. Overrides are per element in this view only and are fully reversible.
/// </summary>
public sealed class ColorSplasherService(Document doc, View view)
{
    public const string NoValue = "(no value)";

    public IReadOnlyList<SplashCategory> GetCategories() =>
        ElementsInView()
            .GroupBy(e => e.Category.Id)
            .Select(g => new SplashCategory(g.First().Category.Name, g.Key, ParameterNames(g)))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Distinct values of <paramref name="parameter"/> (instance, else type) across the categories.</summary>
    public IReadOnlyList<SplashValue> GetValues(IReadOnlyCollection<ElementId> categoryIds, string parameter) =>
        ElementsInView(categoryIds)
            .GroupBy(e => ParameterHelper.GetValueByName(e, parameter) ?? NoValue)
            .Select(g => new SplashValue(g.Key, g.Select(e => e.Id).ToList()))
            .OrderBy(v => v.Value == NoValue) // "(no value)" last
            .ThenBy(v => v.Value, NaturalComparer.Instance)
            .ToList();

    /// <param name="colorLines">False keeps edges black (shaded iso maps); only the surface and cut fills are coloured.</param>
    public void Apply(IEnumerable<(IReadOnlyList<ElementId> Ids, Color Color)> entries, bool colorLines = true)
    {
        var solidFill = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement))
            .Cast<FillPatternElement>()
            .FirstOrDefault(f => f.GetFillPattern().IsSolidFill)?.Id;

        using var t = new Transaction(doc, "Color Splasher");
        t.Start();
        foreach (var (ids, color) in entries)
        {
            var ogs = new OverrideGraphicSettings()
                .SetSurfaceForegroundPatternColor(color)
                .SetCutForegroundPatternColor(color);
            if (colorLines) ogs.SetProjectionLineColor(color).SetCutLineColor(color);
            if (solidFill is not null)
                ogs.SetSurfaceForegroundPatternId(solidFill).SetCutForegroundPatternId(solidFill);

            foreach (var id in ids) view.SetElementOverrides(id, ogs);
        }
        t.Commit();
    }

    /// <summary>Resets element overrides for the given categories (all visible elements when empty).</summary>
    public int Clear(IReadOnlyCollection<ElementId> categoryIds)
    {
        var reset = new OverrideGraphicSettings();
        int count = 0;
        using var t = new Transaction(doc, "Color Splasher - Clear");
        t.Start();
        foreach (var e in ElementsInView(categoryIds.Count > 0 ? categoryIds : null))
        {
            view.SetElementOverrides(e.Id, reset);
            count++;
        }
        t.Commit();
        return count;
    }

    private IEnumerable<Element> ElementsInView(IReadOnlyCollection<ElementId>? categoryIds = null)
    {
        var collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().WhereElementIsViewIndependent();
        if (categoryIds is not null) collector = collector.WherePasses(new ElementMulticategoryFilter(categoryIds.ToList()));
        return collector.Where(e => e.Category is { CategoryType: CategoryType.Model });
    }

    /// <summary>Instance and type parameter names found on any element of the category.</summary>
    private IReadOnlyList<string> ParameterNames(IEnumerable<Element> elements)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTypes = new HashSet<ElementId>();
        foreach (var e in elements)
        {
            foreach (Parameter p in e.Parameters) names.Add(p.Definition.Name);
            var typeId = e.GetTypeId();
            if (typeId != ElementId.InvalidElementId && seenTypes.Add(typeId) && doc.GetElement(typeId) is { } type)
                foreach (Parameter p in type.Parameters) names.Add(p.Definition.Name);
        }
        names.Remove("Category");
        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Sorts "L2" before "L10".</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (double.TryParse(x, out var a) && double.TryParse(y, out var b)) return a.CompareTo(b);
            return string.Compare(Pad(x), Pad(y), StringComparison.OrdinalIgnoreCase);
        }

        private static string Pad(string? s) =>
            System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\d+", m => m.Value.PadLeft(10, '0'));
    }
}
