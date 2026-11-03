# Modstoke Loader UI Runtime Strategy

Last updated: 2026-11-02

Developer: Alex Cooper

## Decision

Use the game's native Unity UI style as the primary player-facing mod menu path.
Keep Unity IMGUI only as a transitional fallback. Treat Dear ImGui as a future
optional developer/admin tooling backend, not as the default in-game mods menu.

This keeps the mod menu looking like Sons Of The Forest shipped with it, while
still leaving a route for high-density diagnostics and admin tooling later.

## Terminology

- Dear ImGui is the C++ immediate-mode UI project at
  `https://github.com/ocornut/imgui`. It is renderer/backend driven and outputs
  vertex buffers/command lists.
- Unity IMGUI is Unity's `OnGUI` / `GUI.*` immediate-mode system. It is provided
  by `UnityEngine.IMGUIModule.dll`.
- uGUI / Unity UI is the GameObject/component-based runtime UI system used with
  `Canvas`, `Image`, `Button`, `ScrollRect`, and TextMeshPro.
- UI Toolkit is Unity's newer retained UI system using visual elements, USS, and
  UI documents.

The loader's [fallback renderer](../src/Modstoke.Loader/UI/Menus/ModMenuFallback.cs)
uses Unity IMGUI, not Dear ImGui. Source paths below are relative to
[Loader Core](../src/Modstoke.Loader).

## Local Sons UI Facts

The current Sons Of The Forest install exposes these relevant interop assemblies:

- `UnityEngine.UIModule.dll`
- `UnityEngine.UI.dll`
- `UnityEngine.IMGUIModule.dll`
- `UnityEngine.UIElementsModule.dll`
- `Sons.UiElements.dll`
- `Unity.TextMeshPro.dll`

OpenACAI's native mods menu currently uses Unity UI and TextMeshPro, creates a
screen-space `Canvas`, and searches native Sons fonts/materials/sprites so the
menu can reuse the game's existing visual language.

## Backend Assessment

### Native Unity UI / TextMeshPro

Best default for player-facing menus.

Pros:

- Closest to the Sons options/menu look.
- Can reuse native fonts, sprites, materials, tooltip behavior, and panel
  textures.
- Works with the same Unity UI stack the game already ships.
- Does not add native renderer hooks beyond what the loader already needs.
- Easier to integrate with title/pause menu button clones.

Cons:

- More stateful than immediate-mode tooling.
- Requires careful object lifetime management across scene loads.
- More work to build complex table/filter/editor controls.

Use for:

- Main mods menu.
- Mod config screen.
- Credits/about screen.
- Player-visible loader status.

### Unity IMGUI

Good fallback and debug path, poor default player UI.

Pros:

- Already available in the shipped game.
- Very small integration surface.
- Useful when native UI creation fails.

Cons:

- Unity documents IMGUI as mainly intended for programmer tools/debug displays,
  not normal player-facing in-game UI.
- Runs through `OnGUI` and is easy to make visually inconsistent with the game.
- Harder to make feel like Sons' native options screen.

Use for:

- Emergency fallback.
- Temporary diagnostics when the native menu fails.

Do not use for:

- The default mod menu experience.

### Dear ImGui

Potentially excellent for developer/admin tools, but expensive and risky as the
default in-game mod menu.

Pros:

- Mature, actively maintained, and very strong for debug/admin/tooling UIs.
- Good for dense tables, inspectors, logs, live variables, profiler-like panels,
  and short-lived tools.
- MIT-licensed, so it is compatible with the public GPL loader from a licensing
  standpoint.

Cons:

- It is not Unity IMGUI. It is a C++ library that needs platform and renderer
  backends.
- In this IL2CPP Unity game, integration likely means a native plugin plus
  DirectX/Win32 or Unity render-event/input bridging.
- That adds native hook surface area, DLL packaging, signing, crash risk, and
  anti-cheat compatibility risk.
- It will not naturally look like the Sons options menu unless heavily themed.
- C# bindings still require native Dear ImGui/cimgui-style runtime pieces.

Use for:

- Optional admin/developer overlay.
- Private diagnostics and protected-server operator tooling.
- Future high-density client companion panels if native UI becomes too awkward.

Do not use for:

- The main public mods menu until the native Unity UI path is proven impossible.

### UI Toolkit

Possible future renderer, but not the immediate target.

Pros:

- Available in the local interop set.
- Modern retained UI system with styling/layout advantages.
- Unity positions UI Toolkit as an alternative runtime overlay UI system.

Cons:

- Sons' existing menus and the current OpenACAI clone work are closer to
  uGUI/TextMeshPro.
- Runtime UI Toolkit in a modded IL2CPP game may require more asset/document
  setup than the current object-built native menu.
- It does not automatically solve reuse of existing Sons menu sprites/materials.

Use for:

- Future companion-style UI if we need advanced retained layout and can prove it
  is stable in Sons.

## Architecture Direction

Separate menu data/controllers from renderer backends:

- `LoaderConfig` owns config file parsing and saving.
- `UiState` owns surface visibility and tooltip state.
- `Timings` owns timings.
- `ModInventoryState` owns inventory data.
- `MenuState` owns the shared menu inventory, the open page and provider,
  provider snapshots, the selected config document, edit mode, config text,
  drafts, status, save, and revert state. Its `ViewState` keeps the session's
  last page, page history, filter, search text, folded groups and sections,
  and scroll offsets per view.
- `MenuViewBuilder` turns that state and the `MenuRegistry` snapshots into the
  Unity-free view model in `MenuView.cs`, and `MenuText` holds the display text
  and status tones both renderers share.
- Renderers draw that view model and send user actions back through
  `MenuState` intents instead of owning core state or calling providers
  directly.

Renderer layout under `UI/Menus`:

- `MenuController.cs`: Mods screen lifecycle, keybinds, and live controls
  (MOD SETTINGS and MOD ACTIONS) with their resume.
- `ModMenuDispatcher.cs`: native/fallback renderer selection, input focus,
  and the open/hide timing wrapper.
- `ModMenuState.cs`: `MenuState`, the shared controller state and intents.
- `MenuView.cs`, `MenuViewBuilder.cs`, `MenuText.cs`: the shared view model,
  its builder, and shared text.
- `ModMenuRenderer.cs`: primary native Unity UI / TextMeshPro renderer, with
  `MenuRows.cs` (pooled row views), `MenuScroll.cs` (scroll panes), and
  `Sons/UI/MenuTheme.cs` (captured Sons fonts, sprites, and colours).
- `ModMenuFallback.cs`: Unity IMGUI emergency fallback.
- Future `OpenACAIDearImGuiOverlay` or similar: optional developer/admin
  overlay only, behind a capability flag and separate package/native DLL.
- Future `OpenACAIUiToolkitRenderer`: experiment only if UI Toolkit proves more
  stable or maintainable in Sons.

## Mods Menu Structure

Both renderers show the same view model.

- **Nav:** Home, then the groups PINNED, MODS, MOD PAGES, LOADER,
  DIAGNOSTICS (folded by default), and ABOUT. A provider page with an owning
  mod is nested under that mod in MODS. A page whose mod is filtered out moves
  to MOD PAGES. Groups fold, and search opens every group with a match.
- **Filter chips:** All, Modstoke, RedLoader, BepInEx, Libraries, and Issues,
  with counts. Chip labels come from the built-in tab providers.
- **Pages:** Home (summary, issues, pinned pages, last visited), a mod page
  (issue, settings from its config mirrors, config file, live controls, its
  pages, details), a provider page (settings, forms, content sections,
  actions), and About (credits and licensing). Titled sections fold from
  their header.
- **Config file:** opens from the mod page. The native editor has FRIENDLY and
  TEXT modes and asks before unsaved edits are dropped. The fallback edits the
  raw text only, with SAVE and REVERT.
- **Session memory:** the last page, filter, search, folds, and scroll
  position carry over between opens. BACK walks the page history and closes
  the menu from Home.

The native renderer uses two panes: nav on the left and the page on the
right, under a header with the breadcrumb and a status strip. The IMGUI
fallback draws one scrolled column, the nav list followed by the open page,
scaled from a 1080-line layout to the screen height. It rebuilds the view
only when the state changes or on the 0.5 s provider poll, not on every
`OnGUI` pass.

## Next UI Refactor Slices

1. Done: `UI/Menus/ModMenuDispatcher.cs` owns native/fallback renderer
   selection for show/hide, falls back around native renderer dispatch
   failures, and records menu timing/state refreshes.
2. Done: `UI/Menus/ModMenuState.cs` owns the shared menu state for both
   renderers, and `MenuViewBuilder` builds the view model they both draw.
3. Done: `MenuController` lives in `UI/Menus/MenuController.cs`, so
   `UI/Menus/ModMenuFallback.cs` contains only fallback rendering.
4. Keep native Unity UI as the default player-facing renderer.
5. Keep Unity IMGUI fallback available but minimize its visual feature work.
6. Prototype Dear ImGui only as a separate optional diagnostic/admin overlay
   after the loader's native hook and signing path is stable.

## Sources

- Dear ImGui upstream: `https://github.com/ocornut/imgui`
- Dear ImGui FAQ: `https://github.com/ocornut/imgui/blob/master/docs/FAQ.md`
- Unity IMGUI introduction: `https://docs.unity3d.com/Manual/GUIScriptingGuide.html`
- Unity IMGUI basics: `https://docs.unity3d.com/Manual/gui-Basics.html`
- Unity UI systems comparison: `https://docs.unity3d.com/Manual/UI-system-compare.html`
- Unity uGUI package notes: `https://docs.unity3d.com/Manual/com.unity.ugui.html`
- Unity UI Toolkit notes: `https://docs.unity3d.com/Manual/UIElements.html`

