# Kimodo Bridge (Unity)

Real-time, interactive bridge between the [Kimodo](https://research.nvidia.com/labs/sil/projects/kimodo/)
text-to-motion model and **Unity 6**.

Generate human motion from a text prompt, preview it live on any **Humanoid** character, author
**constraints** (root path, hand/foot targets, whole-body poses), and **bake** to an `AnimationClip`.

> Independent wrapper — **not** affiliated with or endorsed by NVIDIA. Apache-2.0.

## 0. Install

**Window ▸ Package Manager ▸ + ▸ Install package from git URL**:

```
https://github.com/Amin-HP/Kimodo-Unity.git?path=/Packages/com.aminhp.kimodobridge
```

Add `#v0.2.2` (or any tag) to pin a version. The package is self-contained; the Unity project in that
repository is only where it is developed.

## 1. Start the bridge server

Unity talks to a small local server that wraps the Kimodo model.

```powershell
# in the Kimodo repo
.\run_bridge.ps1              # serves http://127.0.0.1:8765
```

Add `-Preload soma` to load the model at startup (otherwise it loads on the first generate). On an
8 GB-VRAM machine it runs the diffusion model on CUDA and the ~8B text encoder on CPU
(`TEXT_ENCODER_DEVICE=cpu`), so a generation takes tens of seconds. **Restart the server after any
server change.**

**Or start it from Unity — one setting.** The server ships inside this package, so the KimodoBridge
manager only needs to know which **Python** to use: an environment that has Kimodo installed
(`venv/Scripts/python.exe` on Windows, `venv/bin/python` on macOS and Linux). It is guessed from
virtual environments near the project, and remembered per machine rather than in the scene. Press
**Start** and it connects by itself once the server answers.

It runs the interpreter directly (`python -m kimodo_bridge`), not a shell script, so the same button
works on every platform. If it cannot start, it says why rather than just failing: Kimodo missing from
that environment, the port already in use, a wrong interpreter. The server's own output is under
*Server output*. It keeps running across recompiles (it is re-found by its process id), so **Stop** is
how you end it.

## 2. Set up the character (component workflow)

Everything is driven by components you add from the **GameObject ▸ Kimodo** menu:

1. `GameObject ▸ Kimodo ▸ Bridge Manager` — creates the **KimodoBridge** manager. Press **Connect**
   (this preloads the model).
2. Select a **Humanoid** character (Rig → Animation Type → **Humanoid**), then
   `GameObject ▸ Kimodo ▸ Set Up Selected Character` — adds **KimodoGenerator** (+ effectors) and wires
   it to the bridge.
3. On **KimodoGenerator**: enter a prompt + duration → **Generate** → play / scrub the preview →
   **Bake to AnimationClip** to save a reusable humanoid `.anim`.

While it generates, a **progress bar** shows where the model has got to — in the generator and in the
Timeline toolbar. It is the server's real position in its denoising loop (per segment, for a
multi-prompt shot), not a guess from elapsed time; encoding the prompt is called out separately because
on a CPU text encoder it takes a while and has nothing to count.

The generated motion is **kept across script compiles, play mode and Unity restarts** — it is cached in
`Library/KimodoMotionCache/`, so you do not have to regenerate (or bake early) just because Unity
recompiled. Save the scene once so the cache can be found again after a restart. The generator shows
its size, with a **Clear** next to it.

## 3. Timeline — sequencing a whole shot (optional)

A single prompt gets you one beat. To sequence several, use the timeline: **`Window ▸ Kimodo ▸ Timeline`**
(or **Open ▸** next to the Timeline field on the generator).

1. With the character selected, press **New…** next to *Timeline file* to create a **Kimodo Timeline**
   asset (also available from `Assets ▸ Create ▸ Kimodo ▸ Timeline`). It is seeded from the generator's
   current prompt, and assigning it takes over the prompt + duration fields.
2. Add **segments** — each is one sentence with its own duration. They compose into Kimodo's multi-prompt
   form (`"A person walks. A person waves."` with `"2 3"` seconds), which the model generates as a
   sequence with transitions.
3. Everything is laid out on one **frame axis**:

   | Track | What it shows |
   |-------|---------------|
   | **Prompt** | The segments. Drag a block's right edge to retime it; right-click to split at the playhead, duplicate or delete. |
   | **Waypoints** | The `KimodoWaypoints` keys (root path). |
   | **Effectors** | The `KimodoEndEffectors` hand/foot keys, coloured per limb (`LF+RH` when a key pins several). |
   | **Poses** | The `KimodoPoseConstraints` whole-body keys. |

   Drag any key sideways to move it to another frame; `＋` on a track header adds one at the playhead
   (adding the component first if the character doesn't have it). The **left panel** edits whatever is
   selected — including a pose key's prompt and **Generate pose**.
4. **Generate** and **Bake** are in the window's toolbar, so you never have to leave it.

**Shortcuts:** `Space` play/pause · `Delete` remove the selection · `←`/`→` step a frame (`Shift` = 10) ·
`Home`/`End` jump to the ends · `F` look at the selected key in the Scene · `Esc` deselect.

The constraint keys stay on the character's components (that is where their world positions and Scene
gizmos live) — the window is a second view onto the same data, so the inspectors and the timeline always
agree. **Take ▸ Save/Load** snapshots those keys into the timeline asset when you want one file to hold
the whole shot.

**❄ Freeze a segment** to lock it: its frames are captured and Generate stops requesting it
altogether — only the live runs between frozen segments are sent, so freezing the first of two
segments means one request instead of two. The run after a frozen segment is told where and how that
segment ended (its last frames go along as a boundary constraint, with the matching start heading), so
it carries on from there instead of restarting at the origin; unfreezing drops that automatically.
A frozen segment's duration is locked to the frames it kept, and while anything is frozen Generate
returns a single sample. **Requires the bridge server from this repo** (it sends `first_heading_angle`).

**Per-segment constraint weight:** a segment can pin harder or looser than its neighbours — tick *Own
constraint weight* in the panel (the block shows a `w5` badge). Kimodo generates a multi-prompt sequence
one segment at a time, so the guidance strength can differ per segment. **Requires the bridge server from
this repo** (it sends `segment_cfg_weights`); restart the server after updating it.

## 4. Constraints (optional)

Add any of these to the character to guide generation; each draws editable gizmos in the Scene view:

- **KimodoWaypoints** — a ground **root path** (X/Z) with a rotatable facing arrow per waypoint.
- **KimodoEndEffectors** — per-frame **hand / foot keyframes**, authored the way Kimodo's own demo does it.
  A key is a whole-body pose of which only the limbs you tick (`LF` `RF` `LH` `RH`) — and the root — are
  pinned, so several limbs share **one key per frame**. The Scene view shows the pose as a ghost skeleton
  with the constrained joints in **red** and an axes gizmo on each hand/foot for the rotation that is
  constrained too. Tick **Free root** on a key to leave the pose's height and facing free, so the body
  can adapt to reach.
- **KimodoPoseConstraints** — **whole-body (Full-Body) pose** keyframes. Each shown key draws a ghost
  skeleton (and, optionally, a transparent **ghost mesh** of the model) at its frame; rotate joints, move
  the whole pose — including **height**, e.g. onto a box — or switch individual joints off.

### The editing tools

They live in the Scene view's **Kimodo Constraints overlay** — dock or hide it like any Unity overlay
(the ⋮⋮ overlay menu, or the `` ` `` key). The overlay shows the tools for the kind of key you have
selected, and nothing else:

| Key | Tools |
|-----|-------|
| **2D Root (waypoint)** | None — its gizmo already does both jobs: drag the dot to move it, the arrow tip to aim it. |
| **End-Effectors** | **Limb** — drag a hand/foot, the limb bends to reach it (IK; out of reach draws a dotted line, never silently corrected) · **Aim** — rotate it, since Kimodo constrains the effector's rotation too |
| **Full-Body** | **Joints** — the demo's editing mode, click a joint then rotate it · **Pose** — drag the pelvis, height included · **On/Off** — click joints to include or exclude them |

Every key draws its skeleton (hide one with its own **Show** toggle), with the constrained joints in
red. The **ghost mesh** is what narrows down: a hand key ghosts the arm, a foot key the lower body on
that side, so the torso and head stay out of the shot. Nothing writes text into the Scene view.

Picking the key to edit: leave **⏱ follow the playhead** on and the key at the current frame is the one
you edit — so scrubbing, or clicking a key in the **Timeline window**, selects it. `◀ ▶` in the overlay
walk the keys in frame order.

Constraints are *soft* diffusion guidance; raise **Constraint weight** on the generator if a target is
under-enforced.

## 5. Use it from code (runtime)

Add a `KimodoMotionPlayer` to a Humanoid character and drive it:

```csharp
using AminHP.KimodoBridge;

var player = character.GetComponent<KimodoMotionPlayer>();
var client = new KimodoClient("http://127.0.0.1:8765");
client.Generate(
    new KimodoGenerateRequest { prompt = "wave hello", duration = "3" },
    (ok, motion, err) => { if (ok) player.Play(motion); else Debug.LogError(err); });
```

## Coordinate system

The server returns pure Kimodo coordinates (right-handed, Y-up, +Z forward, metres). The conversion to
Unity's left-handed space (negate X) happens in `KimodoCoords`; the affine world↔Kimodo mapping used by
the constraint authoring lives in `KimodoRootMap`.

## Roadmap

- **Foot-lock / anti-sliding** using the `footContacts` already in the payload.
- **Model switching UI** (SOMA is the default; other models are heavier — a second text encoder).
