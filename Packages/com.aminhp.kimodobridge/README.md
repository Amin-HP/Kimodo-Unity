# Kimodo Bridge for Unity

Generate human motion from a text prompt, preview it live on any **Humanoid** character, shape it with
Scene-view **constraints**, sequence a whole shot on a **timeline**, and **bake** the result to a normal
`AnimationClip`.

It is a Unity front end for the [Kimodo](https://research.nvidia.com/labs/sil/projects/kimodo/)
text-to-motion diffusion model, which runs in Python next to Unity (or on another machine) and is
reached over plain HTTP.

> Independent wrapper by AminHP — **not** affiliated with, sponsored by, or endorsed by NVIDIA.
> Apache-2.0. See [THIRD-PARTY-NOTICES](THIRD-PARTY-NOTICES.md).

**▶ [Watch the demo](https://youtu.be/GlcN4yTN5CE)** — the whole workflow, from starting the server to
baking a clip, in a couple of minutes.

---

## Contents

1. [Requirements](#1-requirements)
2. [Install](#2-install)
3. [Start the server](#3-start-the-server)
4. [Your first motion](#4-your-first-motion)
5. [Shaping the motion — constraints](#5-shaping-the-motion--constraints)
6. [Sequencing a shot — the Timeline](#6-sequencing-a-shot--the-timeline)
7. [Baking to an AnimationClip](#7-baking-to-an-animationclip)
8. [Using it from code](#8-using-it-from-code)
9. [Troubleshooting](#9-troubleshooting)
10. [How it works](#10-how-it-works)

---

## 1. Requirements

| | |
|---|---|
| **Unity** | 6000.0 or newer. The character needs *Rig ▸ Animation Type ▸ **Humanoid*** — any humanoid rig works, the motion is retargeted through Mecanim. |
| **Python** | 3.10, with [NVIDIA Kimodo](https://github.com/nv-tlabs/kimodo) installed (`pip install -e .` in its repo). This package ships the small bridge server that wraps it, but not Kimodo itself. |
| **GPU** | CUDA. About 8 GB of VRAM is enough with the text encoder on the CPU (the default) — a generation then takes tens of seconds. |

The server does **not** have to be on the machine running Unity — see
[Running it on another machine](#running-it-on-another-machine).

## 2. Install

**Window ▸ Package Manager ▸ + ▸ Install package from git URL:**

```
https://github.com/Amin-HP/Kimodo-Unity.git?path=/Packages/com.aminhp.kimodobridge
```

Append a tag to pin a version, e.g. `…com.aminhp.kimodobridge#v0.2.8`.

Only the package is fetched; the Unity project in that repository is just where it is developed.

## 3. Start the server

Add the manager once per scene: **GameObject ▸ Kimodo ▸ Bridge Manager**. Its inspector is the whole
server story, in the order things happen:

```
● Server running (PID 12345)                    [ Stop ]
● Connected                                     [ Disconnect ]
● soma loaded                        [ SOMA ✓ ▾ ]
▸ Setup
▸ Server output
```

### From Unity (recommended)

Open **Setup** and set **Python** to the interpreter of the environment that has Kimodo installed —
`venv/Scripts/python.exe` on Windows, `venv/bin/python` on macOS and Linux. It is guessed from virtual
environments near the project, and remembered per machine (not saved in the scene). Then press
**Start**.

That is the only setting. The server itself ships inside this package, and Unity runs the interpreter
directly, so the same button works on every platform. It connects by itself once the server answers.

If it cannot start, it says which of the usual causes it hit — Kimodo missing from that environment or a
wrong interpreter — and the server's own output is under **Server output**. The server keeps running
across script recompiles (it is re-found by its process id), so **Stop** is how you end it; stopping also
drops the connection so nothing shows green against a server that is gone.

**One server serves everything.** It is a normal HTTP service on port 8765, not something owned by a
project, so a server started by another Unity project or in a terminal is used as-is: **Start** notices
one is already answering and connects to it rather than launching a second on the same port, and the
status reads *Server running (started outside this project)* — with no Stop, since it is not this
project's to stop. Connect can then come back instantly, model and all, because that server has been up
for a while and already has the model loaded. That is working correctly, not a cached-looking lie.

Two options worth knowing, both in **Setup**:

- **Text encoder on CPU** (on by default) keeps the ~8B encoder off the GPU so the diffusion model fits
  in 8 GB. Turn it off on a larger card for faster prompts.
- **Reachable from other devices** binds the server to `0.0.0.0` — see below.

### By hand

```bash
python -m kimodo_bridge                 # serves http://127.0.0.1:8765
python -m kimodo_bridge --preload soma  # load the model at startup instead of on the first generate
```

Run it from this package's `Server~` folder, or add that folder to `PYTHONPATH`. Set
`TEXT_ENCODER_DEVICE=cpu` for the 8 GB case. Then press **Connect** in Unity.

### Running it on another machine

Useful when the GPU lives elsewhere, or a standalone device should drive it.

1. **On the machine with the GPU:** start the server so it listens beyond localhost —
   `python -m kimodo_bridge --host 0.0.0.0`, or tick **Reachable from other devices** if you start it
   from a Unity there. Allow the port (8765) through its firewall.
2. **On the other machine:** put that address in **Server URL** (e.g. `http://192.168.1.50:8765`) and
   press **Connect**. Start/Stop disappear — there is nothing local to start — and everything else
   behaves identically.

> **The API has no authentication.** Anything that can reach the port can drive the model. Only open it
> on a network you trust.

## 4. Your first motion

1. Select a **Humanoid** character in the scene.
2. **GameObject ▸ Kimodo ▸ Set Up Selected Character** — adds **KimodoGenerator** and
   **KimodoEndEffectors**, and wires them to the bridge.
3. On the generator: type a prompt (`A person walks forward and waves.`) and a duration in seconds.
4. **Generate.** A progress bar shows where the model actually is — it reports the server's position in
   its denoising loop, per segment, rather than guessing from elapsed time. (Encoding the prompt is
   called out separately: on a CPU text encoder it takes a while and has no steps to count.)
5. Play or scrub the preview on the character.

**Periods separate segments.** `"A person walks. A person waves."` with duration `"2 3"` is two beats of
2 s and 3 s, generated as one sequence with a transition. For more than a couple of beats, use the
[Timeline](#6-sequencing-a-shot--the-timeline).

The generated motion **survives script compiles, play mode and Unity restarts** — it is cached under
`Library/KimodoMotionCache/`, so a recompile no longer costs you a minute of regenerating. Save the scene
once so the cache can be found again after a restart. The generator shows its size with a **Clear**
button.

## 5. Shaping the motion — constraints

A prompt decides *what* happens; constraints decide *where* and *how*. They are **soft guidance**: the
model is steered toward them and a post-process snaps the pinned frames onto them, but an impossible or
out-of-distribution request will still be approximated. If something is under-enforced, raise
**Constraint weight** on the generator (3 → 5–6) and make sure the prompt describes the same thing.

Add these from **Add Component** or the **GameObject ▸ Kimodo** menu:

| Component | What it pins | Shown as |
|---|---|---|
| **KimodoWaypoints** | The ground path the pelvis travels through, and optionally the facing at each point. | A dot per waypoint with an arrow, plus the character's current path. |
| **KimodoEndEffectors** | Hand / foot keyframes. A key is a whole-body pose of which only the limbs you tick (`LF` `RF` `LH` `RH`) — and the root — are pinned. Several limbs share **one key per frame**. | A ghost skeleton with the constrained joints in **red** and an axes gizmo on each hand/foot (its rotation is constrained too), plus a transparent ghost of your model. |
| **KimodoPoseConstraints** | Whole-body poses. Individual joints can be switched off so only part of the body is pinned. | The same: a ghost skeleton plus a transparent ghost of your model. |

Three per-key options worth knowing:

- **Free root** (hand/foot keys) — leave the pose's height and facing free so the body can adapt to
  reach, instead of pinning it exactly where you posed it.
- **Show** — hide an individual key's ghost when the Scene gets busy.
- **Show ghost mesh** — the transparent model at each key's pose, **on by default** on both
  components, with a transparency slider. It is what makes a key readable: a hand key fades the ghost
  down to the arm it belongs to (a foot key, to the lower body), so you place the hand on an actual
  arm rather than on a bare skeleton. Turn it off if the Scene gets crowded.

### The editing tools

They live in the Scene view's **Kimodo Constraints** overlay — dock, move or hide it like any Unity
overlay (the ⋮⋮ overlay menu, or the `` ` `` key). It shows the tools for the kind of key you have
selected, and nothing else:

| Selected key | Tools |
|---|---|
| **2D Root** (waypoint) | *None* — its gizmo does both jobs: drag the dot to move it, drag the arrow tip to aim it. |
| **End-Effectors** | **Limb** — drag a hand or foot and the limb bends to reach it. **Aim** — rotate it. |
| **Full-Body** | **Joints** — click a joint, then rotate it. **Pose** — drag the pelvis to move the whole pose, height included. **On/Off** — click joints to include or exclude them. |

**Picking the key to edit** is mostly automatic: with **⏱ follow the playhead** on, the key at the
current frame is the editable one — so scrubbing, or clicking a key in the Timeline, selects it. `◀ ▶`
in the overlay walk the keys in frame order, and clicking another key's ghost switches to it.

If a limb cannot reach where you dragged it, a dotted line shows how far short it is. Nothing is
silently corrected: move the pose, add a waypoint to bring the body closer, or tick **Free root**.

## 6. Sequencing a shot — the Timeline

**Window ▸ Kimodo ▸ Timeline** (or **Open ▸** next to the *Timeline* field on the generator).

1. With the character selected, press **New…** to create a **Kimodo Timeline** asset. Assigning it takes
   over the generator's prompt and duration fields.
2. Add **segments** — one sentence each, with its own duration.
3. Everything lands on one frame axis:

   | Track | Contents |
   |---|---|
   | **Prompt** | The segments. Drag a block's right edge to retime; right-click to split at the playhead, duplicate or delete. |
   | **Waypoints** | `KimodoWaypoints` keys. |
   | **End-Effectors** | `KimodoEndEffectors` keys, coloured per limb (`LF+RH` when one key pins several). |
   | **Poses** | `KimodoPoseConstraints` keys. |

   Drag keys sideways to retime them; `＋` on a track header adds one at the playhead, adding the
   component first if the character does not have it. The left panel edits whatever is selected.
4. **Generate** and **Bake** are in the window's toolbar.

**Shortcuts** — `Space` play/pause · `Delete` remove selection · `←`/`→` step a frame (`Shift` = 10) ·
`Home`/`End` jump to the ends · `F` look at the selected key · `Esc` deselect.

The keys themselves stay on the character's components; the window is a second view of the same data, so
the inspectors and the timeline always agree. **Take ▸ Save/Load** snapshots them into the timeline asset
when you want one file to hold the whole shot.

### ❄ Freezing a segment

Freeze a segment you are happy with and Generate **stops requesting it**: its frames are kept exactly,
and only the live runs between frozen segments are sent. Freeze the first of two segments and Generate
makes one request instead of two.

Continuity is handled for you: the run after a frozen segment is told where and how that segment ended,
so it carries on from there instead of restarting at the origin, and a run that ends at a frozen segment
is steered into its opening. Unfreezing drops all of that automatically.

A frozen segment's duration is locked to the frames it kept, and while anything is frozen Generate
returns a single sample. If a run has to travel a long way to meet a frozen segment, you get a console
warning — it will line up, but it may slide; a waypoint near the junction is the better fix.

### Per-segment constraint weight

One beat can pin harder than its neighbours: tick **Own constraint weight** in the panel (the block then
shows a `w5` badge).

## 7. Baking to an AnimationClip

**Bake to AnimationClip…** on the generator (or in the Timeline toolbar) writes a normal Humanoid
`.anim` you can use anywhere — no Kimodo, no server, no package at runtime.

Two things decide how it moves through the world:

- **Root bake mode** on the generator — **Travel** keeps the root motion; **InPlace** strips the
  horizontal travel so the character animates on the spot.
- The Animator's **Apply Root Motion** checkbox — with a Travel clip, that is what actually moves the
  GameObject.

**Root motion scale** is measured automatically on each Generate to absorb per-character unit and scale
differences. If the character shoots across the scene or barely moves, nudge it there; the value is kept
when a cached motion is restored, so a recompile will not undo your tuning.

## 8. Using it from code

Add a `KimodoMotionPlayer` to a Humanoid character and drive it at runtime:

```csharp
using AminHP.KimodoBridge;

var player = character.GetComponent<KimodoMotionPlayer>();
var client = new KimodoClient("http://127.0.0.1:8765");
client.Generate(
    new KimodoGenerateRequest { prompt = "wave hello", duration = "3" },
    (ok, motion, err) => { if (ok) player.Play(motion); else Debug.LogError(err); });
```

`KimodoClient` is a plain callback-based HTTP client (`GetHealth`, `GetModels`, `GetSkeleton`,
`LoadModel`, `Generate`, `GetProgress`) and works in the editor and in play mode.

## 9. Troubleshooting

| Symptom | Likely cause and fix |
|---|---|
| **Start fails: "does not have Kimodo installed"** | The Python you picked is not the environment Kimodo lives in. Point **Setup ▸ Python** at that environment's interpreter. |
| **Connect goes green instantly, model and all** | A server was already running (another Unity project, or a terminal) with the model loaded. Nothing is wrong — that is the one you are using. |
| **Start says the port is already taken** | Same cause, older behaviour. Start now adopts a server that is already answering; if you still see this, something else owns port 8765 — press **Connect**, or stop that process where it was started. |
| **Generate fails with HTTP 500** | The message names the failing stage and the request shape; the full traceback is in **Server output** (or the terminal). |
| **Generate fails: connection refused** | The server is not running, or **Server URL** points somewhere else. For a remote server, check it was started with `--host 0.0.0.0` and that the firewall allows the port. |
| **Everything is green but nothing works** | The connection is a health check, not a live socket. Press **Disconnect** then **Connect** to re-check. |
| **A hand or foot ignores its target** | Constraints are soft. Raise **Constraint weight**, describe the same action in the prompt, and make sure the target is reachable — a dotted line means the limb cannot get there from that pose. |
| **A dragged arm or leg stretches longer and longer, and the move gizmo grows with it** | Fixed in **v0.2.8**. Keys already authored repair themselves the next time they load; one that ended up badly mangled can be reseeded with **⋮ ▸ Align pose to frame** on the key. |
| **The character walks on the spot / shoots across the scene** | Root bake mode and the Animator's **Apply Root Motion**; then **Root motion scale** on the generator. See [Baking](#7-baking-to-an-animationclip). |
| **The preview vanished after a recompile** | It should be restored from the cache automatically. Check the cache line at the bottom of the generator; if it says nothing is cached, generate once more and save the scene. |
| **No editing tools in the Scene view** | The **Kimodo Constraints** overlay is hidden — open the Scene view's overlay menu (⋮⋮ or `` ` ``) and tick it. |
| **A frozen junction slides** | The generated run had to travel to meet the frozen segment (there is a console warning with the distance). Add a waypoint near the junction, or a prompt that gets the character closer. |

## 10. How it works

```
Unity Editor (C#)  ──HTTP/JSON──►  bridge server (FastAPI)  ──►  Kimodo model
 components + gizmos                 127.0.0.1:8765               diffusion (CUDA)
 retarget preview / bake             ships in Server~             text encoder (CPU)
```

The server returns **pure Kimodo coordinates** — right-handed, Y-up, +Z forward, metres. Unity does all
the conversion: `KimodoCoords` for the handedness flip, and `KimodoRootMap` for the measured
world↔Kimodo mapping that constraint authoring uses. Motion is retargeted onto your character through
Mecanim's muscle space, so any Humanoid rig works.

The model stays loaded in the server between requests, so the first generate — which also loads it — is
much slower than the ones after it. Those still take tens of seconds: the prompt is encoded every time,
on the CPU by default.

## Roadmap

- **Foot-lock / anti-sliding** using the `footContacts` the server already returns.
- **Model switching UI** — SOMA is the default; other models are heavier (a second text encoder).

## License

Apache-2.0. Kimodo is separate, under its own Apache-2.0 license, and is not redistributed here.

## Support

If this saved you some time, you can support the work here: **[Buy me a coffee](https://buymeacoffee.com/aminhp)**.
