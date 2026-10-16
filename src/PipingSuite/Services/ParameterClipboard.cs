using System;
using System.Collections.Generic;
using PipingSuite.Core;

namespace PipingSuite.Services;

/// <summary>
/// Session-scoped store between the Copy and Paste commands. Revit commands run on
/// the single UI thread, so no locking is needed.
/// </summary>
public static class ParameterClipboard
{
    public static IReadOnlyList<ParameterSnapshot> Items { get; private set; } = [];

    /// <summary>Document the values came from; ElementId values are only pasted back into it.</summary>
    public static Guid SourceDocumentGuid { get; private set; }

    public static string SourceDescription { get; private set; } = string.Empty;

    public static bool IsEmpty => Items.Count == 0;

    public static void Set(IReadOnlyList<ParameterSnapshot> items, Guid sourceDocumentGuid, string sourceDescription)
    {
        Items = items;
        SourceDocumentGuid = sourceDocumentGuid;
        SourceDescription = sourceDescription;
    }
}
