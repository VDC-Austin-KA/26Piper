using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;

namespace PipingSuite.Services;

/// <param name="Values">One per editable column; null where the element doesn't have that parameter.</param>
public sealed record GridRow(long Id, string Category, string FamilyType, string SystemType, string?[] Values);

/// <param name="Columns">The "Set" group's writable instance parameters found on the elements.</param>
public sealed record GridData(IReadOnlyList<string> Columns, IReadOnlyList<GridRow> Rows);

public sealed record CellWrite(long Id, string Parameter, string Value);

/// <summary>
/// Parameter Grid: reads the "Set" parameter group (BC_SYSTEM, BC_PACKAGE, BC_LINE SEGMENT, BC_REV…) of the elements
/// in the active view or selection into rows for a spreadsheet-style editor, and writes only the changed cells back,
/// in one transaction. Values are text as Revit shows them; numbers and lengths are parsed in project units.
/// Loading reads each cell with one direct lookup; connectivity (runs and lines) is worked out only when asked for.
/// </summary>
public sealed class ParamGridService(Document doc)
{
    public const string SetGroup = "Set";

    private static readonly ElementMulticategoryFilter Piping = new(new List<BuiltInCategory>
    {
        BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
    });

    /// <summary>Elements visible in the view (or the given selection), piping only or every model element.</summary>
    public List<Element> Scope(View view, ICollection<ElementId> selection, bool selectionOnly, bool pipingOnly)
    {
        if (selectionOnly)
            return selection.Select(doc.GetElement).OfType<Element>()
                .Where(e => e.Category is { CategoryType: CategoryType.Model } && (!pipingOnly || Piping.PassesFilter(e))).DistinctBy(e => e.Id).ToList();
        var collector = new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType();
        if (pipingOnly) collector = collector.WherePasses(Piping); // filtered inside Revit, not element by element here
        return collector.Where(e => e.Category is { CategoryType: CategoryType.Model }).ToList();
    }

    public GridData Load(IReadOnlyList<Element> elements)
    {
        // Columns from one element per category and type: parameters are bound per category (family ones per type),
        // so the rest of each group has the same ones.
        var found = new Dictionary<string, Definition>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in elements.DistinctBy(e => (e.Category?.Id.Value, e.GetTypeId().Value)))
        foreach (Parameter p in e.Parameters)
            if (Editable(p)) found.TryAdd(p.Definition.Name, p.Definition);
        var columns = found.Keys.OrderBy(n => n, ColorSplasherService.NaturalComparer.Instance).ToList();
        var definitions = columns.Select(c => found[c]).ToArray();

        var types = new Dictionary<long, string>();
        string TypeName(Element e)
        {
            var id = e.GetTypeId();
            if (types.TryGetValue(id.Value, out var name)) return name;
            return types[id.Value] = doc.GetElement(id) is ElementType t ? $"{t.FamilyName} : {t.Name}" : e.Name;
        }

        var rows = elements.Select(e => new GridRow(e.Id.Value, e.Category?.Name ?? string.Empty, TypeName(e),
                e.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsValueString() ?? string.Empty,
                columns.Select((c, i) => Cell(e, definitions[i], c) is { } p ? ParameterHelper.GetDisplayValue(p) : null).ToArray()))
            .ToList();
        return new GridData(columns, rows);
    }

    /// <summary>
    /// Each piping element's connected run ("R3") and line ("L12": a branch, or a header run between branches, as EZ-ISO
    /// Check splits them). Only worked out when a feature needs it, since it walks every connection.
    /// </summary>
    public Dictionary<long, (string Run, string Line)> Topology(IReadOnlyList<Element> elements)
    {
        var piping = elements.Where(e => e.IsValidObject && PipingElements.IsPiping(e)).ToDictionary(e => e.Id);
        var uf = new UnionFind(piping.Keys);
        foreach (var e in piping.Values)
        foreach (var n in PipingElements.GetConnectedElements(e).Where(n => piping.ContainsKey(n.Id)))
            uf.Union(e.Id, n.Id);
        var runs = piping.Keys.GroupBy(uf.Find).OrderBy(g => g.Min(id => id.Value)).Select((g, i) => (g, i))
            .SelectMany(x => x.g.Select(id => (id, Name: $"R{x.i + 1}"))).ToDictionary(x => x.id, x => x.Name);
        var lines = EzIsoService.LineOf([.. piping.Values]);
        return piping.Keys.ToDictionary(id => id.Value, id => (runs[id], lines.TryGetValue(id, out var l) ? $"L{l}" : string.Empty));
    }

    /// <summary>The column's parameter on an element: by its definition (one lookup), else by name for a same-named family parameter.</summary>
    private static Parameter? Cell(Element e, Definition definition, string name) =>
        e.get_Parameter(definition) is { } p && Editable(p) ? p
        : e.LookupParameter(name) is { } q && Editable(q) ? q
        : null;

    /// <summary>Every parameter on the elements (instance and type), as "Name (Group)", to copy values from.</summary>
    public static List<string> SourceParameters(Document doc, IEnumerable<Element> elements) =>
        elements.Take(500).SelectMany(e => e.Parameters.Cast<Parameter>().Concat(doc.GetElement(e.GetTypeId())?.Parameters.Cast<Parameter>() ?? []))
            .Where(p => p.StorageType != StorageType.None)
            .Select(p => $"{p.Definition.Name} ({GroupLabel(p)})")
            .Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Each element's value of a "Name (Group)" parameter: instance first, then type.</summary>
    public Dictionary<long, string?> SourceValues(string source, IEnumerable<long> ids)
    {
        int cut = source.LastIndexOf(" (", StringComparison.Ordinal);
        var name = cut > 0 ? source[..cut] : source;
        return ids.Distinct().ToDictionary(id => id, id => doc.GetElement(new ElementId(id)) is { } e ? ParameterHelper.GetValueByName(e, name) : null);
    }

    /// <summary>Writes the changed cells in one transaction; returns how many were written and why the rest weren't.</summary>
    public (int Written, List<string> Errors) Apply(IReadOnlyList<CellWrite> writes)
    {
        var errors = new List<string>();
        int written = 0;
        using var t = new Transaction(doc, "Parameter Grid");
        t.Start();
        foreach (var w in writes)
        {
            var e = doc.GetElement(new ElementId(w.Id));
            var p = e is null ? null : Find(e, w.Parameter);
            string? why = p is null ? "not found" : p.IsReadOnly ? "read-only" : Set(p, w.Value);
            if (why is null) written++;
            else errors.Add($"[{w.Id}] {w.Parameter} = \"{w.Value}\": {why}");
        }
        t.Commit();
        return (written, errors);
    }

    /// <summary>A writable instance parameter in the "Set" group holding text or a number.</summary>
    private static bool Editable(Parameter p) =>
        !p.IsReadOnly && p.StorageType is StorageType.String or StorageType.Integer or StorageType.Double
        && GroupLabel(p).Equals(SetGroup, StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> Groups = [];

    /// <summary>The parameter group's name as Revit shows it ("Set", "Mechanical"…), cached; "Other" for parameters with no group.</summary>
    private static string GroupLabel(Parameter p)
    {
        var group = p.Definition.GetGroupTypeId();
        if (group.Empty()) return "Other";
        if (Groups.TryGetValue(group.TypeId, out var label)) return label;
        try { label = LabelUtils.GetLabelForGroup(group); }
        catch (Autodesk.Revit.Exceptions.ArgumentException) { label = group.TypeId; }
        return Groups[group.TypeId] = label;
    }

    private static Parameter? Find(Element e, string name) =>
        e.LookupParameter(name) is { } p && Editable(p) ? p
        : e.Parameters.Cast<Parameter>().FirstOrDefault(p => p.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && Editable(p));

    /// <returns>Null when written, else why not.</returns>
    private string? Set(Parameter p, string value)
    {
        switch (p.StorageType)
        {
            case StorageType.String:
                return p.Set(value) ? null : "Revit refused the value";
            case StorageType.Integer when p.Definition.GetDataType() == SpecTypeId.Boolean.YesNo:
                var yes = value.Trim().ToUpperInvariant() is "YES" or "Y" or "TRUE" or "1";
                var no = value.Trim().ToUpperInvariant() is "NO" or "N" or "FALSE" or "0";
                return yes || no ? (p.Set(yes ? 1 : 0) ? null : "Revit refused the value") : "not Yes or No";
            case StorageType.Integer:
                return int.TryParse(value.Trim(), out var n) ? (p.Set(n) ? null : "Revit refused the value") : "not a whole number";
            default:
                var spec = p.Definition.GetDataType();
                bool parsed = UnitUtils.IsMeasurableSpec(spec)
                    ? UnitFormatUtils.TryParse(doc.GetUnits(), spec, value, out double d)
                    : double.TryParse(value.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out d);
                return parsed ? (p.Set(d) ? null : "Revit refused the value") : "not a number in project units";
        }
    }
}
