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
3. Click a swatch to change a color (Revit's own color dialog). **Random** gives new distinct colors, **Gradient** blends around the color wheel from the first color to the last, **Rainbow** spreads red→violet across every value (tick **Auto rainbow** to do this automatically for each parameter), and **Select** selects that value's elements.
4. **Apply** overrides projection and cut lines plus solid surface and cut fill, for these elements in this view only. **Clear** removes those overrides.

Color schemes save to and load from `.json` files. The window stays open while you apply, so you can adjust and re-apply.

**Ribbon icons and tooltip previews:** every button has 16 and 32 px icons. Hovering shows a looping MP4 preview of the tool, attached through AdWindows the way Revit's own tools do it. If the video can't play, a still image is shown instead. All of this media is generated by `tools/generate_media.py` (Pillow + ffmpeg), and the output is committed under `src/PipingSuite/Resources/`.

**Insert Olet** (Routing panel):
- **Insert:** click a point on a pipe. The point is projected onto the main's centreline, so the olet is always centred. A temporary branch pipe of the chosen size is drawn at the chosen angle (top, 45° steps, side, bottom) and Revit builds the takeoff from it. The temporary pipe is then deleted, leaving the olet with an open connector.
- **Resize:** click an existing olet. It is rebuilt at the same centre and direction at the new size, and a pipe already connected to it is resized and reconnected.
- **Type:** pick any loaded tap/olet family type, or leave it on Auto. Auto uses the pipe type's own tap, else the first loaded olet, else an `*olet*.rfa` found in your Revit library folders. Routing preferences that don't include a tap (e.g. Junction = Tee) still work: a temporary all-sizes tap rule is added for the takeoff and removed in the same transaction, so the pipe type is unchanged.
- **Keep placing:** reuse the same settings for each click until you press Esc.

**Copy Olet** (Routing panel):
- **Select:** preselect the source olet plus the target pipes, or pick them when prompted.
- **What's copied:** the olet's distance along its main, its rotation around the pipe, its size and its family type are applied to every target.
- **Placement modes:** the same distance from the start, the same distance from the end, or the same position projected onto each pipe (for stacked or parallel runs).
- Targets are oriented like the source main, so "start" means the same end even when a pipe was drawn the other way.
- **Short pipes:** a pipe too short for the olet is lengthened at its free end. If that end is connected, the pipe is skipped and listed in the report.
- Each olet goes through the same olet-family fallback as Insert Olet. The whole batch is one undo.

**ISO Map** (Visualize panel) makes sheet-ready isometrics like enlarged rack ISO maps:
- **Select:** preselect the elements, or pick them when prompted (window selection works). Only the selection is shown in the new view.
- **Name:** `ISO MAP - <package>`, where the package comes from `PACKAGE`, else `BC_PACKAGE`. The most common value in the selection is offered first; you can pick another or type over it. A clash with an existing view name gets ` (2)` etc.
- **Corner:** choose which of the four plan corners (SW, SE, NE, NW) the isometric looks from. The view is then saved and locked at that orientation.
- **Scale:** the view scale (default 1/2" = 1'-0") sets how big the tags are against the piping. Corner and scale are remembered for the session.
- **Graphics:** Fine detail, shaded with edges. The selection is isolated permanently and wrapped in a hidden section box, so later model additions nearby stay out of the map. If the 3D view type applies a view template, its scale, detail level and style are kept and the report says so.
- **Color:** each `BC_LINE SEGMENT` value (editable) gets a color, rainbow red→violet by default or distinct colors. Surfaces are filled through the Color Splasher overrides and edges stay black. Elements with no value keep their normal graphics.
- **Tags:** one `ISO Tag : Pipe Size System Tag` per line segment, on that segment's longest pipe. Tags stack in a column beside the piping, aligned on the side facing it, each with a horizontal shoulder and a leader to the part of its pipe nearest the column. Rows are ordered so leaders don't cross each other.
- **Layout:** a single column on the right, a single column on the left, and a split (branches above the run on one side, below on the other) are each tried at a range of heights. The layout with the squarest overall footprint wins; ties go to the one whose leaders cross the fewest pipes, then the shortest leaders.
- **Square crop:** the crop is fitted around the piping and tags, then made square and centred with a small margin, so maps tile evenly on a sheet. The crop boundary is hidden.
- If the tag type isn't loaded, the view is still made and the report says so. Everything is one undo. Revit then opens the new view and reports the colors and tag count.

**Overall Map** (Visualize panel) makes an overall package key plan:
- **Start:** open the floor plan to base it on, then preselect the piping or window-select it when prompted.
- **View:** the plan is duplicated with its detailing as `ISO MAP - OVERALL - <common package prefix>` (e.g. `PKG 6` from `PKG 6 - A`…`D`). The name is editable.
- **Boxes:** each connected spool with the same `PACKAGE` (else `BC_PACKAGE`) value gets one filled-region box around it, padded 6". Fittings with no value join the run they touch. Boxes follow the plan's own axes, so they stay square on a rotated plan.
- **Color:** one color per package value, rainbow by default or distinct colors. Each color lives on a filled region type named `ISO MAP - <value>`. Change the color there and later runs keep it.
- **Translucency:** boxes get a surface transparency override (default 50%, remembered for the session), so the piping reads through them.
- **Legend:** swatches in the same colors, with the package names inside, stacked to the left of the boxes and top-aligned with them.
- Everything is one undo. Revit then opens the new view and reports the box count.

**Replace Configuration** (Routing panel) swaps wrongly built branches for a correct one, wherever they are:
1. **Pick the wrong branches:** preselect or window-select one or more of them, on any header sections. Pick their pipes, fittings and valves, and their olets too if those should be replaced as well. Each branch's header is found automatically: it's the outside connection to the piping with the biggest connectors, or the pipe the olet sits on.
2. **Pick the correct branch:** select its elements (with its olet, if it has one), or its assembly. It can be loose or already on a header.
3. **Match options:** before searching, choose how alike a branch must be. Your choices are remembered for the session.
   - **Leg length:** each leg's length must be within a tolerance of the picked branch's (default ±1"). Or tick **Any length**, and each match is stretched to fit.
   - **Direction:** legs must run the same compass directions as the picked branch's (default). Or tick **Also branches facing other ways**, for the other side of a header or another header direction.
4. **Matching by route, not by parts:** a branch's route is its straight legs from the header to its far end: direction, pipe size and length. Branches match when they have the same number of legs and sizes and pass the options above.
   - **Allowed to differ:** valves, olets and unions never have to match, since those are what's wrong with the bad branches. Up always stays up.
   - **Where matches are found:** every tee side outlet and olet outlet of the right size and system type in the model is checked.
   - **Report:** the tool says how many were checked and why the rest didn't match (different number of legs, run in other directions, different leg lengths, pipe size).
5. **Review in the model:** every branch to be replaced is selected and zoomed to, inside a live Revit pick. You can pan, zoom and orbit. Shift+click a branch to drop it, Ctrl+click elements of another branch to add it, then Finish.
6. **Numbering (optional):** pick a parameter and step either its last number (`01 → 02`, `09 → 10`) or its last letter (`DETAIL A → DETAIL B`, `Z → AA`) for each placement. Placements are numbered along the direction they spread out most, lowest coordinate first.
7. **Replace:** each old branch is deleted, and the correct one is copied onto that branch's header connection, turned (about vertical) so its legs run the same way.
   - **Stretch to fit:** each leg is then stretched or shortened to the old branch's length for that leg. Lengths are measured between the corners where the centrelines meet, so elbow sizes don't matter. The longest pipe in the leg takes up the difference and everything beyond it moves with it, so valves keep their distance from the header and the far end lands exactly where the old branch ended.
   - **Reconnect:** the branch is then reconnected to the header and to everything the old branch was connected to.
   - **With olets:** a new olet of the correct type is made on the same header pipe at the same spot.
- Each placement is its own transaction inside one undo group, and Revit warnings are suppressed. A placement Revit rejects keeps its old branch, and the report gives the reason.

**Connect Disconnects** (Routing panel) finds and fixes pieces that touch but aren't connected:
1. **Scope:** preselect, or window-select when prompted, the piping to check. Only the selection is checked, so it's quick on any model. Each open end looks at what is right there, selected or not, so a selected branch can still be joined to an unselected header. Olets on or at the selected pipes are always included, even if they weren't picked: an unattached olet isn't connected to anything, so selecting only its header and branch would otherwise never reach it.
2. **What it finds** (within a tolerance, default 1/8"); ends touching nothing are not listed:
   - **Connect:** two open ends that touch, are the same size and face each other. A hairline gap is closed by moving a pipe end onto the other connector first.
   - **Add elbow:** two pipe ends that touch at an angle.
   - **Add transition:** two pipe ends in line but of different sizes.
   - **Add olet:** a pipe end landing on the side of another pipe. An olet is placed there the same way Insert Olet does it: the pipe type's tap if it has one, otherwise a loaded olet family. This works even when the routing preferences only have a tee. The pipe end is then moved onto the olet's outlet and connected.
   - **Re-attach olet:** an olet sitting on a pipe without being attached to it. It's recognised by shape, not by how the family's connectors are set up: its outlet points straight out from a pipe it sits on, within 5/8" of the surface. It's re-made as a real takeoff of the same type, size and direction, and whatever it fed is reconnected.
   - **Check:** touching but needing judgement (fittings at an angle, a size change on a fitting). Listed, not fixed.
3. **Same system type only** (on by default) never joins e.g. supply to return.
4. **Window:** the list stays open while you work. **Zoom** selects an item and zooms to it in Revit. Tick what to fix, then **Connect ticked**. Each fix is its own step in one undo group, and the list rescans the same selection afterwards.

**EZ-ISO Check** (Routing panel) gets line numbers ready for EZ-ISO's Auto Line Nos, which reads `{BC_SYSTEM}-{BC_PACKAGE}-{BC_LINE SEGMENT}`:
1. **Scope:** preselect, or window-select when prompted, the piping to check. A whole package or system works best, so duplicates are found. Olets on the selected pipes are included.
2. **Lines:** each branch from its header tee or olet to its far end is one line, and each header run between branch points is its own line.
   - A tee or olet goes with the side that already carries its number. If neither side does, an olet goes with its branch and a tee with its run.
   - A header numbered straight through its tees, or a vent carrying its pipe's number, stays one line: touching pieces that already share a number are never split.
   - A line's number is the one most of its elements carry. A line with no BC_SYSTEM or BC_PACKAGE borrows them from the line it comes off.
3. **Correct lines are left alone:** a line whose elements all agree and whose number no other line uses is not listed and never changed.
   - **Identical branches may share a number.** One EZ-ISO serves every branch built the same way, so branches sharing a BC_LINE SEGMENT aren't duplicates when they're built alike: the same fitting and valve types, and the same pipe types with lengths within 1/2". Only lines touching just the line they come off count as branches; headers still need their own `##`.
   - **DETAIL values are never overwritten.** A BC_LINE SEGMENT containing `DETAIL` (e.g. `DETAIL A`) is the reference for an EZ-ISO. No fix writes over one, even if the scan proposed it, and a DETAIL line is never renumbered. A line with a DETAIL value on any element takes that as its number.
4. **What it lists:**
   - **Fill values** (ticked): fittings, valves and olets with an empty BC_ value get their line's.
   - **Same as config** (ticked): an unnumbered branch built like numbered ones gets their BC_LINE SEGMENT, a DETAIL value first.
   - **New number** (ticked): a duplicate (a separate line or configuration with the same number) or an unnumbered line gets the next free BC_LINE SEGMENT for its system and package. That is the one after the highest numbered segment anywhere in the model, with the same zero padding (`06 → 07`); DETAIL values don't count. Of the duplicates, the longest keeps its number. Identical branches get one new number between them.
   - **Match DETAIL** (unticked): a numbered branch built like `DETAIL` branches but carrying another number can take the DETAIL value.
   - **Match line** (unticked): a few elements carrying a different number than the rest of their line are set back to the line's number.
   - **Connect** (unticked): a tap or olet sitting on a pipe without being attached, or ends touching without being connected (within 1/8"). These are fixed the way Connect Disconnects does it.
   - **Check:** needs a person, e.g. a line with no BC_SYSTEM anywhere near it, or one DETAIL shared by branches that aren't built alike. Listed, not fixed.
5. **Window:** **Zoom** selects an item and zooms to it in Revit. **Fix ticked** applies the fixes in one undo: values first, then connections. Then it rescans. **Select for EZ-ISO** selects the checked piping, so EZ-ISO's Auto Line Nos can run on it.

**ISO Views** (Visualize panel) makes a tagged, dimensioned isometric for each configuration type in the selection:
1. **Scope:** preselect, or window-select when prompted, the configurations (branches, spools). Each connected piece of the selection is one configuration.
2. **Types:** the tool finds the parameter whose values look like `DETAIL A` (`DETAIL` + letters) and groups configurations by that value.
   - Configurations without a DETAIL value are grouped by how they're built: pipe types with cut lengths to 1/16", plus fitting and valve types, whichever way they face.
   - The dialog lists the types found. It also notes a DETAIL whose configurations are built differently, and different DETAILs that are built alike.
   - You can type another parameter, or clear it to group by build only.
3. **One view per type** (default) shows the first configuration of each type. Untick it for one view per configuration.
   - Views are named `ISO - DETAIL A`. Untyped configurations are named after their EZ-ISO line number, or `ISO - TYPE n`.
4. **Each view** is made the way ISO Map makes its view:
   - It's a locked, shaded isometric showing only that configuration.
   - It's colored by BC_LINE SEGMENT with Color Splasher, using a rainbow gradient by default. Either can be switched off; the choices are remembered for the session.
   - The line segments are tagged with `ISO Tag : Pipe Size System Tag`, in a column with shouldered leaders.
   - The crop is square. Every view uses the same corner and scale, so the views line up on sheets.
5. To dimension a view, open it and run **Auto Dimension** (below).
6. **BOM** (tick **Add a BOM table** and pick its size and side; off by default and remembered for the session): added the same way as **ISO BOM** below, with the crop kept square.
7. One undo removes all the views.

**Auto Dimension** (Visualize panel) dimensions the piping in the active view the way a spool iso does. It uses the selected piping if any is selected, otherwise all piping visible in the view. A dialog asks which linear dimension type to use. The choice is saved as your default (in `%AppData%\PipingSuite`), so it's preselected next time.
1. **One chained dimension per straight leg.** It runs to the centre of every fitting and accessory on the leg (elbows, tees, olets, valves), and to pipe ends that leave the set. Each string reads centre to centre and adds up to the leg.
2. **Centre references:**
   - A fitting is measured to its family's centre reference plane that is square to the leg (Center Left/Right, Front/Back or Elevation).
   - If the family has none, the dimension goes to the centreline of the pipe leaving it at right angles, which runs through the same centre.
   - Pipe ends use the pipe's centreline end.
3. **Placement:** each dimension sits outside its spool.
   - **3D view:** the view is locked first, if needed. Each dimension lies in the plane of its leg that faces the viewer most.
   - **Plan or section:** each dimension lies in the view plane. Legs running towards the viewer have no length to show, so they're skipped.
4. **Report:** the report says how many legs were dimensioned. For each leg Revit refused, it gives the pipe id and Revit's reason. One undo removes everything.

**Auto Tag** (Visualize panel) tags the piping in the active view once per unique value. It uses the selected piping if any is selected, otherwise all piping visible in the view.
1. **Tag and parameter:** pick any loaded pipe tag (`ISO Tag : Pipe Size System Tag` is listed first) and what to group by. The default is the EZ-ISO line number `{BC_SYSTEM}-{BC_PACKAGE}-{BC_LINE SEGMENT}`; any text parameter on the pipes can be picked instead. The dialog previews how many tags it will place and for which values.
2. **One tag per value:** a header `TWCS-PKG6-01` with six identical `TWCS-PKG6-DETAIL A` branches gets two tags. Add a branch whose BC_LINE SEGMENT is `DETAIL B` and it gets three. Piping without a value isn't tagged.
3. **Placement:** each tag goes on its line's longest pipe, stacked in a column with shouldered leaders the way ISO Map does. If the tags land outside the crop, the crop widens to show them. A 3D view is locked first, if needed.
4. **Re-run** to update: tags of the chosen type already on this piping in the view are replaced, so there is still exactly one per value. One undo removes them.

**ISO BOM** (Visualize panel) adds a bill of materials table to the active view, usually an ISO view. It lists the selected piping if any is selected, otherwise all piping visible in the view.
1. **Columns:** ITEM, QTY, SIZE, DESCRIPTION.
   - Pipe is listed by segment (material and schedule) and size, with its total length as the quantity.
   - Fittings and valves are listed by family, type and size, with a count.
   - Items are numbered pipe first, then fittings, then valves and accessories.
2. **In the view:** the table is made of text notes drawn in the view itself, so it goes onto the sheet with the iso. Each column is a bordered note with a bold, underlined header. The notes use text types named `PipingSuite BOM <size>`, which are made when first needed.
3. **Size:** Small (3/32" text), Medium (1/8"), Large (3/16") or Extra large (1/4"). **Fit to the piping** sizes the text so the table is as tall as the piping beside it, or as wide as the piping above or below it (1/16" to 3/8").
4. **Side:** right, below, left or above the piping. The table stays clear of the tags and dimensions.
5. **Crop:** the crop grows to take in the table. It stays square by default, so the view still lines up with other ISO views; untick it to crop tight around the piping and table.
6. **Re-run** to resize or move the table: the view's old BOM is replaced. A 3D view is locked first, if needed. One undo removes it.

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
