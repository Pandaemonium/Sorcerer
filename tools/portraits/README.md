# Portrait worker

Out-of-process SDXL portrait generation for the character creation screen.

`worker.py`, `generate_portrait.py`, and `_fetch_sdxl.py` are **verbatim copies from
WildMagic** (`C:\Games\WildMagic\tools\portraits\`) — that is why they read `WILDMAGIC_*`
environment variables; the C# client (`Scripts/Portraits/PortraitClient.cs`) maps Sorcerer's
`SORCERER_PORTRAIT_*` config onto those names when it spawns the worker. Keep them unmodified
so a re-sync stays a plain file copy.

The heavy torch/SDXL stack lives in a machine-level venv shared with WildMagic, default
`C:\Games\wm_image_venv\Scripts\python.exe` (override with `SORCERER_PORTRAIT_PYTHON`). When
that python or these scripts are absent, `PortraitConfig.Enabled` is false and the creation
screen simply omits the portrait panel — nothing else depends on this directory.

Protocol (one JSON object per line over stdin/stdout):

```
stdin  <- {"id": "0", "description": "...", "out": "C:/.../p.png", "seed": 7, "size": 768, "steps": 28}
stdout -> {"event": "ready"}                          once, after the model loads
stdout -> {"id": "0", "ok": true, "out": "..."}       per completed request
stdout -> {"id": "0", "ok": false, "error": "..."}    per failed request
```

Config (all optional): `SORCERER_PORTRAIT_PYTHON`, `SORCERER_PORTRAIT_ENABLED` (1/0/auto),
`SORCERER_PORTRAIT_DIR` (default `runs/portraits`), `SORCERER_PORTRAIT_SIZE` (768),
`SORCERER_PORTRAIT_STEPS` (28), `SORCERER_PORTRAIT_QUANT` (int8/fp8/none),
`SORCERER_PORTRAIT_FREE_VRAM` (default on — evicts resident Ollama models before generating).
