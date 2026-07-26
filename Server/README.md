# Kimodo Bridge — server

A small [FastAPI](https://fastapi.tiangolo.com/) server that wraps the
[NVIDIA Kimodo](https://research.nvidia.com/labs/sil/projects/kimodo/) text-to-motion model and exposes
it as a local JSON API for the Unity package (`GET /health`, `GET /models`, `POST /load_model`,
`POST /generate`).

> This is **reference** code. It does **not** run on its own — it imports the NVIDIA `kimodo` package and
> needs the model weights. Set that up first (see the Kimodo repo), then drop `kimodo_bridge/` next to it.

## Setup

1. Install NVIDIA Kimodo and its dependencies into a Python 3.10 environment (venv/conda), following the
   [Kimodo](https://research.nvidia.com/labs/sil/projects/kimodo/) instructions, and confirm `import kimodo`
   works and the weights download.
2. Copy the `kimodo_bridge/` folder so it is importable as a top-level package (e.g. into the root of your
   local Kimodo repo).
3. Run it:

   ```powershell
   python -m kimodo_bridge --host 127.0.0.1 --port 8765 [--preload soma]
   ```

   `run_bridge.ps1` is a convenience launcher: it activates a sibling `venv`, sets
   `TEXT_ENCODER_DEVICE=cpu` (keeps the ~8B text encoder off the GPU so the diffusion model fits in 8 GB
   VRAM), and starts the server. Adjust the venv path for your machine.

## API

- `GET  /health` — `{ ok, device, loadedModels }`.
- `GET  /models` — available model short-keys.
- `POST /load_model` — `{ model }` → loads/caches the model (heavy; the ~8B text encoder is loaded once).
- `POST /generate` — `{ prompt, model, duration, num_samples, diffusion_steps, seed, postprocess,
  include_positions, cfg_weight, constraints }` → shared skeleton + clips in **pure Kimodo coordinates**
  (right-handed, Y-up, +Z forward, metres). Unity converts to its own space; see the package README.

The model stays resident and is cached by resolved short-key — restart the server after changing
`server.py`.
