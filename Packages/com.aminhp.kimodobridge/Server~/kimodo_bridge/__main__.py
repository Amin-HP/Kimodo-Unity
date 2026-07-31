# SPDX-License-Identifier: Apache-2.0
"""Launch the Kimodo Unity bridge server.

Usage:
    python -m kimodo_bridge [--host 127.0.0.1] [--port 8765] [--preload soma]
"""

from __future__ import annotations

import argparse

import uvicorn


def main() -> None:
    parser = argparse.ArgumentParser(description="Kimodo <-> Unity bridge server")
    parser.add_argument("--host", default="127.0.0.1", help="Bind host (default: 127.0.0.1)")
    parser.add_argument("--port", type=int, default=8765, help="Bind port (default: 8765)")
    parser.add_argument(
        "--preload",
        default=None,
        help="Optional model name to load at startup (e.g. 'soma'). Otherwise loaded on first request.",
    )
    args = parser.parse_args()

    # Import after arg parsing so --help is fast and doesn't import torch.
    from . import server

    if args.preload:
        print(f"[kimodo-bridge] Preloading model '{args.preload}' on {server._DEVICE} ...")
        with server._MODEL_LOCK:
            server._get_or_load_model(args.preload)
        print("[kimodo-bridge] Preload complete.")

    print(f"[kimodo-bridge] Serving on http://{args.host}:{args.port}  (device={server._DEVICE})")
    uvicorn.run(server.app, host=args.host, port=args.port, log_level="info")


if __name__ == "__main__":
    main()
