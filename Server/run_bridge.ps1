# Launch the Kimodo <-> Unity bridge server.
#
#   TEXT_ENCODER_DEVICE=cpu keeps the LLM2Vec text encoder on the CPU so the
#   diffusion model fits in 8 GB VRAM (same setup as the demo). HF_HOME is
#   already a user env var pointing at H:\kimodo\hf-cache.
#
# Usage:
#   .\run_bridge.ps1                 # serves http://127.0.0.1:8765
#   .\run_bridge.ps1 -Port 8765 -Preload soma

param(
    [string]$BindHost = "127.0.0.1",
    [int]$Port = 8765,
    [string]$Preload = ""
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $MyInvocation.MyCommand.Path

# Activate the venv so `python` resolves to 3.10 with kimodo installed.
& "$repo\..\venv\Scripts\Activate.ps1"

$env:TEXT_ENCODER_DEVICE = "cpu"

Set-Location $repo
$args = @("-m", "kimodo_bridge", "--host", $BindHost, "--port", "$Port")
if ($Preload -ne "") { $args += @("--preload", $Preload) }

python @args
