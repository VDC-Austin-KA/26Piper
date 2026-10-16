using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;

namespace PipingSuite.Services;

public sealed record CombineRequest(string First, string Second, string Separator, string NewName);

public sealed record CombineResult(bool CreatedParameter, int CategoryCount, int UpdatedElements);

/// <summary>
/// Creates (or reuses) a text shared parameter holding "First{sep}Second" and fills it on every
/// element in the categories both sources are bound to. Values are a snapshot: re-run to refresh.
/// </summary>
public sealed class CombinedParameterService(Document doc)
{
    private const string GroupName = "PipingSuite";

    /// <summary>Project and shared parameters bound in this document, with their categories.</summary>
    public Dictionary<string, CategorySet> GetBoundParameters()
    {
        var result = new Dictionary<string, CategorySet>(StringComparer.OrdinalIgnoreCase);
        var it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
        {
            if (it.Key is Definition def && it.Current is ElementBinding binding)
                result[def.Name] = binding.Categories;
        }
        return result;
    }

    public CombineResult Combine(CombineRequest r)
    {
        var bound = GetBoundParameters();
        var categories = Intersect(bound[r.First], bound[r.Second]);
        if (categories.IsEmpty)
            throw new InvalidOperationException($"\"{r.First}\" and \"{r.Second}\" are not bound to any common category.");

        using var t = new Transaction(doc, "Combine Parameters");
        t.Start();

        bool created = !bound.ContainsKey(r.NewName);
        if (created)
        {
            WithDefinition(r.NewName, def =>
                doc.ParameterBindings.Insert(def, new InstanceBinding(categories), GroupTypeId.Data));
        }
        else
        {
            // Existing parameter: make sure it also covers the source categories.
            var def = FindBoundDefinition(r.NewName);
            var merged = Union(bound[r.NewName], categories);
            doc.ParameterBindings.ReInsert(def, new InstanceBinding(merged), GroupTypeId.Data);
        }
        doc.Regenerate();

        var categoryIds = categories.Cast<Category>().Select(c => c.Id).ToList();
        var elements = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .WherePasses(new ElementMulticategoryFilter(categoryIds));

        int updated = 0;
        foreach (var e in elements)
        {
            var a = ParameterHelper.GetValueByName(e, r.First);
            var b = ParameterHelper.GetValueByName(e, r.Second);
            if (a is null && b is null) continue;

            var target = e.LookupParameter(r.NewName);
            if (target is null || target.IsReadOnly || target.StorageType != StorageType.String) continue;

            var value = $"{a}{r.Separator}{b}";
            if (target.AsString() == value) continue;
            if (target.Set(value)) updated++;
        }

        t.Commit();
        return new CombineResult(created, categoryIds.Count, updated);
    }

    private Definition FindBoundDefinition(string name)
    {
        var it = doc.ParameterBindings.ForwardIterator();
        while (it.MoveNext())
            if (it.Key is Definition d && d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return d;
        throw new InvalidOperationException($"Parameter \"{name}\" not found.");
    }

    /// <summary>
    /// Uses the user's shared parameter file when one is set; otherwise a private file under
    /// %AppData%\PipingSuite. <paramref name="use"/> runs while that file is active; the user's
    /// setting is always restored afterwards.
    /// </summary>
    private void WithDefinition(string name, Action<ExternalDefinition> use)
    {
        var app = doc.Application;
        var original = app.SharedParametersFilename;
        try
        {
            if (string.IsNullOrEmpty(original) || !File.Exists(original))
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PipingSuite", "SharedParameters.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (!File.Exists(path)) File.WriteAllText(path, string.Empty);
                app.SharedParametersFilename = path;
            }

            var file = app.OpenSharedParameterFile()
                       ?? throw new InvalidOperationException("Could not open the shared parameter file.");

            // Reuse a same-named definition from any group so the GUID stays stable across projects.
            var existing = file.Groups.SelectMany(g => g.Definitions).OfType<ExternalDefinition>()
                .FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                var group = file.Groups.get_Item(GroupName) ?? file.Groups.Create(GroupName);
                existing = (ExternalDefinition)group.Definitions.Create(
                    new ExternalDefinitionCreationOptions(name, SpecTypeId.String.Text)
                    {
                        Description = "Combined value created by Piping Productivity Suite.",
                    });
            }
            use(existing);
        }
        finally
        {
            if (app.SharedParametersFilename != original) app.SharedParametersFilename = original ?? string.Empty;
        }
    }

    private CategorySet Intersect(CategorySet a, CategorySet b)
    {
        var set = doc.Application.Create.NewCategorySet();
        foreach (Category c in a)
            if (b.Contains(c)) set.Insert(c);
        return set;
    }

    private CategorySet Union(CategorySet a, CategorySet b)
    {
        var set = doc.Application.Create.NewCategorySet();
        foreach (Category c in a) set.Insert(c);
        foreach (Category c in b) set.Insert(c);
        return set;
    }
}
