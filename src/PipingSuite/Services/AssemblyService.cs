using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using PipingSuite.Core;

namespace PipingSuite.Services;

/// <param name="GroupByParameter">Optional parameter (e.g. LINENUMBER) that splits connected runs; null = connectivity only.</param>
/// <param name="IncludeConnectedFittings">Pull in unselected fittings/accessories touching selected pipes.</param>
public sealed record AssemblyOptions(string? GroupByParameter, bool IncludeConnectedFittings, string NamePrefix);

public sealed record AssemblyGroup(string? Key, IReadOnlyList<ElementId> ElementIds);

public sealed record AssemblyResult(IReadOnlyList<string> CreatedNames, int SkippedGroups, int AlreadyAssembled);

/// <summary>
/// Turns a piping selection into assemblies, one per logical run.
/// A run is a connected set of elements sharing the same group-by value.
/// </summary>
public sealed class AssemblyService(Document doc)
{
    // Characters Revit rejects in type names.
    private static readonly char[] InvalidNameChars = @"\:{}[]|;<>?`~".ToCharArray();

    public IReadOnlyList<AssemblyGroup> BuildGroups(IEnumerable<ElementId> selection, AssemblyOptions options, out int alreadyAssembled)
    {
        var all = selection.Select(doc.GetElement).Where(e => e is not null && PipingElements.IsPiping(e)).ToList();
        alreadyAssembled = all.Count(InAssembly);

        var members = all.Where(e => !InAssembly(e)).ToDictionary(e => e.Id);

        if (options.IncludeConnectedFittings)
        {
            foreach (var pipe in members.Values.Where(PipingElements.IsPipe).ToList())
            foreach (var n in PipingElements.GetConnectedElements(pipe))
            {
                if (!PipingElements.IsPipe(n) && PipingElements.IsPiping(n) && !InAssembly(n))
                    members.TryAdd(n.Id, n);
            }
        }

        var keys = members.Values.ToDictionary(
            e => e.Id,
            e => options.GroupByParameter is null ? null : ParameterHelper.GetValueByName(e, options.GroupByParameter));

        var neighbours = members.Values.ToDictionary(
            e => e.Id,
            e => PipingElements.GetConnectedElements(e).Select(n => n.Id).Where(members.ContainsKey).Distinct().ToList());

        var uf = new UnionFind(members.Keys);

        // Pass 1: join neighbours with identical keys (both null counts as identical).
        foreach (var (id, ns) in neighbours)
        foreach (var n in ns)
            if (keys[id] == keys[n]) uf.Union(id, n);

        // Pass 2: an un-keyed cluster (typically fittings without LINENUMBER) joins the first keyed
        // neighbour it touches. Joining only once stops a blank tee from bridging two lines.
        if (options.GroupByParameter is not null)
        {
            var rootKey = members.Keys.Where(id => keys[id] is not null)
                                      .GroupBy(uf.Find)
                                      .ToDictionary(g => g.Key, g => keys[g.First()]);

            foreach (var (id, ns) in neighbours)
            {
                if (keys[id] is not null || rootKey.ContainsKey(uf.Find(id))) continue;

                var keyedNeighbour = ns.FirstOrDefault(n => rootKey.ContainsKey(uf.Find(n)));
                if (keyedNeighbour is null) continue;

                var key = rootKey[uf.Find(keyedNeighbour)];
                uf.Union(keyedNeighbour, id);
                rootKey[uf.Find(id)] = key;
            }

            return Collect(uf, members.Keys, root => rootKey.GetValueOrDefault(root));
        }

        return Collect(uf, members.Keys, _ => null);
    }

    /// <summary>
    /// Creates the assemblies. Renaming must happen in a second transaction because the
    /// AssemblyType only exists once the creating transaction is committed.
    /// </summary>
    public AssemblyResult CreateAssemblies(IReadOnlyList<AssemblyGroup> groups, AssemblyOptions options, int alreadyAssembled)
    {
        var created = new List<(AssemblyInstance Instance, string? Key)>();
        int skipped = 0;

        using var tg = new TransactionGroup(doc, "Create Piping Assemblies");
        tg.Start();

        using (var t = new Transaction(doc, "Create Assemblies"))
        {
            t.Start();
            foreach (var g in groups)
            {
                var namingCategory = GetNamingCategory(g.ElementIds);
                if (namingCategory is null || !AssemblyInstance.AreElementsValidForAssembly(doc, g.ElementIds.ToList(), namingCategory))
                {
                    skipped++;
                    continue;
                }
                created.Add((AssemblyInstance.Create(doc, g.ElementIds.ToList(), namingCategory), g.Key));
            }
            t.Commit();
        }

        var names = new List<string>();
        using (var t = new Transaction(doc, "Name Assemblies"))
        {
            t.Start();
            var used = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(AssemblyType)).Select(e => e.Name),
                StringComparer.OrdinalIgnoreCase);

            foreach (var (instance, key) in created)
            {
                var name = UniqueName(BuildBaseName(options.NamePrefix, key), used);
                instance.AssemblyTypeName = name;
                names.Add(name);
            }
            t.Commit();
        }

        tg.Assimilate();
        return new AssemblyResult(names, skipped, alreadyAssembled);
    }

    private static bool InAssembly(Element e) => e.AssemblyInstanceId != ElementId.InvalidElementId;

    /// <summary>Prefer the pipe category so assemblies are listed under Pipes in the browser.</summary>
    private ElementId? GetNamingCategory(IEnumerable<ElementId> ids)
    {
        var categories = ids.Select(id => doc.GetElement(id).Category?.Id).OfType<ElementId>().Distinct().ToList();
        var pipeCat = new ElementId(BuiltInCategory.OST_PipeCurves);
        if (categories.Contains(pipeCat) && AssemblyInstance.IsValidNamingCategory(doc, pipeCat, ids.ToList()))
            return pipeCat;
        return categories.FirstOrDefault(c => AssemblyInstance.IsValidNamingCategory(doc, c, ids.ToList()));
    }

    private static string BuildBaseName(string prefix, string? key)
    {
        var raw = string.IsNullOrWhiteSpace(key) ? prefix : $"{prefix}{key}";
        var clean = new string(raw.Where(c => !InvalidNameChars.Contains(c)).ToArray()).Trim();
        return clean.Length == 0 ? "PipeAssembly" : clean;
    }

    private static string UniqueName(string baseName, HashSet<string> used)
    {
        for (int i = 1; ; i++)
        {
            var candidate = $"{baseName}-{i:000}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static List<AssemblyGroup> Collect(UnionFind uf, IEnumerable<ElementId> ids, Func<ElementId, string?> keyOfRoot) =>
        ids.GroupBy(uf.Find)
           .Select(g => new AssemblyGroup(keyOfRoot(g.Key), g.ToList()))
           .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
           .ToList();

    private sealed class UnionFind
    {
        private readonly Dictionary<ElementId, ElementId> _parent;

        public UnionFind(IEnumerable<ElementId> ids) => _parent = ids.ToDictionary(id => id, id => id);

        public ElementId Find(ElementId id)
        {
            while (_parent[id] != id)
            {
                _parent[id] = _parent[_parent[id]]; // path halving
                id = _parent[id];
            }
            return id;
        }

        public void Union(ElementId a, ElementId b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb) _parent[rb] = ra;
        }
    }
}
