# The bridge server

This is the Python side of the package: a small FastAPI service that wraps NVIDIA Kimodo and speaks JSON
to the Unity editor.

It ships **inside the package** so that starting the server needs one setting instead of three — Unity
runs it with your Python interpreter and does not have to be pointed at a script elsewhere on the
machine. The folder is named `Server~`, and Unity ignores folders ending in `~`, so none of this is
imported as project assets.

## What you have to install

Kimodo itself, into a Python 3.10 environment:

```bash
git clone https://github.com/nv-tlabs/kimodo
cd kimodo
python -m venv ../venv
../venv/Scripts/pip install -e .     # Windows;  ../venv/bin/pip on macOS / Linux
```

Then in Unity, on the **KimodoBridge** manager, open **Setup**, set **Python** to that environment's
interpreter (`venv/Scripts/python.exe`, or `venv/bin/python`) and press **Start**. Nothing else is
needed — this folder is put on `PYTHONPATH` for you.

## Running it yourself

```bash
python -m kimodo_bridge [--host 127.0.0.1] [--port 8765] [--preload soma]
```

Run it from this folder, or add this folder to `PYTHONPATH`. Then press **Connect** in Unity.

- `--preload soma` loads the model at startup instead of on the first request.
- `--host 0.0.0.0` lets other machines reach it. **There is no authentication** — only do that on a
  network you trust.
- `TEXT_ENCODER_DEVICE=cpu` keeps the ~8B text encoder off the GPU, which is what makes the diffusion
  model fit in 8 GB of VRAM. Unity sets it for you unless you turn it off in Setup.

## Endpoints

| | |
|---|---|
| `GET /health` | Liveness, device, which models are loaded. |
| `GET /models` | The model registry. |
| `GET /skeleton?model=` | Rest skeleton only, so the client can draw and author constraints before the first generation. |
| `GET /progress` | How far the generation in flight has got. Answers *while* `/generate` is working. |
| `POST /load_model` | Preload a model. |
| `POST /generate` | Generate motion; returns the skeleton and per-frame clips in Kimodo coordinates. |

## Keeping it in sync

If you edit the server while developing, the copy you run from your Kimodo checkout is the live one —
copy it back here before releasing, so the package ships the same code.
