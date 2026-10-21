using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PipingSuite.Core;
using PipingSuite.Services;
using PipingSuite.UI.ViewModels;
using PipingSuite.UI.Views;

namespace PipingSuite.Commands;

/// <summary>
/// Spreadsheet-style editor for the "Set" parameter group of the elements in the active view (or the selection).
/// The window is modal, so its buttons run inside this command's API context; only Apply changes the model.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class ParamGridCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uiDoc = commandData.Application.ActiveUIDocument;
        try
        {
            if (uiDoc.Document.ActiveView is not { IsTemplate: false })
            {
                TaskDialog.Show("Parameter Grid", "Open a view to edit the parameters of what's in it.");
                return Result.Cancelled;
            }
            var vm = new ParamGridViewModel(new Host(uiDoc));
            WindowHelper.ShowModal(new ParamGridWindow(vm), commandData.Application);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private sealed class Host(UIDocument uiDoc) : IParamGridHost
    {
        private readonly ParamGridService _service = new(uiDoc.Document);
        private List<Element> _scope = [];

        public GridData Load(bool selectionOnly, bool pipingOnly)
        {
            _scope = _service.Scope(uiDoc.Document.ActiveView, uiDoc.Selection.GetElementIds(), selectionOnly, pipingOnly);
            return _service.Load(_scope);
        }

        public IReadOnlyDictionary<long, (string Run, string Line)> Topology() => _service.Topology(_scope);

        public IReadOnlyList<string> SourceParameters() => ParamGridService.SourceParameters(uiDoc.Document, _scope.Where(e => e.IsValidObject));

        public IReadOnlyDictionary<long, string?> SourceValues(string source, IReadOnlyCollection<long> ids) => _service.SourceValues(source, ids);

        public (int Written, IReadOnlyList<string> Errors) Apply(IReadOnlyList<CellWrite> writes)
        {
            var (written, errors) = _service.Apply(writes);
            uiDoc.RefreshActiveView();
            return (written, errors);
        }

        public void Show(IReadOnlyCollection<long> ids)
        {
            var found = ids.Select(id => new ElementId(id)).Where(id => uiDoc.Document.GetElement(id) is not null).ToList();
            if (found.Count == 0) return;
            uiDoc.Selection.SetElementIds(found);
            uiDoc.ShowElements(found);
        }

        /// <summary>"PipingSuite Parameters - project - view": the import reads ElementId and parameter-name headers, whatever the name.</summary>
        public string DefaultFileName =>
            string.Concat($"PipingSuite Parameters - {uiDoc.Document.Title} - {uiDoc.Document.ActiveView.Name}"
                .Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
    }
}
