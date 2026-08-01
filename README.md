# Kimodo Bridge for Unity

Type a prompt, get human motion on your character.

This is a Unity front end for [NVIDIA Kimodo](https://research.nvidia.com/labs/sil/projects/kimodo/), a
text-to-motion diffusion model: generate from a prompt, preview it live on any **Humanoid** rig, shape it
with Scene-view **constraints**, sequence a whole shot on a **timeline**, and **bake** the result to an
ordinary `AnimationClip` that needs none of this at runtime.

> Independent wrapper by **AminHP** — **not** affiliated with, sponsored by, or endorsed by NVIDIA.
> Apache-2.0. See [THIRD-PARTY-NOTICES](Packages/com.aminhp.kimodobridge/THIRD-PARTY-NOTICES.md).

---

## Demo

Every feature below, in a couple of minutes — starting the server, a prompt, constraints, the timeline,
freezing, and baking.

[![Kimodo Bridge for Unity — text to motion, live in the editor](https://img.youtube.com/vi/GlcN4yTN5CE/maxresdefault.jpg)](https://youtu.be/GlcN4yTN5CE)

---

## Install

**Window ▸ Package Manager ▸ + ▸ Install package from git URL:**

```
https://github.com/Amin-HP/Kimodo-Unity.git?path=/Packages/com.aminhp.kimodobridge
```

Append a tag to pin a version, e.g. `…com.aminhp.kimodobridge#v0.2.8`. Only the package folder is
fetched — the Unity project in this repository is just where it is developed.

You also need **Python 3.10 with [Kimodo](https://github.com/nv-tlabs/kimodo) installed** and a CUDA GPU
(~8 GB VRAM is enough). The bridge server that wraps Kimodo ships inside the package.

## Getting started

1. **GameObject ▸ Kimodo ▸ Bridge Manager** → in **Setup**, point **Python** at the environment that has
   Kimodo → **Start**. It connects on its own.
2. Select a **Humanoid** character → **GameObject ▸ Kimodo ▸ Set Up Selected Character**.
3. Type a prompt, press **Generate**, watch the progress bar, scrub the preview.
4. **Bake to AnimationClip** when you like it.

The server can also be run by hand (`python -m kimodo_bridge`) or **on another machine** — it is plain HTTP.

**→ The full manual is the [package README](Packages/com.aminhp.kimodobridge/README.md)**: constraints,
the timeline, freezing, baking, the runtime API and troubleshooting.

## What you get

- **Text → motion**, retargeted onto any Unity Humanoid through Mecanim muscle space.
- **Live preview** (play / scrub) and **bake** to a reusable `.anim`, in place or travelling, with the
  root-motion scale measured automatically per character.
- **Constraints**, authored with Scene-view gizmos and a dockable tool overlay:
  - **Waypoints** — the ground path the pelvis follows, with per-point facing.
  - **End-Effectors** — hand / foot keyframes; drag a limb and it bends to reach.
  - **Full-Body poses** — pose the whole rig, or switch individual joints off.
  - Each key is shown as a transparent **ghost of your own model**, faded down to the part of the body
    the key is about, so you place a hand on an arm rather than on a bare skeleton.
- **Timeline** — sequence a shot as ordered prompt segments on one frame axis carrying every constraint
  key, give a beat its own constraint strength, and **❄ freeze** the segments you are happy with so
  Generate skips them entirely and only regenerates the rest.
- **Generation progress** read from the model's own denoising loop, not guessed from elapsed time.
- **Survives recompiles** — the generated motion is cached, so a script change no longer costs you a
  minute of regenerating.

## Repository layout

| Path | What it is |
|---|---|
| `Packages/com.aminhp.kimodobridge/` | **The package** — runtime + editor code, and the Python bridge server under `Server~/`. This is what the git URL installs. |
| `Assets/` | A minimal URP project used to develop and test the package. The test character is not committed. |

## Requirements

- **Unity 6000.0+**, and a character with *Rig ▸ Animation Type ▸ **Humanoid***.
- **Python 3.10** with NVIDIA Kimodo installed (`pip install -e .` in its repo).
- A **CUDA GPU**; ~8 GB VRAM works with the text encoder on the CPU (the default).

## License

Apache-2.0 for this repository's own code. NVIDIA Kimodo is separate, under its own Apache-2.0 license,
and is **not** redistributed here — install it yourself.

## Support

If this saved you some time, you can support the work here: **[Buy me a coffee](https://buymeacoffee.com/aminhp)**.
