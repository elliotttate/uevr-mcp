# RenderDoc Capture From MCP

UEVR-MCP can drive UEVRJ's embedded RenderDoc path without a separate shell.
The MCP server runs on the host, launches the game through
`UEVRRenderDocLauncher.exe`, writes UEVR's RenderDoc capture sentinel, waits for
the `.rdc`, and validates it with `renderdoccmd.exe`.

## Requirements

- UEVRJ built with the RenderDoc port:
  `E:\Github\UEVRJ\build\bin\uevr\UEVRRenderDocLauncher.exe`
- RenderDoc source checkout built at `E:\Github\renderdoc`, or pass explicit
  paths to `renderdoc.dll` and `renderdoccmd.exe`.
- A D3D12 game launch path. For UE games, prefer the shipping exe and force
  D3D12 with `--dx12` or the game's equivalent flag.

Set these environment variables when your paths differ:

| Variable | Purpose |
|---|---|
| `UEVRJ_ROOT` | UEVRJ checkout root |
| `UEVR_RENDERDOC_LAUNCHER` | Explicit `UEVRRenderDocLauncher.exe` |
| `UEVR_RENDERDOC_BACKEND_DLL` | Explicit `UEVRBackend.dll` |
| `UEVR_RENDERDOC_DLL` | Explicit `renderdoc.dll` |
| `RENDERDOC_ROOT` | RenderDoc checkout root |
| `RENDERDOCCMD_EXE` | Explicit `renderdoccmd.exe` |

## Tool Flow

1. `uevr_renderdoc_paths`

   Verifies path discovery before launching anything.

2. `uevr_renderdoc_launch_game`

   Creates the game suspended through UEVRJ's launcher, injects
   `renderdoc.dll` first, injects `UEVRBackend.dll` second, waits for UEVR's
   early D3D12 prehook, then resumes the game.

3. `uevr_renderdoc_request_capture`

   Writes `%TEMP%\uevr_renderdoc_capture.req`, waits for the resulting
   `.rdc`, then validates and thumbnails it. Use this when the game is already
   running from step 2.

4. `uevr_renderdoc_capture_game`

   One-call version of steps 2 and 3. Use this for scripted captures.

5. `uevr_renderdoc_validate_capture`

   Runs `renderdoccmd index-capture` and `renderdoccmd thumb` on an existing
   `.rdc`.

6. `uevr_renderdoc_list_captures`

   Lists recent captures in the common UEVR temp capture directories.

## Example: Subnautica 2

```json
{
  "tool": "uevr_renderdoc_capture_game",
  "arguments": {
    "gameExe": "E:\\Github\\Subnautica 2\\Subnautica2\\Binaries\\Win64\\Subnautica2-Win64-Shipping.exe",
    "cwd": "E:\\Github\\Subnautica 2",
    "gameArgs": "--dx12",
    "xrRuntimeJson": "E:\\Github\\OpenXR-Simulator\\bin\\openxr_simulator.json",
    "startupDelaySeconds": 75,
    "captureTimeoutSeconds": 120,
    "uevrRoot": "E:\\Github\\UEVRJ",
    "renderDocRoot": "E:\\Github\\renderdoc",
    "stopAfterCapture": false
  }
}
```

The result includes:

- launcher stdout/stderr and game PID when reported;
- capture path and byte size;
- validation output directory;
- `actions.jsonl`, `events.jsonl`, `state.jsonl`, and `resources.json` sizes and
  line counts when full indexing is enabled;
- `thumbnail.png` when thumbnail extraction succeeds.

## What The Capture Proves

This path is different from late DLL injection. The launcher makes RenderDoc
resident before the game's first `D3D12CreateDevice`, `CreateDXGIFactory*`, and
swapchain creation. UEVR then hooks RenderDoc's wrapped D3D12/DXGI interfaces.
When the capture is healthy, the UEVR log should show the first observed device,
factory, swapchain, queue, command list, resources, descriptor heap, root
signature, and PSO with vtables from `renderdoc.dll`.

The `.rdc` is a native RenderDoc file. It should open directly in qrenderdoc and
index with:

```powershell
E:\Github\renderdoc\x64\Development\renderdoccmd.exe index-capture --out <out_dir> <capture.rdc>
```

## Large Captures

Full `index-capture` can take minutes and several GB of RAM on large live-game
captures. For a quick proof that the file opens, call
`uevr_renderdoc_validate_capture` with `runIndex=false` and
`extractThumbnail=true`. Use full indexing when an agent needs draw/event/state
JSONL for analysis.
