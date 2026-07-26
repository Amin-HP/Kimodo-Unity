# Kimodo ↔ Unity Bridge — Project Handoff

> **Purpose:** single reference to continue this project in a new chat. Point the assistant here first
> (“read `Packages/com.aminhp.kimodobridge/HANDOFF.md`”). Persistent memory also exists at
> `C:\Users\mahag\.claude\projects\h--kimodo\memory\` (`kimodo-unity-bridge.md` — detailed running log,
> `kimodo-setup.md` — how Kimodo is installed).

Last updated: 2026-07-23. Owner/brand: **AminHP** (independent wrapper — *not* affiliated with NVIDIA).

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
   - A legacy all-in-one window still exists: `Window ▸ Kimodo ▸ Bridge` (older, superseded by components).

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
- `server.py` — FastAPI app. `GET /health`, `GET /models`, `POST /load_model`, `POST /generate`.
  `_get_or_load_model` caches by resolved short-key (MUST reuse — reload re-loads the ~8B text encoder).
  Constraint machinery (see §7). Launcher `..\run_bridge.ps1`. `__main__.py`, `__init__.py`.

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
- `KimodoMotionPlayer.cs` — runtime component to play a `KimodoMotion` from code.
- `KimodoGhost.shader` — `"Kimodo/GhostMesh"`, URP transparent fresnel used by the pose ghost-mesh preview.
- **Components:** `KimodoBridge.cs` (manager), `KimodoGenerator.cs` (per-character generate+preview+bake state),
  `KimodoEffectors.cs` (hand/foot IK targets), `KimodoWaypoints.cs` (root path + facing),
  `KimodoPoseConstraints.cs` (whole-body pose keys).

**Editor**
- `KimodoBaker.cs` — bake `KimodoMotion` → Humanoid `AnimationClip` (RootT/RootQ + muscle curves).
- `KimodoBridgeEditor.cs`, `KimodoGeneratorEditor.cs`, `KimodoEffectorsEditor.cs`, `KimodoWaypointsEditor.cs`,
  `KimodoPoseConstraintsEditor.cs` — custom inspectors + Scene gizmos.
- `KimodoPoseGhosts.cs` — manages the transparent ghost-mesh clones for the pose-constraint editor (see §7).
- `KimodoMenu.cs` — the `GameObject ▸ Kimodo ▸ …` menu items.
- `KimodoBridgeWindow.cs` — **legacy** all-in-one window (still compiles).

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
  `global_root_heading`). Editor draws the pelvis path + waypoint discs (ground Y, radius, angle). **Works well** —
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
  - **Ghost mesh** (added 2026-07-26, `showGhostMesh` toggle on the component; editor-only): for each *shown*
    key it keeps a **transparent clone** of the target character posed at that key via the real retarget path, so
    you see the actual model (skin/mesh renderers), not just the skeleton, as you edit. `Editor/KimodoPoseGhosts.cs`
    manages the clones (HideAndDontSave, one `KimodoPlayer` each → `KimodoPlayer.PoseFromLocal`, torn down on
    deselect/regeneration; orphan-swept after domain reload). Renders with `Runtime/KimodoGhost.shader`
    (`"Kimodo/GhostMesh"`, URP transparent fresnel). Look is **fixed white ~0.51 alpha** (not user-editable — set in
    `KimodoPoseGhosts`, only an on/off toggle in the inspector). Clones sit exactly over the ghost skeleton because
    both use the same retarget + `rootMotionScale`.

---

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
- **Timeline** — a Unity Timeline track to sequence prompt segments + per-frame constraints is the big unstarted
  feature the user wants eventually.
- **Per-joint constraint intensity is NOT supported by the model.** `create_conditions` writes constrained joints
  into a **boolean** `motion_mask`; the only strength dial is the single global `cfg_weight[1]` (`constraintWeight`),
  applied uniformly. "More upper-body, less lower-body" today = **constrain only the joints you want** (partial key
  vs. full 23-joint fullbody). Graded weights would need changing the mask to a float weight folded into the
  diffusion guidance (model-side change) — not started.
- **Legacy `KimodoBridgeWindow`** can be removed once the component workflow is fully settled.
- Model switching loads a second model (its own text encoder) — heavy; user stays on SOMA.

## Reference
- Kimodo constraints: `H:\kimodo\kimodo\kimodo\constraints.py`; motion rep + `create_conditions`:
  `kimodo\motion_rep\reps\kimodo_motionrep.py`; demo constraint code: `kimodo\demo\generation.py`, `kimodo\viz\`.
- Docs: `https://research.nvidia.com/labs/sil/projects/kimodo/docs/`.
