using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using PipingSuite.Core;

namespace PipingSuite.Services;

/// <param name="Detail">Its "DETAIL A"-style value, if it has one.</param>
/// <param name="Signature">How it is built (pipe types and cut lengths, fitting and valve types), whichever way it faces.</param>
public sealed record Configuration(IReadOnlyList<Element> Elements, string? Detail, string Signature);

/// <param name="Name">View name without the "ISO - " prefix.</param>
public sealed record ConfigurationType(string Name, IReadOnlyList<Configuration> Members);

/// <param name="DetailParameter">Parameter holding "DETAIL A"-style values; empty to group by build only.</param>
/// <param name="OnePerType">One view per type (its first configuration); otherwise one view per configuration.</param>
/// <param name="Color">Colour Splasher by BC_LINE SEGMENT; <paramref name="Rainbow"/> for a rainbow gradient, else distinct colours.</param>
public sealed record IsoViewsOptions(IsoCorner Corner, int Scale, string DetailParameter, bool OnePerType, bool Tag, bool Color, bool Rainbow);

public sealed record IsoViewsResult(IReadOnlyList<View3D> Views, IReadOnlyList<string> Notes);

/// <summary>
/// ISO Views: splits a selection into configurations (connected pieces), sorts them into types by their
/// DETAIL value (or, without one, by how they're built), and makes a locked isometric of each type the way
/// ISO Map does: only that configuration shown, coloured and tagged by line segment, and a square crop, all from
/// the same corner and scale so they line up on sheets.
/// </summary>
public sealed class IsoViewsService(Document doc)
{
    private static readonly Regex DetailValue = new(@"^\s*DETAIL\s+[A-Z]{1,3}\s*$", RegexOptions.IgnoreCase);

    /// <summary>The text parameter in the selection whose values look most like "DETAIL A".</summary>
    public static string? FindDetailParameter(IEnumerable<Element> elements) =>
        elements.SelectMany(e => e.Parameters.Cast<Parameter>())
            .Where(p => p.StorageType == StorageType.String && p.AsString() is { } v && DetailValue.IsMatch(v))
            .GroupBy(p => p.Definition.Name).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

    public static List<Configuration> Configurations(IReadOnlyList<Element> elements, string detailParameter)
    {
        var byId = elements.Where(PipingElements.IsPiping).DistinctBy(e => e.Id).ToDictionary(e => e.Id);
        var uf = new UnionFind(byId.Keys);
        foreach (var e in byId.Values)
        foreach (var n in PipingElements.GetConnectedElements(e).Where(n => byId.ContainsKey(n.Id)))
            uf.Union(e.Id, n.Id);

        return byId.Keys.GroupBy(uf.Find)
            .Select(g => g.Select(id => byId[id]).ToList())
            .Select(members => new Configuration(members, Detail(members, detailParameter), Signature(members)))
            .OrderBy(c => c.Detail ?? "￿", ColorSplasherService.NaturalComparer.Instance)
            .ThenBy(c => c.Elements.Min(e => e.Id.Value))
            .ToList();
    }

    /// <summary>
    /// Types: configurations with the same DETAIL value are one type; ones without a value are typed by how
    /// they're built. Notes flag a DETAIL whose configurations are built differently, and different DETAILs built alike.
    /// </summary>
    public static (List<ConfigurationType> Types, List<string> Notes) Types(IReadOnlyList<Configuration> configurations)
    {
        var notes = new List<string>();
        var types = new List<ConfigurationType>();
        foreach (var g in configurations.Where(c => c.Detail is not null).GroupBy(c => c.Detail!.Trim().ToUpperInvariant()))
        {
            types.Add(new ConfigurationType(g.First().Detail!.Trim(), g.ToList()));
            int odd = g.Count(c => c.Signature != g.First().Signature);
            if (odd > 0) notes.Add($"{g.Key}: {odd} of {g.Count()} configuration(s) are built differently from the one shown.");
        }
        foreach (var same in types.GroupBy(t => t.Members[0].Signature).Where(s => s.Count() > 1))
            notes.Add($"{string.Join(", ", same.Select(t => t.Name))} are built the same.");

        int n = 0;
        foreach (var g in configurations.Where(c => c.Detail is null).GroupBy(c => c.Signature))
        {
            var key = LineKey(g.First().Elements);
            types.Add(new ConfigurationType(key ?? $"TYPE {++n}", g.ToList()));
        }
        return (types, notes);
    }

    public IsoViewsResult Create(IReadOnlyList<ConfigurationType> types, IsoViewsOptions options)
    {
        var views = new List<View3D>();
        var notes = new List<string>();
        var iso = new IsoMapService(doc);

        using var tg = new TransactionGroup(doc, "ISO Views");
        tg.Start();
        foreach (var type in types)
        {
            var shown = options.OnePerType ? type.Members.Take(1) : type.Members;
            foreach (var c in shown)
            {
                var result = iso.Create(c.Elements, new IsoMapOptions($"ISO - {type.Name}", options.Corner, options.Scale,
                    EzIsoService.SegmentParam, options.Rainbow, TagSegments: options.Tag, Color: options.Color));
                views.Add(result.View);
                notes.AddRange(result.Warnings.Select(w => $"{result.View.Name}: {w}"));
            }
        }
        tg.Assimilate();
        return new IsoViewsResult(views, notes);
    }

    private static string? Detail(IEnumerable<Element> elements, string parameter) =>
        string.IsNullOrWhiteSpace(parameter) ? null
            : elements.Select(e => ParameterHelper.GetValueByName(e, parameter)).OfType<string>().Where(v => DetailValue.IsMatch(v))
                .GroupBy(v => v.Trim(), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

    /// <summary>Pipe types with cut lengths to 1/16", plus fitting and accessory types: the same whichever way it faces.</summary>
    private static string Signature(IEnumerable<Element> elements) => string.Join("|", elements
        .Select(e => e is MEPCurve { Location: LocationCurve lc }
            ? $"{e.GetTypeId().Value}:{Math.Round(lc.Curve.Length * 192)}"
            : e.GetTypeId().Value.ToString())
        .OrderBy(s => s, StringComparer.Ordinal));

    /// <summary>The EZ-ISO line number of the configuration's longest pipe, if it has one.</summary>
    private static string? LineKey(IEnumerable<Element> elements)
    {
        var pipe = elements.OfType<MEPCurve>().MaxBy(p => (p.Location as LocationCurve)?.Curve.Length ?? 0);
        if (pipe is null) return null;
        var parts = new[] { EzIsoService.SystemParam, EzIsoService.PackageParam, EzIsoService.SegmentParam }
            .Select(p => ParameterHelper.GetValueByName(pipe, p)).ToList();
        return parts.All(p => p is not null) ? string.Join("-", parts) : null;
    }
}
