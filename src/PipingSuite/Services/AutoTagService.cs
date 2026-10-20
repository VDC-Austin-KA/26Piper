using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;
using static PipingSuite.Services.IsoMapService;

namespace PipingSuite.Services;

/// <param name="Replaced">Tags of the same type on this piping that were already in the view.</param>
public sealed record AutoTagResult(int Tags, int Values, int Replaced, IReadOnlyList<string> Notes);

/// <summary>
/// Tags the piping in a view once per unique value of a parameter, by default the EZ-ISO line number
/// {BC_SYSTEM}-{BC_PACKAGE}-{BC_LINE SEGMENT}: a header and the six identical branches off it are two lines, so two
/// tags. Each tag goes on its line's longest pipe, stacked in a column with shouldered leaders the way ISO Map does.
/// Tags of the same type already on this piping are replaced, so re-running leaves exactly one per value.
/// The caller holds the transaction.
/// </summary>
public sealed class AutoTagService(Document doc)
{
    public const string LineNumber = "Line number (BC_SYSTEM-BC_PACKAGE-BC_LINE SEGMENT)";

    private static readonly string[] LineParams = [EzIsoService.SystemParam, EzIsoService.PackageParam, EzIsoService.SegmentParam];

    /// <summary>The element's value to group by; null when it has none (for a line number, when any part is missing).</summary>
    public static string? Key(Element e, string parameter)
    {
        if (parameter != LineNumber) return ParameterHelper.GetValueByName(e, parameter);
        var parts = LineParams.Select(p => ParameterHelper.GetValueByName(e, p)).ToList();
        return parts.All(p => p is not null) ? string.Join("-", parts) : null;
    }

    public static List<IGrouping<string?, Element>> Groups(IEnumerable<Element> elements, string parameter) =>
        elements.Where(PipingElements.IsPiping).DistinctBy(e => e.Id)
            .GroupBy(e => Key(e, parameter))
            .Where(g => g.Key is not null)
            .OrderBy(g => g.Key!, ColorSplasherService.NaturalComparer.Instance)
            .ToList();

    /// <summary>Loaded pipe tag types, "ISO Tag : Pipe Size System Tag" first.</summary>
    public static List<FamilySymbol> TagTypes(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_PipeTags).Cast<FamilySymbol>()
            .OrderByDescending(s => s.FamilyName.Equals(TagFamily, StringComparison.OrdinalIgnoreCase) && s.Name.Equals(TagType, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.FamilyName).ThenBy(s => s.Name)
            .ToList();

    public AutoTagResult Tag(View view, IReadOnlyList<Element> elements, string parameter, FamilySymbol symbol)
    {
        var notes = new List<string>();
        if (view is View3D { IsLocked: false } v3)
        {
            v3.SaveOrientationAndLock();
            notes.Add("The 3D view was locked so it can hold tags.");
        }

        var ids = elements.Select(e => e.Id).ToHashSet();
        var old = new FilteredElementCollector(doc).OwnedByView(view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
            .Where(t => t.GetTypeId() == symbol.Id && t.GetTaggedLocalElementIds().Any(ids.Contains))
            .Select(t => t.Id).ToList();
        if (old.Count > 0) doc.Delete(old);

        var groups = Groups(elements, parameter);
        if (groups.Count == 0) return new AutoTagResult(0, 0, old.Count, [$"No piping here has a value for {parameter}."]);
        if (!symbol.IsActive) symbol.Activate();

        var frame = new ViewFrame(view);
        var placed = new IsoMapService(doc).PlaceTags(view, frame, symbol, elements, groups, notes);
        if (placed.Count > 0 && view.CropBoxActive) GrowCrop(view, placed.Aggregate((a, b) => a.Union(b)), notes);
        return new AutoTagResult(placed.Count, groups.Count, old.Count, notes);
    }

    /// <summary>Widens the crop just enough to show tags that landed outside it.</summary>
    private static void GrowCrop(View view, Rect2 tags, List<string> notes)
    {
        var crop = view.CropBox;
        var current = new Rect2(crop.Min.X, crop.Min.Y, crop.Max.X, crop.Max.Y);
        var all = current.Union(tags);
        if (all == current) return;
        double pad = 0.05 * Math.Max(tags.Width, tags.Height);
        crop.Min = new XYZ(all.MinX - (all.MinX < current.MinX ? pad : 0), all.MinY - (all.MinY < current.MinY ? pad : 0), crop.Min.Z);
        crop.Max = new XYZ(all.MaxX + (all.MaxX > current.MaxX ? pad : 0), all.MaxY + (all.MaxY > current.MaxY ? pad : 0), crop.Max.Z);
        view.CropBox = crop;
        notes.Add("The crop was widened to show the tags.");
    }
}
