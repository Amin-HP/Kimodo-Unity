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

## 3. Constraints (optional)

Add any of these to the character to guide generation; each draws editable gizmos in the Scene view:

- **KimodoWaypoints** — a ground **root path** (X/Z) with a rotatable facing arrow per waypoint.
- **KimodoEffectors** — per-frame **hand / foot targets** (full-body IK reaches the target, then that
  pose is sent as a constraint).
- **KimodoPoseConstraints** — **whole-body pose** keyframes. Each shown key draws a ghost skeleton (and,
  optionally, a transparent **ghost mesh** of the model) at its frame; select a key, rotate joints, and
  drag the pelvis handle to move the whole pose — including **height**, e.g. to place it on a box.

Constraints are *soft* diffusion guidance; raise **Constraint weight** on the generator if a target is
under-enforced.

## 4. Use it from code (runtime)

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
- **Timeline track** for sequencing prompt segments and per-frame constraints.
- **Model switching UI** (SOMA is the default; other models are heavier — a second text encoder).
