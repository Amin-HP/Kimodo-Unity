# The bridge server, bundled

This is the Python side of the package: a small FastAPI service that wraps NVIDIA Kimodo and speaks
JSON to the Unity editor.

It ships **inside the package** so that starting the server needs one setting instead of three — Unity
runs it with your Python interpreter and does not have to be pointed at a script somewhere else on the
machine. The folder is named `Server~`; Unity ignores folders ending in `~`, so none of this is imported
as project assets.

## What you still have to install

Kimodo itself, into a Python 3.10 environment:

```bash
git clone <the NVIDIA Kimodo repository>
cd kimodo
python -m venv ../venv          # or use conda, or any environment you like
../venv/Scripts/pip install -e .   # Windows;  ../venv/bin/pip on macOS / Linux
```

Then in Unity, on the **KimodoBridge** manager, set **Python** to that environment's interpreter
(`venv/Scripts/python.exe`, or `venv/bin/python`) and press **Start server**. Nothing else is needed —
this folder is put on `PYTHONPATH` for you.

## Running it yourself

```bash
python -m kimodo_bridge --host 127.0.0.1 --port 8765 [--preload soma]
```

Run it from this folder (or add this folder to `PYTHONPATH`). On a machine with ~8 GB of VRAM, set
`TEXT_ENCODER_DEVICE=cpu` so the ~8B text encoder stays off the GPU and the diffusion model fits;
Unity sets that for you unless you turn it off.

## Keeping it in sync

If you edit the server while developing, the live copy is the one you run from your Kimodo checkout —
copy it back here before releasing so the package ships the same code.
