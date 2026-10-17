using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PipingSuite.Core;

/// <summary>Disjoint sets of element ids, for grouping connected runs.</summary>
internal sealed class UnionFind
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
