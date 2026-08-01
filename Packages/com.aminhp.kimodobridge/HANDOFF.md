# Kimodo ↔ Unity Bridge — Project Handoff

> **Purpose:** single reference to continue this project in a new chat. Point the assistant here first
> (“read `Packages/com.aminhp.kimodobridge/HANDOFF.md`”). Persistent memory also exists at
> `C:\Users\mahag\.claude\projects\h--kimodo\memory\` (`kimodo-unity-bridge.md` — detailed running log,
> `kimodo-setup.md` — how Kimodo is installed).

Last updated: 2026-07-31. Owner/brand: **AminHP** (independent wrapper — *not* affiliated with NVIDIA).
Published: **https://github.com/Amin-HP/Kimodo-Unity** (branch `main`, currently private). The package
ships the Python server at `Packages/com.aminhp.kimodobridge/Server~/kimodo_bridge/` — **re-sync it from
the live `H:\kimodo\kimodo\kimodo_bridge\server.py` before each push** (see the `kimodo-github-repo`
memory). The old duplicate at the repo root (`Server/`) was removed once the package started shipping
and running its own copy, so there are two copies to keep in step, not three.

**Read §7e–§7i first if you are picking this up now**: the constraint system, its editing UX and the
freeze feature were all reworked on 2026-07-31, and the older sections describe what came before.

---

## 1. What this is

A real-time, interactive add-on that connects **NVIDIA Kimodo** (text→human-motion diffusion model) to
**Unity 6**. Generate motion from a text prompt, preview it on a Unity **Humanoid** character, author
**constraints** (root path, hand/foot targets, whole-body poses), and **bake** to an `AnimationClip`.
Uses the **SOMA** model by default (77 joints); model is user-selectable.

**Status: fully working** end-to-end — connect → generate → preview → constraints (waypoints,
end-effectors, pose) → timeline with frozen segments → bake. Confirmed by the user in-editor, including
the reworked hand/foot keys, the Scene-view tool overlay and partial generation of frozen timelines.

---

## 2. How to run

1. **Server:** `cd H:\kimodo\kimodo; .\run_bridge.ps1` (add `-Preload soma` to load at startup).
   Activates the venv, sets `TEXT_ENCODER_DEVICE=cpu` (8 GB VRAM), serves `http://127.0.0.1:8765`.
   Generation is CUDA + CPU text-encoder → ~tens of seconds per generate. **Restart the server after any
   `server.py` change.**
2. **Unity:** open `H:\kimodo\Kimodo Unity` (Unity 6000.4.4f1, URP). Component workflow:
   - `GameObject ▸ Kimodo ▸ Bridge Manager` — creates the **KimodoBridge** manager.
   - Select a **Humanoid** character (Rig → Humanoid; tested with a stock Mixamo model) →
     `GameObject ▸ Kimodo ▸ Set Up Selected Character` — adds **KimodoGenerator** + **KimodoEndEffectors**,
     wires the bridge.
   - On KimodoBridge: **Connect** (preloads the model). On KimodoGenerator: prompt → **Generate** →
     preview (play/scrub) → **Bake to AnimationClip**.
   - Add constraint components as needed (`GameObject ▸ Kimodo` or Add Component): **KimodoWaypoints**,
     **KimodoEndEffectors**, **KimodoPoseConstraints** — each draws Scene gizmos and is gathered on Generate.

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
  Constraint machinery (see §7). Launcher `..\run_bridge.ps1` (only for running it by hand — Unity runs
  the module directly, §7j). `__main__.py`, `__init__.py`. `GET /progress` reports how far a generation
  in flight has got (§7k). **This is the live server.** The shipped copy is
  `Packages/com.aminhp.kimodobridge/Server~/kimodo_bridge/` — keep it in sync (see header).

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
- `KimodoMotionCache.cs` — keeps the generated motion in `Library/KimodoMotionCache/` so a compile,
  play mode or a restart does not lose it (§7i).
- `KimodoGhost.shader` — `"Kimodo/GhostMesh"`, URP transparent fresnel used by the pose ghost-mesh preview.
- **Components:** `KimodoBridge.cs` (manager), `KimodoGenerator.cs` (per-character generate+preview+bake state),
  `KimodoEndEffectors.cs` (hand/foot keys — a pose + a limb mask, §7e), `KimodoWaypoints.cs` (root path + facing),
  `KimodoPoseConstraints.cs` (whole-body pose keys).

**Editor**
- `KimodoBaker.cs` — bake `KimodoMotion` → Humanoid `AnimationClip` (RootT/RootQ + muscle curves).
- `KimodoBridgeEditor.cs`, `KimodoGeneratorEditor.cs`, `KimodoEndEffectorsEditor.cs`, `KimodoWaypointsEditor.cs`,
  `KimodoPoseConstraintsEditor.cs` — custom inspectors + Scene gizmos.
- `KimodoTimelineWindow.cs` — the sequencer window (`Window ▸ Kimodo ▸ Timeline`), see §7c.
- `KimodoPoseGhosts.cs` — transparent ghost-mesh clones for any posed key (`Sync(generator, opacity, views)`).
- `KimodoEditState.cs` — **static** tool + key selection shared by the overlay, the inspectors and the
  Timeline window (see §7f — this must never move back onto an editor instance).
- `KimodoConstraintOverlay.cs` — the Scene-view **Overlay** with the editing tools + key navigation (§7f).
- `KimodoMotionRestore.cs` — `[InitializeOnLoad]`; puts each character's cached motion back after a
  domain reload or a scene open (§7i).
- `KimodoProgressPoller.cs` — `[InitializeOnLoad]`; polls `/progress` while a character is generating
  and writes it onto the generator for the two progress bars (§7k).
- `KimodoServerLauncher.cs` — runs / stops `run_bridge.ps1` from the Bridge inspector (§7j).
- `KimodoMenu.cs` — the `GameObject ▸ Kimodo ▸ …` menu items.
  (The legacy all-in-one `KimodoBridgeWindow.cs` was **removed** in the v0.1.0 cleanup — component workflow only.)

### Example (outside the package) — `H:\kimodo\Kimodo Unity\Assets\`
`KimodoRailAuthor.cs` is an abstract base holding everything the shapes share (rail sampling, arm fit,
pose building, gizmo chrome, context menus) with four abstract members for the path shape;
`KimodoLineWaypoints.cs` is the straight version (open rail: clamps at its ends, `railOverhang`).
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
- pose-based (fullbody **and** hand/foot keys): `localQuats[N*J*4 wxyz]`, `rootPositions[N*3]`.
- `activeJoints[]` (fullbody only): active BODY-joint names → server pins only those (partial pose). Omit = all.
- `freeRoot` (hand/foot only): `false` (default) = the demo's behaviour, the pose's root ground position,
  height and facing are pinned with the limb; `true` = pin the limb + ground position only. See §7e.
- direct effector target (**no longer sent** — see §7e): `effectorPos[N*3]`, `effectorRot[N*4]`, `effectorRootXZ[N*2]`, `constrainRot`.
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
- **The affine `KimodoRootMap`** cleanly inverts world↔Kimodo for the **root/positions** (proven by
  waypoints), but a hand/foot's *height* is set by leg/arm length + muscle retarget, so it is **not** a
  clean affine. The old code measured a per-effector vertical fit to work around that (`FitVerticalY`,
  now deleted). The lesson that replaced it: **do not map a hand/foot position through an affine at all**
  — author it on a pose and read the effector off that pose's FK, so only the root ever goes through the
  mapping (§7e).

---

## 7. Constraint system (current, all working)

Server: `KimodoGenerator.Generate()` gathers constraints from `KimodoEndEffectors` + `KimodoWaypoints` +
`KimodoPoseConstraints` and sends them. `server.py`:
- `_build_constraint_dicts()` routes by type: **root2d** → `smooth_root_2d` (+ optional `global_root_heading` from
  `rootHeading2d`); **effector with `effectorPos`** → a `_target` dict (single-joint, legacy/unused); **else pose
  path** (fullbody and hand/foot keys) → `localQuats`→axis-angle + root, plus `_free_root` when the key asked for it.
- `_load_constraints()`: **stock Kimodo classes by default** (what the demo uses). A hand/foot key with
  `_free_root` instead uses the position-only subclasses (`_LeftFootPO` etc., via `_ee_position_only_update`)
  which constrain the effector position + rotation + `smooth_root_2d` but **drop `root_y_pos` +
  `global_root_heading`**, so the body can rise/turn to reach. A `fullbody` with `active_joint_names` uses
  `_PartialFullBody`; direct targets use `_SingleJointTarget`. `_apply_target_offsets` shifts pinned positions.
- `create_conditions` skips absent channels, so dropping root height/heading is safe.

Unity components:
- **KimodoWaypoints** (root path) — place ground waypoints; a **rotatable facing arrow** per waypoint (sent as
  `global_root_heading`). Editor draws the pelvis path + waypoint discs + facing arrows (no text labels; `markerRadius`
  is display-only and does NOT affect the constraint — only `world` position + optional facing do). **Works well** —
  this is the reference for "the affine mapping is correct."
- **KimodoEndEffectors** (hand/foot) — **rewritten 2026-07-31 to match the demo**; see §7e.
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
  `KimodoEndEffectors` / `KimodoPoseConstraints`) — they hold world positions and draw Scene gizmos, so the
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
  just want to hide it") and later **brought back and rebuilt — see §7h**, which is the current design.
  The switch is still there: **`KimodoTimeline.FreezeEnabled` (`static readonly bool`, now true)** hides
  the whole feature when false, and `Segment.HasFrozenContent` then returns false so a segment left
  `frozen` in an existing asset is generated normally and can still be retimed (no way to get stuck
  frozen with no UI to release it). `static readonly` rather than `const` on purpose: a const folds at
  compile time and turns every guarded branch into an "unreachable code" warning in the console.
  ❄ on a segment keeps it exactly as generated — `FreezeSegment` copies that frame range's `localQuats` +
  `rootPositions` out of the current motion into the asset (`Segment.frozenClip`).
  **The first version generated the whole timeline anyway and spliced the kept frames back**, on the
  assumption that a segment could not be skipped without reimplementing Kimodo's `_multiprompt` loop. It
  can be: the loop's own handover (a transition constraint built from the previous segment's output) is
  reproducible through the public constraint API, so frozen segments are now not requested at all, and
  `BuildFrozenConstraints` / `KimodoGenerator.ApplyFrozenSegments` were deleted with that approach.
  What still holds: a frozen segment's duration is **locked** to the frames it kept, and the snap adds
  half a frame (`seconds = (len + 0.5) / fps`) because both the block math and the server TRUNCATE
  `seconds × fps` — `61/30 = 2.0333` would round-trip back to 60 frames. Junctions are a hard cut in the
  pose (no crossfade); if a pop shows up, slerp-blend ~3 frames each side.
- **Compile-verified from the CLI** (see the `unity-cli-compile-check` memory), and since confirmed
  working in the Editor by the user.

## 7e. Hand/foot constraints, rebuilt like the demo (2026-07-31)

The old target-based `KimodoEndEffectors` (drag a point in the world → two-bone IK the *motion's* pose →
send it position-only) was replaced, because **a Kimodo end-effector constraint is a POSE, not a point.**
Read `kimodo/constraints.py::EndEffectorConstraintSet.update_constraints`: from the pose it is given it
pins the effector chain's joint **positions**, the effector's global **rotation**, and the root's
`smooth_root_2d` + `root_y_pos` + `global_root_heading`. The demo never sends anything else — its
`EEJointsKeyframeSet` (kimodo/viz/constraint_ui.py) stores the *whole* `joints_pos [J,3]` + `joints_rot
[J,3,3]` of the frame being edited plus a list of `joint_names`, and `demo/generation.py` regroups one
keyframe into **one constraint object per limb, each carrying that same pose**.

What the old version got wrong, and what replaces it:
- **`FitVerticalY`** — a least-squares fit of `worldY = a·kimodoY + b` per limb, because a hand/foot's
  height is not a clean affine of the root map. **Deleted.** The handle now sits on the FK of the key's
  own pose, so world↔Kimodo round-trips through the *root* affine only (the mapping that waypoints
  proved correct) and there is nothing to guess.
- **Silent root shifting** — when the target was out of the limb's reach it moved the pelvis toward it,
  and that shifted pelvis was then pinned as `smooth_root_2d`/`root_y`. **Deleted.** Out of reach draws a
  yellow dotted line to the target the limb could not make (§7g dropped the wording that went with it,
  and §7g also removed the pelvis tool from hand/foot keys — the body is moved with a waypoint, or by
  ticking Free root).
- **Position-only by default** (`_LeftFootPO` etc. dropping root height + facing) — that existed to stop
  the *motion's* root fighting a dragged point. Now the pose is authored, so the demo's stock classes are
  the default and **per key** `freeRoot` opts back into the relaxed variant (server: `_free_root`).

Data model (`Runtime/KimodoEndEffectors.cs`): `Target` = `frame` + `limbs` (a `[Flags] Limb` mask, so one key
can pin several limbs sharing one pose, exactly as the demo merges them) + `localQuats` + `root` +
`hasPose` + `show` + `freeRoot`. `BuildConstraints()` emits one per-limb constraint per active limb — per
limb, **not** generic `end-effector`, because `postprocess.py::_build_constraint_masks_dict` keys its
exact-snap masks off `constraint.name` (`left-hand` / `right-foot` / …) and a generic `end-effector`
matches no mask. The legacy `kind`/`world`/`rot` fields are still serialized: `EnsurePose()` migrates an
old key by seeding the pose from the motion and IK-ing the limb onto its old `world` point, once.

Editor (`Editor/KimodoEndEffectorsEditor.cs`) mirrors the demo's viewer:
- ghost **skeleton** per shown key — constrained joints/bones **red**, the rest light grey (the demo's
  `constrained_idx = [root_idx] + ee_joint_indices`, colours `(255,0,0)` / `(220,220,220)`);
- an **axes gizmo** on each effector's base joint (the demo's `add_batched_axes`) showing the rotation
  that is being constrained — that rotation *is* part of the constraint, which the old version ignored;
- a `left-hand + right-foot @ 42` label over the pelvis, and "show only the key at the current frame"
  (the demo's `show_only_current_constraint`);
- three tools: **Move limb** (drag the hand/foot → IK on the key's pose, plus a pelvis handle to move the
  whole pose incl. height), **Aim limb** (rotate the effector), **Rotate joints** (the demo's editing
  mode: click any joint, rotate it). `keepEffectorOrientation` (default on) re-derives the effector's
  local rotation after an IK drag so a foot stays flat while the leg bends;
- optional transparent ghost **mesh**, reusing `KimodoPoseGhosts` (now generalised: `Sync(generator,
  opacity, IList<PoseView>)`, keyed on an `owner` object, so any editor can drive it).

Constrained joints, confirmed against `skeleton.expand_joint_names()` on **somaskel30** (the model
skeleton — poses are authored on somaskel77 and converted by `from_dict`): hand ⇒ positions
`LeftHand` + `LeftHandMiddleEnd`, rotation `LeftHand`; foot ⇒ positions `LeftFoot` + `LeftToeBase`,
rotation `LeftFoot`. Matches the docs ("wrist position and rotation along with the hand end position").

Verified: package compiles from the CLI (both assemblies), and the server routing was exercised in
Python — default key ⇒ `LeftHandConstraintSet` pinning `{positions, rots, smooth_root_2d, root_y_pos,
global_root_heading}`; `freeRoot` ⇒ `_RightFootPO` pinning `{positions, rots, smooth_root_2d}`;
`crop_move` survives both. **Server changed ⇒ restart the bridge** (repo `Server/` copy re-synced).

## 7f. Constraint editing UX + the gizmo bugs (2026-07-31, same day, after user testing)

User report after trying §7e: one hand worked; adding a second one made the motion glitchy and
"doesn't fit the skeleton"; **rotating worked for a couple of drags and then stopped entirely**;
moving was "a little glitchy, sometimes it doesn't move". Three distinct causes, all fixed:

1. **The selection and the active tool lived on the `UnityEditor.Editor` instance.** Unity throws the
   editor away and rebuilds it whenever the Inspector rebuilds — which `EditorUtility.SetDirty` /
   `Undo.RecordObject` during a drag can trigger. That reset `_selKey` to −1 (every handle vanishes)
   and `_tool` to its default (a rotation gizmo silently became a move gizmo). Exactly "it rotates a
   couple of times and then it doesn't rotate at all". **Fix: `Editor/KimodoEditState.cs`** — static,
   EditorPrefs-backed tool + `(Component owner, int index)` selection + `SelectedJoint`. **RULE: never
   keep constraint selection or tool state in an editor instance field.**
2. **Handles were re-anchored to their own output.** `Handles.PositionHandle(posFromFK…)` was fed the
   FK position of the effector *after* the IK had clamped it to the limb's reach, so the next event's
   drag delta was measured from a point that had moved by less than the drag — a feedback loop that
   jitters and stalls. **Fix:** while `_dragging`, the handle is anchored to the drag value
   (`_dragPos`/`_dragRot`), released on `MouseUp`; the pose follows the target, not the other way
   round. Out of reach now draws a dotted line to the target + a label instead.
3. **Two keys on one frame.** Adding the second hand as its own key gave two end-effector constraints
   at the same frame, each pinning `smooth_root_2d`/`root_y`/heading from *its own* pose —
   contradictory conditioning, which is the glitchy result. **Fix:** `AddOrMergeKey(frame, limb)` —
   one key per frame, extra limbs join the key already there (the demo merges the same way, unioning
   `joint_names`). Handle sets are also built from the fixed `AllLimbs` order, and decoration is drawn
   in a separate Repaint-only pass after the handles, so control IDs never shift mid-drag.

Also in this round:
- **Renamed to match the demo's vocabulary:** `KimodoEffectors` → **`KimodoEndEffectors`** (the demo's
  track is "End-Effectors"), files + editor renamed with their `.meta` GUIDs preserved so existing
  scene references survive; timeline track label and inspector headings follow ("Full-Body" for pose
  keys). Legacy `Kind`/`world`/`rot` fields still deserialize and migrate.
- **`Editor/KimodoConstraintOverlay.cs`** — a Scene-view **Overlay** ("Kimodo Constraints", dockable
  from the overlay menu / `` ` ``) holding the tools, so they are one click away like Unity's own tool
  overlays instead of buried in each inspector: Limb / Aim / Joints / Pose for end-effector keys,
  Joints / Pose / On-Off for pose keys, plus ◀ ▶ to walk keys **in frame order** (moving the playhead
  with them) and `＋` to add one at the playhead. `KimodoEditState.ToolFor(bool)` maps a tool that
  doesn't apply to the other component onto a sensible one, so a shared button can never leave an
  editor with no gizmo.
- **Selecting a key got much cheaper**: `FollowPlayhead` (default on) makes the key at the playhead the
  editable one; clicking a key in the **Timeline window** selects it for Scene editing (`Select` →
  `KimodoEditState.SelectExplicit`, which turns follow off since it is an explicit pick); clicking any
  other key's **pelvis dot in the Scene** switches to it.
- Ghost-mesh transparency defaults to **0.2** on both components (existing scene components keep the
  value they were serialized with — set it by hand or reset the component).

## 7g. Per-kind tools + focused display (2026-07-31, round 3 after user feedback)

- **Tools are now per constraint KIND, stored per kind** (`KimodoEditState.ToolsFor/Tool/SetTool`,
  EditorPrefs key per kind). A waypoint offers **Move / Face**, a hand/foot key **Limb / Aim**, a
  full-body key **Joints / Pose / On-Off**. Joints and Pose were removed from the end-effector toolset
  on the user's call ("unrelevant"): a hand/foot key's pelvis is now moved by giving it a waypoint or
  by ticking Free root, and the out-of-reach label says so. Because the tool is stored per kind,
  selecting a key of another kind (or creating one) switches the toolset with it — the previous
  version kept one global tool, which is why a newly created key showed the wrong tools.
- **`KimodoEditState.ResolveFollow(generator)` is the single place that resolves "the key under the
  playhead"**, called by the overlay and by every editor. Before, each editor decided on its own, so
  the overlay could not tell which KIND was being edited. Ties go to the component already selected,
  so working on hand/foot keys never jumps to a pose key that shares the frame.
- The overlay's **"＋ add a key"** button was removed (the user did not want it); keys are still added
  from the inspectors and the Timeline's track headers.

Corrected in round 4, same day, after more testing — get these right if this area is touched again:
- **NO TEXT IN THE SCENE VIEW.** Every `Handles.Label` is gone from the constraint editors (key
  labels, out-of-reach wording). A grep for `Handles.Label` under `Editor/` must stay empty; wording
  belongs in the overlay and the inspectors. Out of reach is a yellow dotted line, nothing more.
- **Waypoints have no tools.** Their combined gizmo (ground dot moves, arrow tip aims) is live on
  every waypoint at once — that is the part the user was happy with, and splitting it into Move/Face
  modes made it worse. `ToolsFor(Waypoint)` returns empty and the overlay shows a one-line hint
  instead of a tool row.
- **All ghosts are shown**, always; a key is hidden with its own per-key `show` toggle. The
  `showOnlySelected` fields are gone from both components.
- **The skeleton is always drawn in full** for end-effector keys. What narrows to the pinned limbs is
  the **ghost MESH**: `KimodoEndEffectors.GhostChain(limb)` (hand ⇒ Shoulder→Arm→ForeArm→Hand+ends,
  foot ⇒ Hips→Leg→Shin→Foot→toes) feeds `GhostMask(target, skeleton)` into `PoseView.jointActive`,
  reusing the pose keys' skin-weighted fade — so a hand key shows an arm, not a whole extra body.
- **The Timeline's ＋ buttons must publish the selection.** They set `_selKind`/`_selIndex` directly
  and never reached `KimodoEditState`, so the overlay kept the previous key's tools after adding a
  key — the "tools don't change for a new key" report. They now go through `SelectNow(kind, index,
  owner)`. Any new "create a key" path must call `KimodoEditState.Select`.

## 7h. Frozen segments, rebuilt as PARTIAL GENERATION (2026-07-31; server change ⇒ RESTART BRIDGE)

`KimodoTimeline.FreezeEnabled` is **true** again. The parked version generated the whole timeline and
spliced the kept frames back, which the user rejected ("it should just send the second segment, and I
should see just one run on the bridge"). It now really skips frozen content:

- `KimodoTimeline.BuildPlan(fps)` splits the timeline into alternating **blocks** — consecutive frozen
  segments merge into one kept block, consecutive live ones into one request. Freeze the first of two
  segments ⇒ exactly ONE `/generate`, for the second.
- `Runtime/KimodoFrozenGenerate.cs` walks the blocks, chaining one request per live run, and
  assembles the final clip (frozen frames verbatim + the generated runs).
- **Continuity is the whole problem**, and it is solved the way Kimodo solves it for its own
  multi-prompt sequences (`kimodo_model.py::_multiprompt`, the `not is_first_motion` branch), through
  the public constraint API: the frozen block's last **K = 5** frames go in as a `fullbody` constraint
  **plus** a generic `end-effector` one (Kimodo pairs those two — the second is what carries hand/feet
  rotations) over the request's first K frames; everything is translated so the handover frame sits at
  XZ (0,0) and the output is translated back; `first_heading_angle` is the heading of that frame; the
  K lead frames are then **dropped** (the frozen segment owns them). A frozen block that FOLLOWS a run
  gets the mirror treatment — K extra frames appended, constrained to its first K frames, dropped.
  **These boundary constraints ARE the "hidden keys" the user asked for**; they are derived at request
  time from the frozen clip, so `Unfreeze()` (which clears `frozenClip`) removes them with no cleanup.
- Durations are built from FRAME COUNTS as `(frames + 0.5) / fps` — both sides truncate `seconds × fps`,
  so `n/fps` can land a hair under `n`. The assembled length therefore equals `timeline.TotalFrames`.
- User constraints are remapped per run by `KimodoFrozenGenerate.Remap` (frame shift + the same XZ
  origin shift, dropping keys outside the run). Keys inside a frozen range are simply not sent.
- `num_samples` is forced to **1** while anything is frozen (the frozen part is shared, so samples
  could not diverge anyway), and foot contacts are reported as absent on this path — frozen blocks
  carry none, so an accumulated array would not line up with the frames.
- Server: `GenerateRequest` gained `has_first_heading` + `first_heading_angle` (a bool is needed
  because JsonUtility cannot send a null float), passed straight to `model(...)`. The Unity side
  computes the angle with Kimodo's own convention — `atan2(dz, -dx)` over (RightLeg − LeftLeg), see
  `motion_rep/feature_utils.py::compute_heading_angle`; verified numerically against it.
**The two junctions are NOT symmetric — fixed after the user tested [1 frozen | 2 live | 3 frozen]:**
the START works because its constrained frames come BEFORE the kept ones (the run enters the kept range
already snapped onto the frozen pose), while the END's constrained frames come AFTER them, leaving the
last kept frame the only unconstrained frame at a junction. That is what read as "it does not fit the
third segment". Two fixes, which compose:
1. The tail boundary is extended one frame backwards onto the last frame we actually keep — the frozen
   block's opening pose with its root stepped back by one frame of that block's own entry velocity
   (33 ms earlier, so the pose is a fair approximation), so post-processing snaps it.
2. A **two-point motion warp** in `Accumulator.Append`: whatever residual is left at each junction is
   measured against where the run *ought* to start and end (one step outside each frozen block,
   extrapolated from that block's own velocity) and lerped across the whole run, ground position only.
   Junctions then line up exactly with no single-frame step. A correction over 25 cm logs a warning,
   because meeting the frozen block by moving that far can show as sliding.

- Verified in Python: the boundary pair builds into `FullBodyConstraintSet` + `EndEffectorConstraintSet`,
  pins {LeftHand, LeftHandMiddleEnd, LeftFoot, LeftToeBase, …}, survives `crop_move`, lands the handover
  frame exactly on the origin, and is picked up by `postprocess.extract_input_motion_from_constraints`
  (so the snap applies at the boundary). **Not yet Editor-tested end to end by the user.**

## 7i. The generated motion survives compiles (2026-07-31)

User: "each time Unity compiles, it clears the animation and I can't bake — everything starts from
scratch." `KimodoGenerator.Motion` is `[NonSerialized]` (it is generated data, off the wire), so every
domain reload — script compile, entering play mode, restart — threw away a minute of generation.

- `Runtime/KimodoMotionCache.cs` writes it as JSON to **`<project>/Library/KimodoMotionCache/<id>.json`**
  (already gitignored; Unity's own derived-data folder, safe to delete). One file per generator, keyed
  by `[SerializeField, HideInInspector] string motionCacheId` — a GUID minted on first use, which also
  marks the **scene** dirty (`EditorSceneManager.MarkSceneDirty`; `SetDirty` alone does not, and an
  unsaved scene would lose the id and orphan the cache).
- `AdoptMotion` writes it; `Editor/KimodoMotionRestore.cs` (`[InitializeOnLoad]` + `delayCall`, same
  shape as `KimodoBridgeAutoConnect`) restores every generator whose `Motion` is null, and on
  `sceneOpened`. It skips play mode, which has its own object graph.
- **`RebindPreview(bool autoFitRootMotion)`**: a restore passes false. The auto-fit is right after a
  fresh generate, but re-running it on every compile would quietly undo a `rootMotionScale` the user
  had tuned. The serialized value is used instead.
- Loading rejects a short/half-written file (frame and joint counts vs. array lengths) so a stale cache
  can never come back as a broken motion. The generator inspector shows the cache size and a Clear.
- Serialising the motion into the scene was the alternative and was rejected: hundreds of KB of float
  arrays in the scene file, re-dirtied on every Generate, for data that is deliberately throwaway.

## 7j. Starting the server from Unity + installing as a UPM package (2026-07-31)

- **`Editor/KimodoServerLauncher.cs`** runs `<python> -u -m kimodo_bridge --host … --port …
  [--preload …]` so the server can be started from the KimodoBridge inspector instead of a terminal.
  **It launches the interpreter directly, not a shell script** — the script only ever existed to pick
  the venv's interpreter and set `TEXT_ENCODER_DEVICE`, one line of C# each. That is what makes it
  portable (Windows/macOS/Linux differ only in `Scripts/python.exe` vs `bin/python`), removes the
  execution-policy failure mode, and means Stop is a plain `Process.Kill` instead of `taskkill /T`
  chasing a python child. **The server module ships in the package** under `Server~` (Unity ignores
  `~` folders), resolved via `PackageManager.PackageInfo.FindForAssembly(...).resolvedPath` so it works
  from `Packages/` and from the read-only git cache alike; `PYTHONPATH` + the working directory point at
  it, so `-m kimodo_bridge` resolves without the user locating anything. The only setting is the Python
  interpreter (an EditorPref, machine-specific — guessed from venv/.venv/env near the project).
  Three things it has to get right, and did not get for free:
  - **Output arrives on background threads**, where the Unity API is off limits — lines are queued and
    drained on `EditorApplication.update`. Only failures + milestones are logged (a model load prints a
    lot); everything goes to a rolling 400-line buffer the inspector shows.
  - **Failures are classified**, not just reported — and *which* module is missing says which of three
    different things went wrong: `No module named 'kimodo'` → Kimodo is not installed in that
    environment; `'kimodo_bridge'` → the Server folder override is wrong (clear it for the bundled
    copy); anything else → a missing dependency. Plus "address already in use" → a server is already up,
    press Connect. Plain "it exited" would send the user hunting.
  - **A domain reload wipes the `Process` handle**, which would orphan the server. The PID is kept in
    **EditorPrefs** (not SessionState — an orphan must be findable after a full restart too), keyed by
    project, and re-attached on load; `Detached` says the output streams could not be reconnected.
    Stop uses `taskkill /PID <pid> /T /F` because killing PowerShell alone would leave python holding
    the port, and `Process.Kill(entireProcessTree)` is not on Unity's runtime.
  - Being alive ≠ serving, so readiness is `GET /health` polled once a second for up to 5 minutes (a
    preload is slow); on success it Connects the bridge by itself.
- **The Bridge inspector was reorganised** around the three states in the order you meet them — server
  running / connected / model loaded — each one status line with the button that changes it. Everything
  set once (URL, Python, server folder, CPU text encoder) moved behind a **Setup** foldout that opens
  itself while Python is unset or something failed; the server output has its own foldout so a model
  load cannot bury the controls.
- **A server is shared, not owned by a project.** The tracked PID only covers servers *this* project
  started, so a second Unity project saw "Server not running" while one was plainly answering, and
  pressing Start collided on the port. `Start()` now probes `/health` first and adopts whatever is
  already serving the URL; the inspector distinguishes ours (PID) from `external` (online with no PID
  of ours) and offers no Stop for the latter, since it is not ours to stop. A Connect that goes green
  instantly, model included, is the correct outcome of adopting a warm server — not a stale UI.
- **Stopping the server clears the connection UI.** "Connected" is not a live socket — it only means a
  past `/health` answered — so nothing contradicted the green dots when the server went away.
  `KimodoServerLauncher.Stopped` now fires on Stop and on an unexpected exit;
  `KimodoBridgeAutoConnect.OnServerStopped` disconnects every bridge, clears `Wanted` (so auto-reconnect
  does not chase a dead server) and repaints.
- **The server may live on another machine** — it is all HTTP. `IsLocalUrl(url)` decides whether the
  Start/Stop row makes sense at all; a non-local URL shows "Server on another machine" instead of
  buttons that could not work. `AllowRemoteClients` (EditorPref, default **off**) binds `0.0.0.0` so
  another device can reach a locally started server; it warns in the inspector, because the API has no
  authentication.
- **UPM install needs no restructuring.** The repo is a Unity project with the package inside it, and
  Package Manager takes a subfolder directly:
  `https://github.com/Amin-HP/Kimodo-Unity.git?path=/Packages/com.aminhp.kimodobridge` (append `#v0.2.0`
  to pin). Only the package folder is fetched — the sample project does not come with it. Documented in
  both READMEs; releases are tagged `vX.Y.Z` so the pin has something to point at.

## 7k. Generation progress (2026-07-31; server change ⇒ RESTART BRIDGE)

A generate takes about a minute and showed nothing but "Generating…". Kimodo's denoising loop takes a
**`progress_bar` callable that wraps the step iterator** (`kimodo_model.py`: `for i in
progress_bar(indices)`) — a real hook, so this is measured rather than estimated.

- **`_multiprompt` accepts a `progress_bar` and never forwards it** to `_generate` (only the
  single-prompt path in `__call__` does), so passing it to `model(...)` did nothing on the path the
  bridge actually uses — the denoising loop kept its own tqdm and the bar sat at "encoding". It is
  injected by shadowing `model._generate` instead, which is also where per-segment constraint weights
  are applied: **`_generate_hooks(model, weights, bar)` does BOTH in one wrapper**, because two nested
  context managers would each restore `_generate` on exit and the inner one would delete the outer's.
  (It replaced `_per_segment_constraint_weight`.)
- Server: `_PROGRESS` (+ its own lock) and `GET /progress`. `_progress_bar()` returns a generator that
  stands in for tqdm, recording the step as it yields. **It must not take `_MODEL_LOCK`** — `/generate`
  holds that for the whole request, and FastAPI runs sync endpoints in a threadpool, so `/progress`
  answers while generation is still running (verified with a TestClient reading it from another thread).
- **Multi-prompt runs one denoising loop per segment**, so the fraction is
  `(segment + step/steps) / segments`. Segments are counted by counting *loops*, not by looking at the
  phase: the first version tested `phase == "denoising"` to detect a new segment, but the previous loop
  had already moved the phase to "postprocess", so it never advanced past segment 0 and the bar stalled
  at 50 %. Caught by a standalone test before it ever ran against the model.
- The text encoder runs **before** the first loop and on CPU is a large slice of the wall clock, with no
  step count to report. That phase says "encoding the prompt" rather than sitting at 0 % of denoising.
- Unity: `KimodoProgress` DTO + `KimodoClient.GetProgress`, `KimodoGenerator.Progress01`/`ProgressLabel`
  (`-1` = unknown), and `Editor/KimodoProgressPoller.cs` polling twice a second while any generator is
  `Busy`, then `InternalEditorUtility.RepaintAllViews()`. Bars are drawn in the generator inspector and
  in the Timeline toolbar next to the Generate button. An older server without `/progress` leaves
  `Progress01` at -1 and the bar reads "Working…" rather than inventing a number.

## 7l. Errors say what broke (2026-07-31)

A user report of "generation failed, HTTP 500" could not be diagnosed: Starlette answers an unhandled
exception with the plain string "Internal Server Error", so nothing about the cause reached Unity, and
five reconstructed request shapes (plain / first_heading / segment weights / boundary constraints / the
whole frozen-path request) all returned 200 against the live server. So the failure was made
self-reporting instead of guessed at:

- `_describe_exception()` — type, message, and the last frame **inside kimodo/kimodo_bridge**, which is
  the part that says which stage is at fault.
- `@app.exception_handler(Exception)` returns that as JSON `{"detail": …}` (and clears the progress
  state) so nothing can come back as a bodyless 500 again.
- `/generate`'s own handler adds the request shape — segment count, frames, samples, constraint count
  and types, postprocess — since the shape is usually what distinguishes a failing request.
- Response building moved into `_build_response(...)` so a failure there is reported as such rather
  than as "model() failed". **Watch the arguments**: `fps` is computed in `generate()` and had to be
  passed in; the refactor briefly left it as a NameError, caught by the end-to-end run.
- Unity: `DescribeError` now parses `{"detail": …}` and shows that sentence instead of raw JSON.

## 7m. Two small editor fixes (2026-07-31)

- **The position gizmo grew without bound while dragging a hand/foot.** The handle is anchored to the
  live drag value (§7f rule 2) so an IK clamp cannot drag it backwards, but that anchor was only
  released on `EventType.MouseUp` — which the handle itself consumes, and which never arrives at all if
  the mouse comes up over another Scene view or outside one. The stale anchor was then reused as the
  START of the next drag, so drags compounded, the handle marched away from the camera and
  `HandleUtility.GetHandleSize` scaled it up with the distance. **`GUIUtility.hotControl == 0` is the
  authoritative "nothing is being dragged"** and now releases it, with two guards behind that: a
  non-finite anchor (a degenerate IK solve can produce one) and an anchor more than 10 m from the joint
  are both discarded, since a limb reaches under a metre.
- **The Samples slider is gone** from the generator: extra samples cost a full generation each, and
  preview, bake and frozen segments all work on one clip. `numSamples` stays as a `[HideInInspector]`
  field for code that wants a batch, and the request sends `Mathf.Max(1, numSamples)`.

## 7n. The stretching arm — quaternions must stay UNIT (2026-08-01)

§7m stopped the handle from *re-anchoring* to itself, but the user still saw the real bug behind it:
drag a hand and the arm bends off in its own direction and **grows longer and longer**, the gizmo
swelling as the runaway hand carries it away from the camera. Two independent defects, both in the
solve, both now fixed and both verified numerically before the edit:

1. **Nothing kept the rotations unit-length, and the error compounded.** `KimodoFK` places a joint with
   `gpos[parent] + grot[parent] * offset`, and **Unity's `Quaternion * Vector3` scales the vector by
   |q|²** — so a rotation that drifts off the unit sphere does not rotate the skeleton, it STRETCHES
   it. The drift had nowhere to go but up: `Quaternion.Inverse` is a bare conjugate (correct only for a
   unit quaternion), so `localMid = Inverse(gUpNew) * gMidNew` *multiplied* the ancestors' error
   instead of cancelling it, and the effector's `Inverse(parentG) * keepEnd` did it again. Every
   mouse-move event of a drag ran another solve off the previous result, so the exponent grew with the
   square of the number of solves. Simulated in float32 with Unity's conjugate inverse: forearm stretch
   ×1.0027 after 100 solves, ×1.31 after 1 000, ×764 after 5 000, `NaN` shortly after — i.e. fine for a
   nudge, visibly wrong after a few seconds of dragging, exactly what was reported.
   **Fix: `KimodoFK.Unit(q)` / `KimodoFK.Inv(q)`** (normalise; `Inv` normalises *then* conjugates) and
   everything that composes or stores a rotation goes through them — FK on read and on every
   composition, `KimodoIK`'s inputs and outputs, `KimodoEndEffectors.WriteQuat`, `SolveLimbTo`,
   `SetJointGlobalRotation`. `NormalizePose()` renormalises a whole key in `EnsurePose` and before each
   solve, so **poses already saved with drifted quaternions repair themselves on load** rather than
   staying bent (it also keeps what `BuildConstraints` sends the server unit-length).
   **RULE: never trust `Quaternion.Inverse` here, and never store a rotation without normalising it.**
2. **The IK pole was perpendicular to the WRONG axis.** `bendDir` was flattened onto the plane of the
   *current* upper→end axis, then used with the *new* target axis, where `h` and `r` are the legs of a
   right triangle. A `bendDir` still leaning along the new axis makes `|pMidNew - pUp|` longer than the
   upper bone, so the aim rotation is off and FK lands the hand **short of the target** — the limb
   visibly bending its own way, and the next mouse-move solving from an already-wrong pose. Flattening
   onto the NEW axis makes the solve exact in one step: |end − target| went from 0.066 → 0.014 → 0.003 →
   … over six iterations (old) to **0.000 on the first** (new), bone lengths identical either way.

Also: **the ghost mesh is ON by default for hand/foot keys** (`showGhostMesh = true`, matching
`KimodoPoseConstraints`) — a hand on an actual arm is what makes a key readable; the skeleton alone is
not. Because a plain field initialiser only reaches components created from *now on*,
`KimodoEndEffectors` implements `ISerializationCallbackReceiver` with a `settingsVersion` field:
`OnAfterDeserialize` migrates anything saved below the current version. **Bump
`CurrentSettingsVersion` (and add a branch) whenever a default here changes** — that is the only way an
already-authored scene picks it up.

## 8. Known issues / open work

- **Effector HEIGHT is still soft** — a raised foot (e.g. onto a box) often doesn't fully lift: Kimodo is soft +
  needs an in-distribution prompt + the body positioned so it's reachable. Recipe: step-up prompt + a **waypoint** to
  bring the body + raise **Constraint weight** (5–6). Authoring the whole pose (§7e) plus the postprocess
  snap gets much closer, but it is still guidance, not a hard solve.
- **Two-bone IK only** for a dragged limb (upper + mid joints). The shoulder/clavicle and spine do not
  participate, so a big reach looks stiff until you also rotate them in "Rotate joints" mode.
- **Pose ghost** is the Kimodo skeleton (SOMA proportions) — shows the *pose* faithfully but won't pixel-overlay the
  Mixamo mesh. Fine per the user; could scale/retarget the ghost if desired.
- **Foot sliding** (treadmill) — no foot-lock yet; `footContacts` are in the payload → pin planted feet in
  retarget/bake. Not started.
- **Timeline** (§7c) is in use. Remaining ideas: per-segment constraint filtering/colour-coding of keys
  by segment; box-select + multi-key drag; a transition-frames control (`num_transition_frames` is still
  the server default 5, which is also `KimodoFrozenGenerate.LeadFrames`); showing generation progress on
  the track; per-segment sampling overrides (steps/seed).
- **Frozen segments** (§7h): a run that has to travel far to meet a frozen block warps to reach it and
  can slide (there is a >25 cm warning). `num_samples` is forced to 1 while anything is frozen, and foot
  contacts are not reported on that path. Un-frozen-to-frozen junctions are a hard cut in the pose — the
  boundary constraints get it close, but no crossfade is applied.
- **The server launcher (§7j) is untested on macOS/Linux.** It no longer uses any shell, so the code
  path is the same everywhere, but only the Windows path has actually been run.
- **Progress during a frozen-timeline generate** (§7h + §7k) is per request: the bar restarts for each
  live run, and the run number is in the status text next to it rather than folded into one percentage.
- **Motion cache** (§7i) has no size limit and is never swept: one file per generator in
  `Library/KimodoMotionCache/`, replaced on each Generate, orphaned if a character is deleted. Deleting
  the folder is always safe.
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
