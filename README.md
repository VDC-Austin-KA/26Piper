# Piping Productivity Suite (Revit 2026)

Revit 2026 add-in (.NET 8, WPF/MVVM) for piping assemblies, parameter management, view filters and fitting manipulation.

## Build & install

```
dotnet build PipingSuite.sln -c Release
```

A Release build also creates the install package `dist/PipingSuite-<version>.zip` (Revit API references come from NuGet and are not bundled).

**Install:** unzip, close Revit, double-click `Install.bat`. It is a per-user install, so no admin rights are needed. It copies the files to `%AppData%\Autodesk\Revit\Addins\2026\` and replaces any previous version. `Uninstall.bat` removes them.
The manifest turns on Revit 2026 add-in dependency isolation (`UseRevitContext=False`), so our CommunityToolkit.Mvvm version can't clash with other add-ins.

## Layout

```
src/PipingSuite/
  App.cs                     IExternalApplication: ribbon tab "Piping Suite"
  Commands/                  IExternalCommand entry points (thin: selection, dialog, report)
  Core/                      Revit helpers shared by all features
    PipingElements.cs        category tests, connector-graph traversal, selection filter
    ParameterHelper.cs       parameter snapshot / lookup (GUID > built-in > name) / safe set
    WindowHelper.cs          modal WPF parented to Revit main window
  Services/                  feature logic, owns transactions
    AssemblyService.cs       run detection (union-find) + assembly creation/naming
    ParameterClipboard.cs    session store between Copy and Paste
  UI/ViewModels, UI/Views    CommunityToolkit.Mvvm view models + XAML windows
```

The rules are:
- Commands contain no business logic.
- Services own their `Transaction` or `TransactionGroup` objects.
- View models have no Revit API calls except reading snapshot records.

## Feature A (implemented)

**Create Assemblies** works on the current selection, or asks you to pick if nothing is selected.
- Elements are clustered by physical connector connectivity. Logical connectors and system owners are ignored.
- An optional **group-by parameter** (e.g. `LINENUMBER`) splits a connected network into separate lines. Elements with no value for that parameter, usually fittings, join the first keyed neighbour they touch, so a blank tee doesn't bridge two lines.
- It can optionally pull in unselected fittings and accessories that touch the selected pipes.
- Elements that are already in an assembly are skipped and reported.
- Each group goes through `AssemblyInstance.AreElementsValidForAssembly`. The pipe naming category is preferred.
- Creation and naming run as two transactions inside one `TransactionGroup`, because the `AssemblyType` can't be renamed until the creating transaction commits. Undo removes everything in one step.
- Names are `<prefix><key>-NNN` and are unique among existing assembly types.

**Copy / Paste Parameters**
- Copy lists the source element's writable, valued parameters in a filterable checklist. Shared and project parameters are checked by default. Built-ins such as offsets, levels and sizes are opt-in, because pasting them moves or resizes the targets. Type and family-swapping parameters are excluded.
- Paste matches each parameter by shared GUID, then by built-in id, then by name. It only writes when both the storage type and the spec (`ForgeTypeId`) match. ElementId values are only pasted into the source document.
- The paste runs in one transaction, and a summary dialog reports applied, not found, read-only and mismatched values.

## Roadmap

**Feature B – view filters**
- `ViewFilterService` reads `(parameter, value)` sets with `ParameterHelper.GetValueByName`, then builds `ParameterFilterElement`s using `ParameterFilterRuleFactory.CreateEqualsRule` and `ElementParameterFilter`. Filters combine with `LogicalAndFilter`.
- Categories come from `ParameterFilterUtilities.GetFilterableCategoriesContainingParameter`.
- Names are deterministic, e.g. `PKG=PKG9 & LINENUMBER=1`. Existing filters are reused, so re-running doesn't duplicate them.
- Filters are applied with `View.AddFilter` and `SetFilterOverrides`, using a stable colour per value hash.

**Feature C – rotation and re-routing**
- Rotate about the connector axis with `ElementTransformUtils.RotateElement`, using 45° snaps or a custom angle.
- Before rotating, record each connector's partner. Afterwards, check angles between the moved connector and the neighbour pipe.
- If the angle is collinear, reconnect directly. If it is 45° or 90°, insert an elbow with `doc.Create.NewElbowFitting` and the routing preferences. Otherwise, re-route via one 45° + 45° pair instead of a chain of loose adapters.
- The connectivity code in `PipingElements` is already shared with Feature A.
