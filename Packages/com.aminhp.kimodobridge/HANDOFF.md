# Kimodo ↔ Unity Bridge — Project Handoff

> **Purpose:** single reference to continue this project in a new chat. Point the assistant here first
> (“read `Packages/com.aminhp.kimodobridge/HANDOFF.md`”). Persistent memory also exists at
> `C:\Users\mahag\.claude\projects\h--kimodo\memory\` (`kimodo-unity-bridge.md` — detailed running log,
> `kimodo-setup.md` — how Kimodo is installed).

Last updated: 2026-07-29. Owner/brand: **AminHP** (independent wrapper — *not* affiliated with NVIDIA).
Published: **https://github.com/Amin-HP/Kimodo-Unity** (branch `main`, currently private). The repo bundles a
copy of the Python server under `Server/` — **re-sync it from the live `H:\kimodo\kimodo\kimodo_bridge\server.py`
before each push** (see the `kimodo-github-repo` memory).

---

## 1. What this is

A real-time, interactive add-on that connects **NVIDIA Kimodo** (text→human-motion diffusion model) to
**Unity 6**. Generate motion from a text prompt, preview it on a Unity **Humanoid** character, author
**constraints** (root path, hand/foot targets, whole-body poses), and **bake** to an `AnimationClip`.
Uses the **SOMA** model by default (77 joints); model is user-selectable.

**Status: fully working** end-to-end — connect → generate → preview → constraints (waypoints, effectors,
pose) → bake. All confirmed by the user in-editor.

---

## 2. How to run

1. **Server:** `cd H:\kimodo\kimodo; .\run_bridge.ps1` (add `-Preload soma` to load at startup).
   Activates the venv, sets `TEXT_ENCODER_DEVICE=cpu` (8 GB VRAM), serves `http://127.0.0.1:8765`.
   Generation is CUDA + CPU text-encoder → ~tens of seconds per generate. **Restart the server after any
   `server.py` change.**
2. **Unity:** open `H:\kimodo\Kimodo Unity` (Unity 6000.4.4f1, URP). Component workflow:
   - `GameObject ▸ Kimodo ▸ Bridge Manager` — creates the **KimodoBridge** manager.
   - Select a **Humanoid** character (Rig → Humanoid; tested with a stock Mixamo model) →
     `GameObject ▸ Kimodo ▸ Set Up Selected Character` — adds **KimodoGenerator** + **KimodoEffectors**,
     wires the bridge.
   - On KimodoBridge: **Connect** (preloads the model). On KimodoGenerator: prompt → **Generate** →
     preview (play/scrub) → **Bake to AnimationClip**.
   - Add constraint components as needed (`GameObject ▸ Kimodo` or Add Component): **KimodoWaypoints**,
     **KimodoEffectors**, **KimodoPoseConstraints** — each draws Scene gizmos and is gathered on Generate.

---

## 3. Architecture

```
Unity Editor (C#) ──HTTP/JSON──► Bridge server (FastAPI) ──► Kimodo model (load_model + model(...))
  components + gizmos              H:\kimodo\kimodo\kimodo_bridge     diffusion cuda:0, text encoder CPU
  live retarget preview / bake     port 8765
```

- Server returns **pure Kimodo coordinates** (right-handed, Y-up, +Z forward, metres); **Unity converts**.
- Model stays resident (cached); constraints are computed **Unity-side** into Kimodo coords and sent with `/generate`.

---

## 4. File map

### Python bridge server — `H:\kimodo\kimodo\kimodo_bridge\`
- `server.py` — FastAPI app. `GET /health`, `GET /models`, `GET /skeleton`, `POST /load_model`, `POST /generate`.
  `GET /skeleton?model=` returns the rest bone list (no motion) so the client can author + draw constraints BEFORE
  the first generate. `_get_or_load_model` caches by resolved short-key (MUST reuse — reload re-loads the ~8B encoder).
  Constraint machinery (see §7). Launcher `..\run_bridge.ps1`. `__main__.py`, `__init__.py`.
  **This is the live server.** A snapshot lives in the GitHub repo under `Server/` — keep it in sync (see header).

### Unity package — `H:\kimodo\Kimodo Unity\Packages\com.aminhp.kimodobridge\`
Namespaces **`AminHP.KimodoBridge`** (runtime) / **`AminHP.KimodoBridge.Editor`**. Asmdefs
`AminHP.KimodoBridge.Runtime` / `.Editor`. License **Apache-2.0**; `THIRD-PARTY-NOTICES.md` credits Kimodo.

**Runtime**
- `KimodoMotionData.cs` — DTOs (JsonUtility, flat float arrays): `KimodoMotion/Clip/Bone`, `KimodoModelInfo/List`,
  `KimodoHealth`, `KimodoGenerateRequest`, `KimodoConstraint` (fields for all constraint kinds), `KimodoLoadResult`.
- `KimodoClient.cs` — `UnityWebRequest` client (Health/Models/LoadModel/Generate), callback-based, editor+play.
- `KimodoCoords.cs` — **the** conversion: `pos (x,y,z)→(-x,y,z)`; `quat kimodo(w,x,y,z)→Unity(x,-y,-z,w)`.
- `KimodoSomaHumanoid.cs` — SOMA→`HumanBodyBones` map; `BuildSourceRig` (Kimodo skeleton → Humanoid Avatar via
  `AvatarBuilder`); `ApplyFrame`; `RootAt`.
- `KimodoPlayer.cs` — retarget engine (source `HumanPoseHandler` → target). `AutoCalibrateRootMotion` (§6),
  `RootMotionScale`, `SampleFrame/Time`.
- `KimodoFK.cs` — Kimodo-space FK (`GlobalPositions`, `GlobalPose` incl. rotations), `BoneIndex`.
- `KimodoIK.cs` — analytic two-bone IK (leg/arm): bends mid joint, clamps to reach, returns new local rots.
- `KimodoRootMap.cs` — affine world↔Kimodo (measured from preview): `Compute`, `WorldToKimodo`, `KimodoToWorld`,
  `WorldToKimodoQuatWXYZ`. Proven correct (waypoints).
- `KimodoRootBake.cs` — `enum KimodoRootBake { InPlace, Travel }`.
- `KimodoTimeline.cs` — the timeline **asset** (`ScriptableObject`, Assets ▸ Create ▸ Kimodo ▸ Timeline): ordered
  prompt `Segment`s (`prompt` + `seconds`), `BuildPrompt()`/`BuildDuration()` (the multi-prompt encoding),
  frame math (`StartFrame`/`FrameCountOf`/`TotalFrames`/`SegmentAtFrame`), and a saved `Take` (see §7c).
- `KimodoMotionPlayer.cs` — runtime component to play a `KimodoMotion` from code.
- `KimodoGhost.shader` — `"Kimodo/GhostMesh"`, URP transparent fresnel used by the pose ghost-mesh preview.
- **Components:** `KimodoBridge.cs` (manager), `KimodoGenerator.cs` (per-character generate+preview+bake state),
  `KimodoEffectors.cs` (hand/foot IK targets), `KimodoWaypoints.cs` (root path + facing),
  `KimodoPoseConstraints.cs` (whole-body pose keys).

**Editor**
- `KimodoBaker.cs` — bake `KimodoMotion` → Humanoid `AnimationClip` (RootT/RootQ + muscle curves).
- `KimodoBridgeEditor.cs`, `KimodoGeneratorEditor.cs`, `KimodoEffectorsEditor.cs`, `KimodoWaypointsEditor.cs`,
  `KimodoPoseConstraintsEditor.cs` — custom inspectors + Scene gizmos.
- `KimodoTimelineWindow.cs` — the sequencer window (`Window ▸ Kimodo ▸ Timeline`), see §7c.
- `KimodoPoseGhosts.cs` — manages the transparent ghost-mesh clones for the pose-constraint editor (see §7).
- `KimodoMenu.cs` — the `GameObject ▸ Kimodo ▸ …` menu items.
  (The legacy all-in-one `KimodoBridgeWindow.cs` was **removed** in the v0.1.0 cleanup — component workflow only.)

### Example (outside the package) — `H:\kimodo\Kimodo Unity\Assets\`
- `KimodoCircleWaypoints.cs` — a user-side script (namespace-less, `using AminHP.KimodoBridge`) showing how to drive
  the plugin from your own code: builds a **circular loop** of waypoints (center/radius/resolution, closeLoop makes
  first==last exactly, `facingOffsetDeg` e.g. ±90 to look at the centre, `startAtCharacter`), can **duplicate the
  first pose key** onto every waypoint frame (root moved onto the circle), and `BuildAndGenerate()` runs the two-pass
  (baseline generate → build → generate) since waypoints need an existing motion for the mapping. Context-menu:
  Build Circle / Duplicate Pose To Waypoints / Build Circle + Generate.

---

## 5. Data contract

**`/generate` response** — shared skeleton + clips (Kimodo coords, metres):
```
{ model, skeletonName:"somaskel77", coordSystem:"kimodo_rh_yup_zfwd_meters", quatOrder:"wxyz",
  fps:30, frameCount:T, jointCount:77, rootIndex:0, footContactChannels,
  bones:[{name,parent,ox,oy,oz}],                    // rest offsets (T-pose)
  clips:[{ rootPositions:[T*3], localQuats:[T*J*4 wxyz], footContacts:[T*C], posedJoints?:[T*J*3] }] }
```
**`/generate` request** — `{ prompt, model, duration, num_samples, diffusion_steps, num_transition_frames,
seed(-1=random), postprocess, include_positions, cfg_weight:[text,constraint], constraints:[...] }`.

**`KimodoConstraint`** (Unity→server, Kimodo coords) carries whatever a kind needs:
- `type`: `left-hand|right-hand|left-foot|right-foot|fullbody|root2d`.
- `frameIndices[N]`.
- pose-based (fullbody, IK'd effectors): `localQuats[N*J*4 wxyz]`, `rootPositions[N*3]`.
- `activeJoints[]` (fullbody only): active BODY-joint names → server pins only those (partial pose). Omit = all.
- direct effector target (unused now — see IK below): `effectorPos[N*3]`, `effectorRot[N*4]`, `effectorRootXZ[N*2]`, `constrainRot`.
- root path: `rootPath2d[N*2 xz]`, optional `rootHeading2d[N*2 cos,sin]`.
- (legacy pin offsets: `targetOffsets`, `jointNames`.)

---

## 6. Key technical learnings (do NOT re-derive)

- **Coordinate conversion:** Kimodo right-handed (char right = −X), Unity left-handed (right = +X). Map = negate X.
  `pos→(-x,y,z)`; `quat(w,x,y,z)→(w,x,-y,-z)` = Unity `Quaternion(x,-y,-z,w)`. Verified; preserves left/right.
- **Retarget:** build a source Humanoid Avatar from SOMA's T-pose, read `HumanPose`, apply to any target Humanoid.
- **Root-motion scale (critical):** root travel via `HumanPose.bodyPosition` has a hidden per-character factor
  (Mixamo char ≈ **−103.7** = 1.037 humanScale × 100 cm/m, negative=reversed). `AutoCalibrateRootMotion` measures
  hip travel vs Kimodo's and solves the signed `RootMotionScale` (fixes vertical too, factor is uniform). On by default.
  Anything that poses the character via HumanPose MUST scale bodyPosition by this (else it flies off).
- **Bake modes:** only two native humanoid behaviours, set by the Animator's **Apply Root Motion** checkbox, not
  clip settings. Do **not** use Loop-Pose/Bake-Into-Pose (warps muscles). `InPlace` = strip horizontal RootT;
  `Travel` (default) = keep RootT (ON=travels, OFF=in place).
- **Constraint representation is ROOT-RELATIVE:** the model stores `local = global − (root_x,0,root_z)`, so any
  constraint on a global joint position **requires `smooth_root_2d`** (else `ValueError: smooth root must also be
  constrained`). Constraints are **soft** diffusion guidance; strength = `cfg_weight[1]` (exposed as
  `KimodoGenerator.constraintWeight`, default 3).
- **Device gotcha:** build constraints with `load_constraints_lst(dicts, skeleton)` **without `device=`** — Kimodo's
  `crop_move` rebuilds `pos_indices` on CPU, so moving `frame_indices` to cuda causes a device mismatch in
  `create_pairs`. Keep index tensors on CPU (data goes on cuda via `from_dict`).
- **Perf:** `_get_or_load_model` must hit the cache; reloading re-loads the ~8B text encoder each call.
- **The affine `KimodoRootMap`** cleanly inverts world↔Kimodo for the **root/positions** (proven by waypoints), but
  a hand/foot's *height* is set by leg/arm length + muscle retarget, so it is **not** a clean affine → effector Y
  needs a measured per-effector vertical fit (see `KimodoEffectors.FitVerticalY`).

---

## 7. Constraint system (current, all working)

Server: `KimodoGenerator.Generate()` gathers constraints from `KimodoEffectors` + `KimodoWaypoints` +
`KimodoPoseConstraints` and sends them. `server.py`:
- `_build_constraint_dicts()` routes by type: **root2d** → `smooth_root_2d` (+ optional `global_root_heading` from
  `rootHeading2d`); **effector with `effectorPos`** → a `_target` dict (single-joint); **else pose path** (fullbody
  and IK'd effectors) → `localQuats`→axis-angle + root.
- `_load_constraints()`: effector pins use **position-only** subclasses (`_LeftFootPO` etc., via
  `_ee_position_only_update`) that constrain the effector position + rotation + `smooth_root_2d` but **drop
  `root_y_pos` + `global_root_heading`** (so the body can rise/turn to reach). Direct targets use
  `_SingleJointTarget`. `root2d`/`fullbody` use the stock classes. `_apply_target_offsets` shifts pinned positions.
- `create_conditions` skips absent channels, so dropping root height/heading is safe.

Unity components:
- **KimodoWaypoints** (root path) — place ground waypoints; a **rotatable facing arrow** per waypoint (sent as
  `global_root_heading`). Editor draws the pelvis path + waypoint discs + facing arrows (no text labels; `markerRadius`
  is display-only and does NOT affect the constraint — only `world` position + optional facing do). **Works well** —
  this is the reference for "the affine mapping is correct."
- **KimodoEffectors** (hand/foot) — world targets (position + optional rotation) per frame. **Full-body IK**: takes
  the current pose, two-bone-IKs the leg/arm to reach the target (steps the body/root when out of reach), sends the
  resulting **pose** as a position-only effector constraint. Gizmos = position + rotation handle, kind dropdown,
  "Snap to bone". Y uses `FitVerticalY`. (`effectorPos`/rotation-gizmo path exists but IK-pose path is what's used.)
- **KimodoPoseConstraints** (whole rig / fullbody) — each key is an editable **ghost skeleton** drawn at its frame
  (FK of the stored pose → `KimodoRootMap.KimodoToWorld`; no character posing). **Default Show on**, multiple keys
  visible at once. Select a key (◉), click a body-joint dot, rotate it (world gizmo → Kimodo local via
  `FlipQ=(x,-y,-z,w)`). **Pelvis move handle** (2026-07-26): when a key is selected, a world-axis `PositionHandle`
  at the root moves the whole pose incl. **HEIGHT** (`k.root` via `KimodoRootMap.WorldToKimodo`, Y is invertible) —
  this is how you lift a pose onto a box; the root joint's rotation gizmo is suppressed to avoid overlap. "Align to
  frame" reseeds from the motion. Sent as `fullbody` (constrains all joint positions + `root_y_pos`, so height is
  enforced — unlike waypoints/root2d which are ground X/Z only).
  - **Generate pose from prompt** (2026-07-27): instead of hand-rotating joints, a key can carry a text `prompt`;
    `KimodoPoseConstraints.GeneratePoseForKey(key, cb)` calls `/generate` for a short throwaway clip (`poseGenSeconds`,
    default 1.5s), samples one frame (`poseSampleAt` 0..1, default 1=last), and writes only that frame's `localQuats`
    into the key — the key's frame + root (its waypoint/position) are untouched. No model/server change (Kimodo has no
    literal text→pose; this is text→short-motion then sample a frame). Editor: per-key Prompt field + "Generate pose"
    button; `GeneratingPose` flag for UI. Local rotations are parent-relative so the sampled pose transfers regardless
    of which way the throwaway clip faced. **Per-key waypoint mode** (`Key.useWaypoints`, "WP" toggle in the prompt
    row; default off): runs the FULL timeline (`g.duration`) with the sibling `KimodoWaypoints` path applied
    (`GatherWaypointConstraints`) and samples the key's own frame, copying that frame's localQuats AND root (right
    place + Y). **Prompt-only mode** (WP off, the good default): short clip sampled by `poseSampleAt`, then
    `GraftOntoMainFrame` replaces the generated Hips (root) rotation + root position with the MAIN motion's values at
    key.frame — so the pose faces the walk direction and sits at the correct pelvis height (fixes the "wrong
    direction / wrong Y" that raw prompt-only had; full-generate mode ignored the prompt). Only waypoints, per user
    request — not effectors/other pose keys. **Idle** (`SetIdlePose`) = reset to the neutral
    rest pose (identity local rotations) keeping frame + root; **Align to frame** = pin the motion's natural pose there.
    Per-key UI: the resets + active-region presets (All/None/Upper/Lower) live in a **⋮ dropdown** (`ShowKeyMenu`,
    GenericMenu) on the key's header row, not as separate buttons.
  - **Per-key joint activation** (2026-07-27): a key can DEACTIVATE joints (per key) so only the active part is
    constrained + shown. Editor: "Activate joints" mode — clicking a dot toggles **exactly that one joint**
    (independent, so you can deactivate all except a hand); per-key quick buttons **All / None / Upper / Lower** for
    bulk (Upper/Lower still use subtree fill); inactive joints draw dimmed. Stored as `Key.jointActive` (bool[J], null
    = all). `BuildConstraints` sends `activeJoints` (active BODY-joint names) only when a subset is off; all-active
    omits it → stock `FullBodyConstraintSet`. Server builds **`_PartialFullBody`** which pins **only those joints'
    positions** (+ smooth_root/root_y/heading), rest free — real "upper-body only". Names→indices at load (SOMA
    30/77-safe). **CRITICAL:** `_PartialFullBody` is a **plain class, NOT a `FullBodyConstraintSet` subclass** — because
    `postprocess.py` hard-rewrites the WHOLE body's rotations for any FullBody/EndEffector constraint (it can't mask
    per-joint), which was snapping the free joints back (the "still constrains whole body" bug, 2026-07-27). As a plain
    class postprocess's `isinstance` checks skip it (its `else` is a no-op `NotImplementedError(...)` with no `raise`),
    so the rest stay free. **`_PartialFullBody` must impute the active joints' ROTATIONS, not just positions**
    (2026-07-27 fix #2): `FullBodyConstraintSet` conditions positions only ("global rotations are not used here") and
    gets its actual pose from the postprocess snap — which partial skips — so a positions-only partial did *nothing*
    (constraint weight didn't help). Fix: `update_constraints` appends BOTH `global_joints_positions` AND
    `global_joints_rots` (the primary pose channel) for the active joints, + smooth_root/root_y/heading. Same pattern
    the model itself uses for cross-segment transitions (adds an EndEffectorConstraintSet "to capture hand/feet
    rotations"). **Server change → restart the bridge** (repo `Server/` copy needs re-syncing before the next release).
  - **Ghost mesh** (added 2026-07-26, `showGhostMesh` toggle on the component; editor-only): for each *shown*
    key it keeps a **transparent clone** of the target character posed at that key via the real retarget path, so
    you see the actual model (skin/mesh renderers), not just the skeleton, as you edit. `Editor/KimodoPoseGhosts.cs`
    manages the clones (HideAndDontSave, one `KimodoPlayer` each → `KimodoPlayer.PoseFromLocal`, torn down on
    deselect/regeneration; orphan-swept after domain reload). Renders with `Runtime/KimodoGhost.shader`
    (`"Kimodo/GhostMesh"`, URP transparent fresnel). Look is **fixed white ~0.51 alpha** (not user-editable — set in
    `KimodoPoseGhosts`, only an on/off toggle in the inspector). Clones sit exactly over the ghost skeleton because
    both use the same retarget + `rootMotionScale`. **Transparency slider** (`ghostOpacity`, 0–1, default 0.51) sets
    the ghost's base alpha. Deactivated joints **fade out**: `KimodoPoseGhosts` instances each clone's skinned mesh
    and writes a per-vertex mask into vertex-colour alpha = the skin-weighted average of its bones' active flags
    (bone→HumanBodyBones→SOMA-active), so the boundary blends smoothly; the ghost shader multiplies alpha by it.
    Recomputed only when the key's activation signature changes.

---

## 7b. Connection + pre-generation authoring (2026-07-27)

- **Auto-reconnect** — the bridge "connection" is stateless HTTP (Connect = health-check + preload; the server keeps
  the model loaded). The in-memory `Connection` flag is `[NonSerialized]` and wiped on every domain reload (Play-mode
  enter/exit, recompile), which is why you used to re-press Connect. `Editor/KimodoBridgeAutoConnect.cs`
  (`[InitializeOnLoad]` → `delayCall` after each reload) silently reconnects any `KimodoBridge`, gated on a
  `SessionState` flag set when you Connect and cleared by the new **Disconnect** button (`KimodoBridge.Disconnect`).
  A window would NOT have fixed this — same reload wipes window fields too.
- **Author waypoints/poses before the first generate** — the gizmos convert via a world↔Kimodo affine measured from a
  motion, and the pose ghost needs the skeleton — none of which existed pre-generation, hence the old gating. Now: the
  bridge fetches the rest skeleton on connect (`KimodoBridge.Skeleton` via `GET /skeleton`, exposed as
  `KimodoGenerator.PoseSkeleton`/`HasAuthoringSkeleton`); `KimodoRootMap.Compute` + `KimodoWaypoints.ComputeMapping`
  fall back to an **estimated** mapping from the character's bind pose (`worldHips0`=current hips, `k`=`humanScale`,
  `kimodoRoot0`≈origin); frame counts come from `KimodoGenerator.AuthoringFrameCount` (duration×fps estimate). The
  editors + `BuildRootConstraints`/`BuildConstraints`/`KimodoPoseGhosts` use `PoseSkeleton`/`AuthoringFrameCount`, so
  you can place waypoints + author poses (Generate-pose / Idle; "Align to frame" needs a real motion) and they apply
  on the **first** Generate. Placement is **approximate** until a real motion refines the mapping. **Server change
  (`/skeleton`) → restart the bridge.**

## 7c. Timeline / sequencer (2026-07-29)

**`Window ▸ Kimodo ▸ Timeline`** — one dockable window over a character's whole shot. Built as a custom
IMGUI `EditorWindow` (the decision from 2026-07-27: **not** Unity's native Timeline, whose runtime
Playables/binding model is a poor fit for an author-then-bake diffusion tool).

- **The file** is `KimodoTimeline` (a `ScriptableObject` asset) assigned to `KimodoGenerator.timeline`.
  It owns the shot's **prompt segments** (`prompt` + `seconds` each). `KimodoGenerator.EffectivePrompt` /
  `EffectiveDuration` compose them into Kimodo's own encoding ("A. B." + `"2 3"`) and `Generate()` sends
  those; with no asset assigned the Generator's single prompt/duration fields are used exactly as before
  (fully backwards-compatible). `EstimatedFrameCount` reads `EffectiveDuration`, so the frame axis and all
  the "Add @ frame" authoring work **before** the first generate.
- **Where the keys live:** the constraint keys stay on the scene components (`KimodoWaypoints` /
  `KimodoEffectors` / `KimodoPoseConstraints`) — they hold world positions and draw Scene gizmos, so the
  scene is their home. The window is a second view onto the same data as the inspectors; edits from either
  side agree. `Take ▸ Save/Load` copies them in and out of the asset (`KimodoTimeline.CaptureTake` /
  `ApplyTake`) when you want the whole shot in one file; Load **replaces** the character's keys.
- **Tracks** (x axis = absolute frames, which is what every Kimodo constraint is keyed on): Prompt
  (segment blocks — drag the right edge to retime, click to edit, right-click to split/duplicate/delete),
  Waypoints, Effectors (coloured per hand/foot), Poses. Keys drag sideways to retime, right-click to
  delete, `＋` on a track header adds at the playhead (adding the component first if missing). The bottom
  pane edits whatever is selected — including a pose key's **prompt + Generate pose / Idle / Align to
  frame**, so prompts, constraints and key points are all manipulable from the window.
- Segment blocks are laid out on the **authored** durations (`FrameCountOf` = `floor(seconds × fps)`, the
  same math the server does), so retiming shows up immediately. They still match the motion: Kimodo's
  multi-prompt output is exactly `sum(int(seconds × fps))` frames — a segment generates `N + K` frames but
  the previous segment's last `K` are popped first, so the transition frames are absorbed, not added.
  (A first version laid blocks out on the generated `numFramesPerSegment` when the counts matched; that
  **broke retiming** — the blocks and the ruler only moved after a regenerate. Don't reintroduce it.)
- `KimodoGenerator.AuthoringFrameCount` is the timeline's own total whenever a timeline is assigned (i.e.
  what the NEXT Generate will produce), not the current motion's length — otherwise every key clamps
  against a stale frame count after retiming. The window's axis is `max(that, motion frames)` so keys past
  a shortened timeline stay reachable.
- **Interior periods are rewritten to commas** (`CleanSegmentText`) with a warning in the pane: a period
  inside a segment would silently split it server-side and then the duration count wouldn't match the
  prompt count (`_texts_and_frames` raises 400).
- **Layout (2026-07-29 revision):** the selection panel is a **resizable left column** (`_paneW`, drag the
  splitter), the timeline fills the rest — not a bottom strip. Per-selection actions are a compact glyph
  tool strip (✂ split / ⧉ duplicate / ▲▼ reorder / ⇥ go-to / ⌖ look-at / ✕ delete); glyphs rather than
  `EditorGUIUtility.IconContent` names, which differ between Unity versions (matches the ◉ ⋮ ✕ ＋ style the
  rest of the package already uses).
- **Shortcuts:** Space play/pause, Delete (or Backspace) removes the selection, ←/→ step a frame (Shift = 10),
  Home/End jump to the ends, F looks at the selected key, Esc deselects. Guarded by
  `EditorGUIUtility.editingTextField` so typing a prompt never triggers them, and the object fields are drawn
  **before** `HandleShortcuts` so they consume Delete first (otherwise clearing an ObjectField would also
  delete a key).
- **`Editor/KimodoPlayback.cs` is THE editor playback clock** (`[InitializeOnLoad]`, one
  `EditorApplication.update` hook, `SetPlaying`/`Toggle` + an `Advanced` event the UIs repaint on). Two
  bugs forced it: (1) the Generator inspector AND the Timeline window each advanced `PreviewTime` from
  their own update hook, so with the character selected and the window open everything played at **double
  speed** (three UIs → triple, etc.); (2) `PreviewTime` was advanced without wrapping, so with Loop on the
  playhead stuck at the last frame while the retarget kept looping. Speed now lives on the generator
  (`PlaybackSpeed`, `[NonSerialized]`) so both UIs drive the same value. **Never advance `PreviewTime`
  from a UI again** — call `KimodoPlayback`.
- **Clip everything to the track area** (`FillClipped` / `ClipLabel`): a zoomed or scrolled segment block
  is drawn from a far-negative x, and `EditorGUI.DrawRect` doesn't clip — the block bled over the header
  column and the panel beside it. Keys were already skipped outside the area (their few px of spill land
  on the header column, which is painted after the tracks).
- **IMGUI rule for this window — the single most important thing to keep:** *nothing may change what the
  window draws except at the start of a **Layout** pass.* Everything structural (selection, add/remove a
  key or segment, swapping the character or the timeline asset, Generate, Bake, file panels, Take load) is
  queued with `Defer(...)` — a **list**, since two actions can queue in one pass — and drained at the top
  of `OnGUI` on `EventType.Layout`, followed by `ValidateSelection()`. Two failures taught this:
  - "Mismatched LayoutGroup" — mutating during the event pass, so the control sequence differs from the
    one the layout pass built.
  - `ArgumentException: Getting control 0's position in a group with only 0 controls` (2026-07-29, hit by
    deleting the last key) — the pane methods used to *clear the selection while drawing* when their index
    had gone stale (e.g. the key was deleted from the component's own inspector): the layout pass then
    registered **zero** controls and the repaint pass drew the empty panel. Panes must therefore never
    assign `_selKind`; a stale index falls back to `DrawEmptyPane()`, and `ValidateSelection()` (layout
    only) is what actually drops a dead selection. **Do not add `_selKind = …` inside a Draw* method.**
- The timeline area also keeps its last known geometry during layout (`GetRect` returns a dummy rect then,
  which would otherwise clamp the scroll offset to 0 every frame), and the scrollbar is drawn
  unconditionally (disabled when everything fits) so the control count never changes.
- `KimodoPoseConstraints.AlignKeyToMotion(key)` was factored out of the pose inspector so the window shares it.
- **PER-SEGMENT CONSTRAINT WEIGHT (2026-07-29, server change → RESTART the bridge).** A segment can pin
  harder or looser than the rest (`Segment.overrideConstraintWeight` + `constraintWeight`, shown as a `w5`
  badge on the block). Why it works: Kimodo's `_multiprompt` generates **one segment at a time**, calling
  `model._generate(..., cfg_weight=...)` exactly once per segment in order — there is just no public
  per-segment knob. `server.py`'s `_per_segment_constraint_weight(model, weights)` context manager shadows
  `model._generate` for the duration of one request and rewrites `cfg_weight[1]` (the constraint weight;
  the text weight is untouched) with the Nth entry. Chosen over patching `kimodo_model.py` because Kimodo
  lives **outside** this repo — a library patch would have to be re-applied by every user and lost on every
  Kimodo update, whereas this ships in `Server/`. Defensive by design: calls past the end of the list keep
  the caller's weight and the count mismatch is logged, so a future Kimodo refactor degrades to "the global
  weight was used" instead of crashing or silently shifting weights. Wire: `KimodoTimeline.
  BuildSegmentConstraintWeights(global)` (null unless some segment overrides) → `KimodoGenerateRequest.
  segment_cfg_weights` → `GenerateRequest.segment_cfg_weights` (400 if the count ≠ segment count). Only
  sent when there are constraints at all. Shim verified standalone (weights applied in order, `_generate`
  restored afterwards, overflow falls back).
- **FROZEN SEGMENTS — PARKED / HIDDEN (2026-07-29, user's call: "I want to add it in the future, but I
  just want to hide it").** The switch is **`KimodoTimeline.FreezeEnabled` (`static readonly bool`, false)**
  — flip that one field to bring the whole feature back. While it is false the ❄ tool button, the frozen
  info box, "Re-capture from current motion" and the context-menu Freeze entry are all hidden, and
  `Segment.HasFrozenContent` returns false so a segment left `frozen` in an existing asset is generated
  normally and can still be retimed (no way to get stuck frozen with no UI to release it). Everything the
  feature needs is still in the code, untouched: `Segment.frozen`/`frozenClip`, `FrozenClip`,
  `FreezeSegment`, `BuildFrozenConstraints`, `KimodoGenerator.ApplyFrozenSegments`, and the window's
  `ToggleFreeze`/`RecaptureFreeze`/`Frozen(seg)`. `static readonly` rather than `const` on purpose: a const
  folds at compile time and turns every parked branch into an "unreachable code" warning in the console.
  How it worked, for when it comes back: ❄ on a segment kept it exactly as generated — `FreezeSegment` copies
  that frame range's `localQuats` + `rootPositions` out of the current motion into the asset
  (`Segment.frozenClip`), and every later Generate (a) sends the kept content as a subsampled (~10/s)
  `fullbody` constraint so the segments around it are generated flowing in and out of it, then (b)
  **splices the exact frames back** over that range in every returned sample
  (`KimodoGenerator.ApplyFrozenSegments`, run before `Motion =` so the preview and bake both see it).
  **It does not make generation faster** — Kimodo's `_multiprompt` feeds each segment into the next
  (transition constraints built from the previous segment's output), so a segment cannot be skipped
  without reimplementing that loop server-side; the diffusion still runs over the whole sequence. A frozen
  segment's duration is **locked** to the frames it kept, and the snap adds half a frame
  (`seconds = (len + 0.5) / fps`) because both the block math and the server TRUNCATE `seconds × fps` —
  `61/30 = 2.0333` would round-trip back to 60 frames. Junctions are a hard cut (no crossfade); if a pop
  shows up, slerp-blend ~3 frames each side.
- **Compile-verified from the CLI** (see the `unity-cli-compile-check` memory); not yet exercised in the
  Editor by the user. The repo's `Server/` copy needs re-syncing before the next push.

## 8. Known issues / open work

- **Effector HEIGHT is still soft** — a raised foot (e.g. onto a box) often doesn't fully lift: Kimodo is soft +
  needs an in-distribution prompt + the body positioned so it's reachable. Recipe: step-up prompt + a **waypoint** to
  bring the body + raise **Constraint weight** (5–6). IK helps but isn't a hard solve.
- **Effector rotation gizmo not wired into the IK path** (foot uses its natural orientation). Wiring foot-flat-on-box
  is a good next task.
- **Pose ghost** is the Kimodo skeleton (SOMA proportions) — shows the *pose* faithfully but won't pixel-overlay the
  Mixamo mesh. Fine per the user; could scale/retarget the ghost if desired.
- **Foot sliding** (treadmill) — no foot-lock yet; `footContacts` are in the payload → pin planted feet in
  retarget/bake. Not started.
- **Timeline — BUILT (2026-07-29, see §7c), not yet Editor-tested.** Remaining ideas: per-segment
  constraint filtering/colour-coding of keys by segment; box-select + multi-key drag; a transition-frames
  control (`num_transition_frames` is still the server default 5); showing generation progress on the
  track; and per-segment sampling overrides (steps/seed) if that ever proves useful.
- **Per-joint constraint intensity (graded weight) is still NOT supported by the model.** `create_conditions` writes
  constrained joints into a **boolean** `motion_mask`; the only strength dial is the global `cfg_weight[1]`
  (`constraintWeight`). Binary joint *selection* now exists (pose-key activation → `_PartialFullBody`, see §7), which
  covers "upper-body only". True graded weights would need the mask → float weight folded into the diffusion guidance
  (model-side change) — not started.
- **Prompt-pose graft is hips-entire** — `GraftOntoMainFrame` keeps the main frame's whole Hips rotation (facing +
  tilt), so a pose that hinges from the hips loses that pitch. Possible refinement: align only the heading (yaw) and
  keep the generated pose's own lean.
- Model switching loads a second model (its own text encoder) — heavy; user stays on SOMA.

## Reference
- Kimodo constraints: `H:\kimodo\kimodo\kimodo\constraints.py`; motion rep + `create_conditions`:
  `kimodo\motion_rep\reps\kimodo_motionrep.py`; demo constraint code: `kimodo\demo\generation.py`, `kimodo\viz\`.
- Docs: `https://research.nvidia.com/labs/sil/projects/kimodo/docs/`.
