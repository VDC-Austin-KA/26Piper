# Piping Productivity Suite (Revit 2026)

Revit 2026 add-in (.NET 8, WPF/MVVM) for piping assemblies, parameter management, view filters and fitting manipulation.

## Build & install

```
dotnet build PipingSuite.sln -c Release
```

**Installer (recommended):** `dotnet publish installer/Setup -c Release` builds the add-in and creates `dist/PipingSuite-Setup-<version>.exe`. This single exe has the add-in built into it. Close Revit and double-click the exe; there is nothing to extract and no admin rights are needed. It installs to `%AppData%\Autodesk\Revit\Addins\2026\` for the current user. Run it again to update or uninstall. It uses the .NET 8 runtime that Revit 2026 already installs.

**Zip (fallback):** a Release build also creates `dist/PipingSuite-<version>.zip`. Extract it, then run `Install.bat`.

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

**Copy/Paste fixes (v1.1):** Paste never writes back onto the source element; it used to re-use the selection, which after Copy was still the source. Checkboxes now toggle with a single click. Comments and Mark are ticked by default. A GUID or built-in match no longer fails the spec check. The report lists each parameter that failed, with the reason.

**Multi-parameter assemblies:** tick any number of group-by parameters, e.g. `PKG` + `LINENUMBER`. Elements group only when every value matches. Names look like `PA-PKG9-1-001`.

**Combine Parameters:** pick two project or shared parameters, a separator, and a new name.
- It creates (or reuses) a text instance shared parameter on the categories both parameters share, then fills it with values like `PKG9-1`.
- The definition goes into your active shared parameter file under group `PipingSuite`. If none is set, it uses `%AppData%\PipingSuite\SharedParameters.txt`, and your setting is restored afterwards.
- Values are a snapshot: re-run to refresh them.

**Align & Connect** (Routing panel): pick the element to connect, then what it connects to.
- **End to end:** the mover slides sideways onto the target's axis and its end stretches to meet. It is then joined directly, or with a union (equal pipes), a transition (different sizes) or an elbow (angled pipe-to-pipe).
- **Into a main:** if the open end is nearer the body of a pipe than to any open end, the main is split at that point and a tee is inserted.
- **Which element moves:** by default the smaller pipe moves; fittings and the main never do. You can force the first or second picked element instead.
- **Options:** the X/Y/Z checkboxes limit which project axes are corrected. A maximum-move tolerance (default 6") refuses large corrections.
- Everything runs in one transaction, so a failed connection leaves the model unchanged.

**Color Splasher** (Visualize panel) is an independent reimplementation of the workflow from BIM One's MIT-licensed [Color Splasher](https://github.com/bimone/addins-colorsplasher):
1. Tick the categories visible in the active view, then pick any instance or type parameter.
2. Each distinct value gets a distinct color, and elements without a value are grouped as `(no value)`.
3. Click a swatch to change a color (Revit's own color dialog). **Random** gives new colors, **Gradient** blends from the first color to the last, and **Select** selects that value's elements.
4. **Apply** overrides projection and cut lines plus solid surface and cut fill, for these elements in this view only. **Clear** removes those overrides.

Color schemes save to and load from `.json` files. The window stays open while you apply, so you can adjust and re-apply.

**Ribbon icons and tooltip previews:** every button has 16 and 32 px icons. Hovering shows a looping MP4 preview of the tool, attached through AdWindows the way Revit's own tools do it. If the video can't play, a still image is shown instead. All of this media is generated by `tools/generate_media.py` (Pillow + ffmpeg), and the output is committed under `src/PipingSuite/Resources/`.

**Insert Olet** (Routing panel):
- **Insert:** click a point on a pipe. The point is projected onto the main's centreline, so the olet is always centred. A temporary branch pipe of the chosen size is drawn at the chosen angle (top, 45° steps, side, bottom) and Revit builds the takeoff from it. The temporary pipe is then deleted, leaving the olet with an open connector.
- **Resize:** click an existing olet. It is rebuilt at the same centre and direction at the new size, and a pipe already connected to it is resized and reconnected.
- **Type:** the olet family type comes from the pipe type's routing preferences, or from any loaded tap/spud family you pick.
- **Keep placing:** reuse the same settings for each click until you press Esc.

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
