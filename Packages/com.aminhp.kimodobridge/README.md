# Kimodo Bridge (Unity)

Real-time, interactive bridge between the [Kimodo](https://research.nvidia.com/labs/sil/projects/kimodo/)
text-to-motion model and **Unity 6**.

Generate human motion from a text prompt, preview it live on any **Humanoid** character, author
**constraints** (root path, hand/foot targets, whole-body poses), and **bake** to an `AnimationClip`.

> Independent wrapper — **not** affiliated with or endorsed by NVIDIA. Apache-2.0.

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

## 2. Set up the character (component workflow)

Everything is driven by components you add from the **GameObject ▸ Kimodo** menu:

1. `GameObject ▸ Kimodo ▸ Bridge Manager` — creates the **KimodoBridge** manager. Press **Connect**
   (this preloads the model).
2. Select a **Humanoid** character (Rig → Animation Type → **Humanoid**), then
   `GameObject ▸ Kimodo ▸ Set Up Selected Character` — adds **KimodoGenerator** (+ effectors) and wires
   it to the bridge.
3. On **KimodoGenerator**: enter a prompt + duration → **Generate** → play / scrub the preview →
   **Bake to AnimationClip** to save a reusable humanoid `.anim`.

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
   | **Effectors** | The `KimodoEffectors` hand/foot targets, coloured per limb. |
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

**Per-segment constraint weight:** a segment can pin harder or looser than its neighbours — tick *Own
constraint weight* in the panel (the block shows a `w5` badge). Kimodo generates a multi-prompt sequence
one segment at a time, so the guidance strength can differ per segment. **Requires the bridge server from
this repo** (it sends `segment_cfg_weights`); restart the server after updating it.

## 4. Constraints (optional)

Add any of these to the character to guide generation; each draws editable gizmos in the Scene view:

- **KimodoWaypoints** — a ground **root path** (X/Z) with a rotatable facing arrow per waypoint.
- **KimodoEffectors** — per-frame **hand / foot targets** (full-body IK reaches the target, then that
  pose is sent as a constraint).
- **KimodoPoseConstraints** — **whole-body pose** keyframes. Each shown key draws a ghost skeleton (and,
  optionally, a transparent **ghost mesh** of the model) at its frame; select a key, rotate joints, and
  drag the pelvis handle to move the whole pose — including **height**, e.g. to place it on a box.

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
- **Frozen timeline segments** — keep a segment exactly as generated and only regenerate the rest.
  Implemented but parked behind `KimodoTimeline.FreezeEnabled`.
- **Model switching UI** (SOMA is the default; other models are heavier — a second text encoder).
