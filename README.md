# Kimodo ↔ Unity Bridge

Real-time, interactive bridge between the [NVIDIA Kimodo](https://research.nvidia.com/labs/sil/projects/kimodo/)
text-to-motion diffusion model and **Unity 6**.

Type a prompt → generate human motion → preview it live on any **Humanoid** character → author
**constraints** (root path, hand/foot targets, whole-body poses) → **bake** to an `AnimationClip`.
Sequence several prompts into a whole shot with the **timeline** window.

> Independent wrapper by **AminHP** — **not** affiliated with, sponsored by, or endorsed by NVIDIA.
> Apache-2.0. See [THIRD-PARTY-NOTICES](Packages/com.aminhp.kimodobridge/THIRD-PARTY-NOTICES.md).

---

## How it works

```
Unity Editor (C#) ──HTTP/JSON──►  Bridge server (FastAPI)  ──►  Kimodo model
  components + gizmos                127.0.0.1:8765               diffusion (CUDA) + text encoder (CPU)
  live retarget preview / bake       returns Kimodo coords        model stays resident / cached
```

The server returns **pure Kimodo coordinates** (right-handed, Y-up, +Z forward, metres); Unity does the
conversion and all constraint authoring. The model is generated on CUDA; the ~8B text encoder runs on CPU
so it fits in 8 GB VRAM.

## Install the package

This repository is a Unity *project* (so the package can be developed and tested in place), but the
package itself is `Packages/com.aminhp.kimodobridge` and installs on its own — nothing else here comes
with it. In **Window ▸ Package Manager ▸ + ▸ Install package from git URL**:

```
https://github.com/Amin-HP/Kimodo-Unity.git?path=/Packages/com.aminhp.kimodobridge
```

Pin a release by adding a tag, e.g. `…com.aminhp.kimodobridge#v0.2.4`. You still need the Python
bridge server (below) — the package talks to it over HTTP.

## Quick start

1. **Server** — install NVIDIA Kimodo (Python 3.10). The bridge itself ships with the package
   (`Packages/com.aminhp.kimodobridge/Server~/`), so you can run it by hand with:
   ```bash
   python -m kimodo_bridge        # serves http://127.0.0.1:8765
   ```
   Or let Unity do it: the **KimodoBridge** manager has a **Start** button. The server ships inside the
   package, so the only setting is which **Python** to use — an environment with Kimodo installed. It
   runs the interpreter directly, so it works on Windows, macOS and Linux alike, reports what went wrong
   if it cannot start, and connects by itself when it is up.
2. **Unity** — open this project (Unity 6, URP), or install the package into your own. Then:
   - `GameObject ▸ Kimodo ▸ Bridge Manager` → **Connect**.
   - Select a **Humanoid** character → `GameObject ▸ Kimodo ▸ Set Up Selected Character`.
   - On **KimodoGenerator**: prompt → **Generate** → preview → **Bake to AnimationClip**.
   - For a multi-prompt shot: `Window ▸ Kimodo ▸ Timeline`.

Full usage, constraints, and the runtime API are in the
[package README](Packages/com.aminhp.kimodobridge/README.md).

## Features

- **Text → motion** on a local Kimodo server, retargeted onto any Unity Humanoid (Mecanim muscle space).
- **Live preview** (play / scrub) and **bake** to a reusable humanoid `.anim` (in-place or travelling).
- **Timeline** (`Window ▸ Kimodo ▸ Timeline`) — sequence a shot as ordered **prompt segments**, each with
  its own duration, in a **Kimodo Timeline** asset you assign to the character. One frame axis carries the
  segments plus every constraint key (waypoints / effectors / pose keys): drag a segment edge to retime,
  drag keys to move them, split / duplicate / reorder, and Generate + Bake from the same window.
  Per-segment **constraint weight** lets one beat pin harder than the next, and **❄ freezing** a
  segment keeps it exactly as generated — Generate then skips it and only requests the rest.
- **Constraints**, authored with Scene-view gizmos and sent as soft diffusion guidance:
  - **Waypoints** — ground root path (X/Z) + per-waypoint facing.
  - **End-Effectors** — per-frame hand / foot keyframes, like the demo's: a whole-body pose of which only
    the ticked limbs (and the root) are pinned, one key per frame. The ghost shows the constrained joints
    in red with an axes gizmo on each effector.
  - **Full-Body pose keys** — whole-body poses with a ghost skeleton + optional transparent **ghost mesh**,
    and a pelvis handle to move a pose (including **height**, e.g. onto a box).
  - The editing tools (drag a limb, aim it, rotate joints, move the pose) sit in a dockable **Scene-view
    overlay**, and the key you edit follows the playhead — or a click on its Timeline key.
- **Auto root-motion scale** that absorbs per-character unit/scale differences.
- **The server can live on another machine** — it is plain HTTP; point Server URL at the box with the
  GPU (run it there with `--host 0.0.0.0`).
- **Progress while generating** — the server's real position in its denoising loop, shown in the
  generator and the timeline.

## Repo layout

| Path | What |
|------|------|
| `Packages/com.aminhp.kimodobridge/` | The Unity package (runtime + editor), including the Python bridge under `Server~/`. The deliverable — this is what the git URL above installs. |
| `Assets/` | A minimal URP sample project. The test character is not committed. |

## Requirements

- **Unity 6000.0+** (URP).
- A machine that can run **NVIDIA Kimodo** (CUDA GPU; ~8 GB VRAM works with the CPU text encoder).
- A character with **Rig → Animation Type → Humanoid**.

## License

Apache-2.0 (this repo's original code). NVIDIA Kimodo is separate, under its own Apache-2.0 license, and
is **not** redistributed here — install it yourself.
