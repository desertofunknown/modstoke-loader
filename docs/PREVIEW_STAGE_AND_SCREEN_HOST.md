# Preview stage and screen host

Modstoke SDK by OpenACAI has two client-side building blocks for character
creators and other full-screen editors:

- `Modstoke.Sons.PreviewStage` renders an isolated copy of character parts to a
  texture.
- `Modstoke.Loader.ScreenHost` is a full-screen themed screen that looks like the
  mods menu.

They don't depend on each other. `ScreenHostSession.AttachStage` connects them:
it shows the stage in the screen's content area and passes it pointer input.

Neither is networked or saved. Both refuse to run on a dedicated server. Source
lives in [Sons/UI/Stage](../src/Modstoke.Loader/Sons/UI/Stage) and
[UI/Screens](../src/Modstoke.Loader/UI/Screens).

## Game-thread rules

Call every member of both APIs on Unity's main thread, the thread that
initialised the Loader.

- `Try*` methods called from any other thread return false, with a status that
  names the game thread.
- Void methods called from another thread do nothing.
- `Dispose` from another thread is logged and runs on the next Loader tick
  instead.

Events, callbacks and row handlers always run on the main thread. Keep them
short:

- `TextureChanged`, `BuildCompleted`, `Opened`, `Closed`, `Changed` and the
  `TryOpenWhenReady` completion.
- The handlers on panel rows. A slider's `live` handler runs on every move.

An exception in a handler or callback is logged and does not stop the stage or
the screen. A failing row handler also shows its message in the screen's status
line.

Only one stage and one screen exist per process. A second `TryCreate` fails with
a status naming the current owner. A second `TryOpen` from another owner fails
the same way; one from the same owner replaces that owner's screen. Both stay
client-only: run them from client code, never from a server callback.

## Preview stage

### Creating and closing

`PreviewStage.TryCreate(options, out stage, out status)` needs an `OwnerId`.

- It fails at the title screen unless `AllowTitleScene` is set.
- It fails when HDRP isn't the active pipeline.
- `PreviewStage.Current` holds a snapshot of the live stage, or null.

The stage closes itself:

- when the world is exited, unless `AllowTitleScene` is set;
- when the scene it was created in unloads;
- when the Loader shuts down;
- when a screen it is attached to closes, if it was attached with
  `disposeStageOnClose`.

Otherwise dispose it with the UI that created it. `Dispose` is idempotent.

### What the stage never does

The stage only ever reads the live player. It never calls `ApplyRace`,
`WearClothingList`, `EquipToSlot` or `GetRandomRace`, and it never changes the
player's race, clothing, armour, blend shapes or materials.

A creator keeps its own stored look and applies the final choice through its own
commit path. For the Modstoke Client, that is the stored look plus `ApplyRace`.
Edits made on the stage stay on the stage's copies.

The stage copies parts by rebuilding them, never with `Instantiate`. Each copy is
a new GameObject that holds only a renderer, so no game script, Bolt entity or
save component can exist on it. Copies share meshes and materials with the game:

- Edit a copy's material only through `PreviewPart.GetOwnedMaterial`. The stage
  owns that material and destroys it.
- Never write to `sharedMaterials`.
- After editing a material's values directly, call `MarkDirty()` so the stage
  renders again.

The stage is never parented to the player. It sits at a far-off origin with a
short far clip. Isolation from the world comes from four things:

- a per-camera gate that hides stage renderers from every camera except the
  stage camera;
- the same gate turning the world's directional lights off for the stage
  camera's cull only, since distance and light layers don't stop a sun and HDRP
  ignores a light's culling mask;
- the stage's own layer;
- light layers, when the HDRP asset enables them.

The "Preview stage opened" log line lists each directional light's light layers
next to the stage's, and marks the ones that would reach the stage layer without
the gate.

### Content

- `TryBuild` assembles parts from Addressables keys, asset references or prefabs.
  The build runs in the background. Old parts stay visible until every new part
  is ready, then the whole set is swapped in at once.
- `TryCaptureLocalPlayer` copies the local player's visible renderers onto a copy
  of the live rig. That covers a custom body, the race head and arms, clothing
  and armour.
  - It refuses with `look-settling` while the game is still loading the look.
  - Bones are matched by their path from Hips, so extra leaf bones are copied
    too.
- `Sync()` is cheap enough to call every frame while a creator is open. It
  recopies enabled flags, blend weights and shared materials. When a source
  renderer appears, disappears or changes mesh, it starts a new capture and
  returns false.
- `BuildCompleted` reports every build: the ones that succeeded, failed or were
  superseded. Its `RebuiltPartIds` lists the parts whose edits need to be
  applied again.

The player rig scales Hips by 0.4, so focus, bounds and camera distances are in
world metres. By default the copy rests in the A-pose taken from the meshes' bind
poses. `PreviewCaptureOptions.LivePose` keeps the pose the player was in when
captured instead.

### Camera and rendering

- `Orbit`, `Zoom`, `SetFocus`, `SetFocusBone` and `ResetView` move a damped
  orbit camera.
- Frames render only on demand, at no more than `ContinuousMaxHz`, and only while
  `Visible` is true and the game window has focus. An idle stage costs nothing.
- `Texture` is replaced when the viewport size changes. Listen to
  `TextureChanged` rather than caching it.
- `SetViewportPixels` sets the texture size. It follows the viewport after a
  short debounce and never grows past the screen size.
- The stage camera copies the frame settings and volume layers of the game's
  InventoryCamera, or of the main camera when there is no InventoryCamera. The
  lit shader mode and the material features therefore match the game's camera,
  and so do the shader variants the build kept. The stage turns off only fog,
  volumetrics, clouds, reflection probes, screen-space effects, custom passes,
  motion vectors and lens effects.
- `Snapshot()` reports `FrameSource`. After the first frame it also reports
  `LitShaderMode` and `Exposure` as HDRP rendered them.

### Lighting

The stage is lit like a softly lit studio, in physical units, whatever the
world's time of day or weather. Everything below lives in a volume local to the
stage origin, where only the stage camera anchors its volumes, so no other
camera's exposure, sky or ambient light changes.

| Setting | Default | Why |
|---|---|---|
| Exposure | Fixed, EV100 10 | A lit studio or a bright overcast day. |
| Key | 4000 lx at the subject, front left and above | Main light, with soft shadows from a 25 cm radius source. |
| Fill | 1600 lx, front right, no shadows | Key to fill of 2.5:1. |
| Rim | 2000 lx, behind and above, no shadows | Separates the silhouette from the background. |
| Ambient | 800 lx from a uniform white sky | Fills the side away from the key. |
| Tonemapping | Neutral | Keeps skin hue, and reaches white only at an exposed value of 5.3, far above lit skin at about 1. |

- The rig is three spot lights that follow the pivot and the camera's yaw. Their
  cones are wide enough that the subject stays in the full-intensity core from
  head to feet.
- The sky is a Gradient sky with the same white at the top, middle and bottom,
  in multiplier mode, so its luminance is ambient / π nits. The visual
  environment's ambient mode is Dynamic, so the stage camera's ambient probe and
  sky reflection come from this sky, never from the world's or the baked static
  sky. Indirect diffuse and reflection multipliers are 1. Stage copies have
  light probes turned off, so they take this ambient rather than baked probes.
- HDRP reads a new sky's ambient probe back from the GPU a few frames after it
  changes, so the stage renders a few extra frames when it opens and when the
  ambient changes. They run back to back, whatever `ContinuousMaxHz` is, and a
  readback waits for them.
- The main camera has HDRP tonemapping off, and the stage turns off custom
  post-processes, so the stage's Neutral tonemapper is the only one the copy
  sees. Post exposure is pinned to 0; the rest of the grading still comes from
  the game's volumes.
- The background colour is cleared after exposure, so it stays the same dark
  grey at any EV. Only the tonemapper darkens it a little.
- `stage/light` tunes all of these live, see Diagnostics.

## Screen host

### Opening

`ScreenHost.TryOpen(request, out session, out status)` returns a session that is
either `Open` or `Pending`.

- A Pending session waits a few frames, at most 10, while another UI hands over
  input. When it opens, it raises `Opened`.
- A session that opens inside `TryOpen` is already `Open` when the call returns,
  so `Opened` has nothing to report for it.
- `TryOpen` fails at once in these cases:
  - outside the world, unless `AllowTitleScene` is set;
  - while a scene is loading;
  - without an active `EventSystem`;
  - while the mods menu is showing, unless the origin is `ModsMenu`;
  - when the input lease can't be had.

`ScreenHost.IsOpen` is true for both states. `ScreenHost.Snapshot()` and the
`Changed` event report the owner, state, origin and theme tier.

The origin decides how input is taken over:

| Origin | How the screen gets input |
|---|---|
| `ModsMenu` | When opened from a mods menu command, the menu hides on the next tick. The screen takes the lease once the menu has released it. The command just returns success as usual. When the screen closes, the menu comes back at the same page if `ResumeModsMenu` is set, unless the reason was `Replaced`, `SceneUnloaded` or `WorldExited`. A screen that replaces it for the same owner takes over that return. A config file with unsaved edits keeps the menu open, and the screen gives up. |
| `Tablet` | The screen's UI surface is published while it is Pending. The tablet closes itself and releases its input, then the screen takes it. |
| `Lifecycle`, `OtherMod` | The lease is taken at once, or the open fails. |

While a screen is open, the mods menu refuses to show, including from the pause
menu's MODS button. The console (F5) and other SDK windows refuse because the
lease is held.

### First join

`TryOpenWhenReady(request, ready, timeoutSeconds, completed, out status)` waits
until all of these are true:

- the player is in the world;
- no scene is loading;
- the pause menu is closed;
- the mods menu is closed;
- no SDK window holds input;
- the local player is live;
- `ready()` returns true.

It then calls `TryOpen` once and passes the session, or the reason it failed, to
`completed`.

- With `AllowTitleScene`, it doesn't wait for the world or the player.
- It gives up on timeout, or if the world is exited.
- Only one request can wait at a time.
- Whether this is a first join is for the caller to decide.

The game isn't paused while a screen is open in the world, and multiplayer can't
be paused at all. Open a creator only when it's safe for the player to stand
still.

### Input and Back

While a screen is open, it holds:

- the SDK focus lease for its owner, with `Back` and any `AllowedKeybindIds`
  still working;
- the game's Console action map;
- the hardware cursor;
- a UI surface with the ID `ScreenHost.SurfacePrefix` + owner ID.

The Console action map keeps Esc from opening the pause menu. If another UI
drops the map, the screen applies it again once. If the map is still lost, or
the event system or scene changes, the screen closes with `InputLost` or
`SceneUnloaded`.

Back, from the key or the BACK button, runs in this order:

1. If a text field in the screen has focus, it leaves the field.
2. Otherwise it asks `TryBack`, the caller's own back stack.
3. If `TryBack` doesn't handle it, it asks `CanClose`, the unsaved-changes
   guard.
4. If `CanClose` allows it, the screen closes with `Back`.

`RequestClose()` asks `CanClose` first; `Dispose()` closes without asking. If
`CanClose` throws, the screen closes anyway, so a broken guard can't trap the
player.

### Panel rows

`SetSections` fills the left pane with the mods menu's own row views:

- `Header`, which also starts a new section heading;
- `Slider`;
- `Choice`;
- `Toggle`;
- `Button`;
- `Swatches`.

Rows that keep the same ID and kind keep their view, focus and any drag in
progress.

- A slider calls `live` on every move and `commit` once it has been still for a
  moment, the pointer lets go or the screen closes.
- `TrySetValue` updates one row in place without calling its handler.
- `SetStatus` and `SetHints` write the footer.

Arrow keys and the gamepad move between rows and the BACK button, and never
reach the pause menu underneath. The pane's scroll position is kept per owner.

### Stage view

`AttachStage(stage, disposeStageOnClose)` shows the stage texture in
`ContentRect`. It sets the stage's viewport size from the area's pixel size, and
sets `stage.Visible` from whether the screen is open. Pointer input over the
area goes to the stage:

| Input | Effect |
|---|---|
| Left drag | Orbits, 0.25° of yaw and 0.2° of pitch per pixel. |
| Wheel | Zooms. |
| Double-click | Resets the view. |
| Middle drag | Raises or lowers the pivot, up to 0.3 of the framed radius. |

The wheel over the stage never scrolls the pane. If the stage closes itself, the
view lets go of it and the screen stays open.

### Closing

A screen closes in this order:

1. It calls `commit` for a slider that is still waiting for it.
2. It raises `Closed(reason)`, while the panel and stage still exist.
3. It detaches the stage, and disposes it if that was requested.
4. It hides the canvas, which also turns off the input recipe.
5. It releases the lease.
6. It hides the UI surface.
7. It restores the cursor, if nothing else has taken it.
8. It queues the mods menu to come back, if that applies.

The possible reasons are `Requested`, `Back`, `SceneUnloaded`, `WorldExited`,
`InputLost`, `Replaced` and `Failed`.

## Diagnostics

The Loader registers a built-in `stage` command group. In the console and the
mods menu it offers:

| Command | What it does |
|---|---|
| `stage/probe` | Writes the layers, HDRP settings, lights, cameras and the local player's renderers and rig to the Loader log and `Modstoke/UserData/PreviewStage`. |
| `stage/test` | Renders a test cube to a PNG in the same folder. |
| `stage/test red` | Does the same with a red local light nearby on the main directional light's light layers, to check that light layers keep such a light off the stage. With light layers off it always shows. |
| `stage/test sun` | Does the same with a red directional light on the stage's light layer, to check that the gate keeps directional lights off the stage. With light layers off the world is lit by it too until the image is written. |
| `stage/test capture` | Renders the captured player in three framings. |
| `stage/light` | Prints the open stage's exposure, rig and ambient in the form the command takes. |
| `stage/light ev 10 key 4000 fill 1600 rim 2000 ambient 800` | Changes any subset of them on the open stage at once and logs the result. |

`stage/light` takes EV100 for `ev` and lux at the subject for `key`, `fill`,
`rim` and `ambient`, as name and value pairs in any order. EV is clamped to
2-16 and lux to 0-120000. It refuses when no stage is open or when a value isn't
a number, and changes nothing until every pair is valid. Changes last until the
stage closes, change only that stage, and are never saved; the logged line can
be pasted back to repeat them. While a screen holds input the in-game console
can't open, so run it from the desktop or server console.

Files written by the probe and tests contain no Steam IDs.

## Not yet supported

These aren't implemented yet:

- the part catalogue, and stage content at the title screen (the per-build key
  cache);
- the opt-in volume-override tier;
- the stage-camera early exit for remote-player presentation.

`AllowTitleScene` already works for the screen host, and for a stage given
prefab parts that carry their own bones.

Several behaviours still need checking in a live game:

- the lighting look, and SMAA;
- isolation from the sun and moon;
- Esc over an open pause menu;
- the stage frame cost with remote players.

Treat them as provisional until those checks are recorded.
