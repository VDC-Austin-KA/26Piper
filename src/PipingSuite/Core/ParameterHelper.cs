using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PipingSuite.Core;

/// <summary>
/// Value captured from a source parameter. Identity is kept three ways so the
/// target lookup is as precise as possible: shared GUID, then built-in id, then name.
/// </summary>
public sealed record ParameterSnapshot(
    string Name,
    Guid? SharedGuid,
    BuiltInParameter BuiltIn,
    string DataTypeId,
    StorageType StorageType,
    object? Value,
    string DisplayValue);

public enum PasteOutcome { Applied, NotFound, ReadOnly, TypeMismatch, Failed }

public static class ParameterHelper
{
    // Changing these swaps the element's type/family rather than copying data.
    private static readonly HashSet<BuiltInParameter> Excluded =
    [
        BuiltInParameter.ELEM_TYPE_PARAM,
        BuiltInParameter.ELEM_FAMILY_PARAM,
        BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM,
        BuiltInParameter.SYMBOL_ID_PARAM,
    ];

    private static string StripVersion(string typeId)
    {
        int dash = typeId.LastIndexOf('-');
        return dash > 0 ? typeId[..dash] : typeId;
    }

    public static BuiltInParameter GetBuiltIn(Parameter p) =>
        p.Definition is InternalDefinition d ? d.BuiltInParameter : BuiltInParameter.INVALID;

    /// <summary>Writable, valued instance parameters worth offering in the copy dialog.</summary>
    public static IEnumerable<Parameter> GetCopyableParameters(Element e) =>
        e.Parameters.Cast<Parameter>()
            .Where(p => !p.IsReadOnly
                        && p.HasValue
                        && p.StorageType != StorageType.None
                        && !Excluded.Contains(GetBuiltIn(p)))
            .OrderBy(p => p.Definition.Name, StringComparer.OrdinalIgnoreCase);

    public static ParameterSnapshot Snapshot(Parameter p)
    {
        object? value = p.StorageType switch
        {
            StorageType.String => p.AsString(),
            StorageType.Integer => p.AsInteger(),
            StorageType.Double => p.AsDouble(),
            StorageType.ElementId => p.AsElementId(),
            _ => null,
        };

        return new ParameterSnapshot(
            p.Definition.Name,
            p.IsShared ? p.GUID : null,
            GetBuiltIn(p),
            p.Definition.GetDataType().TypeId,
            p.StorageType,
            value,
            GetDisplayValue(p));
    }

    public static string GetDisplayValue(Parameter p) =>
        p.AsValueString() ?? p.AsString() ?? string.Empty;

    /// <summary>Instance value first, then type value (e.g. LINENUMBER may live on either).</summary>
    public static string? GetValueByName(Element e, string name)
    {
        var p = e.LookupParameter(name)
                ?? e.Document.GetElement(e.GetTypeId())?.LookupParameter(name);
        if (p is null || !p.HasValue) return null;
        var v = GetDisplayValue(p);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public static Parameter? FindMatching(Element target, ParameterSnapshot s)
    {
        if (s.SharedGuid is { } guid) return target.get_Parameter(guid);
        if (s.BuiltIn != BuiltInParameter.INVALID) return target.get_Parameter(s.BuiltIn);
        return target.LookupParameter(s.Name);
    }

    /// <param name="sameDocument">ElementId values are only meaningful in the source document.</param>
    public static PasteOutcome TryPaste(Element target, ParameterSnapshot s, bool sameDocument)
    {
        var p = FindMatching(target, s);
        if (p is null) return PasteOutcome.NotFound;
        if (p.IsReadOnly) return PasteOutcome.ReadOnly;

        if (p.StorageType != s.StorageType) return PasteOutcome.TypeMismatch;

        // GUID/built-in matches are the same parameter by definition. A name-only match could hit an
        // unrelated parameter, so also require the same spec (ignoring the "-2.0.0" schema version).
        bool nameOnlyMatch = s.SharedGuid is null && s.BuiltIn == BuiltInParameter.INVALID;
        if (nameOnlyMatch && StripVersion(p.Definition.GetDataType().TypeId) != StripVersion(s.DataTypeId))
            return PasteOutcome.TypeMismatch;

        try
        {
            bool ok = s.StorageType switch
            {
                StorageType.String => p.Set((string?)s.Value ?? string.Empty),
                StorageType.Integer => p.Set((int)s.Value!),
                StorageType.Double => p.Set((double)s.Value!),
                StorageType.ElementId when sameDocument => p.Set((ElementId)s.Value!),
                _ => false,
            };
            return ok ? PasteOutcome.Applied : PasteOutcome.Failed;
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            return PasteOutcome.Failed;
        }
    }
}
