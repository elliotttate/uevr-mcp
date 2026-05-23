# Plan: Expose the Stereo Forensics system through the UEVR MCP

Status: **partially implemented 2026-05-23** (see "Implementation status" below).
Author: review pass 2026-05-23.

## Implementation status (2026-05-23)

- **Phase 1 (live control) — DONE (pre-existing).** UEVRJ already exports
  `uevr_render_diag_stereo_forensics_json` + `uevr_render_diag_stereo_forensics_arm_json`
  and `StereoForensics::arm_capture()` (plus a `UEVR_STEREO_FORENSICS_ARM_FILE`
  sentinel). The plugin registers `GET /api/render/stereo-forensics` and
  `POST /api/render/stereo-forensics/arm`. C# tools `uevr_render_stereo_forensics`
  and `uevr_render_stereo_forensics_arm` already wrap them.
- **Phases 2-4 — DONE this pass.** New `mcp-server/StereoForensicsTools.cs`
  (`[McpServerToolType]`) adds: bundle readers `uevr_forensics_sessions`,
  `uevr_forensics_eye_diff`, `uevr_forensics_lineage`, `uevr_forensics_manifest`
  (read the on-disk session bundle directly); Python-tool wrappers
  `uevr_forensics_ingest`, `uevr_forensics_suspects`, `uevr_forensics_query`,
  `uevr_forensics_generate_rules`, `uevr_forensics_shader_semantics`,
  `uevr_forensics_compile_rule`, `uevr_forensics_score`, `uevr_forensics_ab_loop`
  (shell out via `ExternalTools.Run`); and experiment-rule control
  `uevr_forensics_set_experiments` / `uevr_forensics_clear_experiments`
  (write the watched rules file). Builds clean (`dotnet build mcp-server`).
- **Removals — none made (deliberate).** On inspection the old forensics tools
  (`uevr_render_symmetry_oracle`, `uevr_render_export_frame_pair_diff`,
  `uevr_render_stereo_summary`, `uevr_render_diagnose_eye_bug`,
  `uevr_render_capture_investigation_bundle`) are **not** superseded: they run off
  the always-on `D3D12Diagnostics`/live-inspector sampling and need **no capture
  session**, so they're the quick live-triage layer, complementary to the new
  capture-session forensics. They were kept; the two closest-overlap tools had
  their descriptions updated to point at the StereoForensics path.
- **Code bridge (symptom → cause) — DONE this pass.** Four more tools in
  `StereoForensicsTools.cs` tie a D3D12 symptom back to engine/game code (full
  design: `UEVRJ/docs/sn2/STEREO_FORENSICS_CODE_BRIDGE.md`):
  - `uevr_forensics_symbolize` — resolve runtime addresses / a captured
    `issuer_stack` to engine functions (binfold 220k syms + curated RVA dict).
  - `uevr_forensics_shader_code` — shader CRC / CS name → UE FShader class +
    `.cpp/.usf` source.
  - `uevr_forensics_ida_ue_diff` — UE 5.6.1 source function vs IDA decompile
    ("stock UE under -emulatestereo, or UWE-modified?").
  - `uevr_forensics_trace` — capstone: eye_diff issue → symbolized issuer →
    shader/source → producer lineage → ranked root-cause hypothesis with a
    file:line. Best with `UEVR_STEREO_FORENSICS_STACKS=1` at capture time
    (UEVRJ `Sn2IssuerStack` attaches the callstack to work events).
- **Config:** `StereoForensicsTools` resolves paths via env with defaults
  (`UEVR_FORENSICS_TOOLS_DIR`, `UEVR_FORENSICS_PYTHON`, `UEVR_STEREO_FORENSICS_DIR`,
  `UEVR_FORENSICS_DB`, `UEVR_STEREO_EXPERIMENTS_FILE`).
- **Not yet done:** end-to-end live verification against a running game; the
  UEVRJ hook-side `is_capturing_this_frame()` perf gate (tracked separately).

## Goal

Make the whole Stereo Forensics layer (built in `E:\Github\UEVRJ`) drivable from
the MCP so an agent can, without hand-running scripts:

1. turn capture on, **arm a capture burst when actually in the target scene**,
   and read live capture/limiter status;
2. pull the current frame's `eye_diff` / `lineage` and the session manifest;
3. run the offline analysis pipeline (ingest → rank suspects → generate
   candidate intervention rules → query);
4. push experiment rules into the running game (hot-reloaded) and run the
   closed-loop A/B (baseline → rule → trial → ROI score → record/promote);
5. run shader-semantic summaries and compile a winning experiment into a durable
   fix rule.

## What already exists (don't rebuild)

The MCP is a C# stdio server (`mcp-server/*.cs`, `[McpServerTool]` methods) that
calls the in-game C++ plugin over HTTP (`Http.Get/Post("/api/render/...")`,
`mcp-server/Http.cs`). The plugin (`plugin/src/routes/render_routes.cpp`,
`register_routes(httplib::Server&)`) resolves `uevr_render_diag_*` exports from
`UEVRBackend.dll` and serves them.

Already wired (the **old** render-diag forensics surface):

- `/api/render/eye-diff`, `/api/render/eye-diff-grid` (in-plugin grid sampling via
  `eye_region_sample`), `/api/render/eye-sample`, `/api/render/eye-dump`,
  `/api/render/export-frame-pair-diff`, `/api/render/select-eye`,
  `/api/render/stereo-summary` path, `/api/render/ranked-candidates`,
  `/api/render/export-bundle`, `/api/render/sn2-state`,
  `/api/render/capture-window`.
- C# wrappers for those live in `RenderDiagnosticsTools.cs`.
- External-CLI tool pattern already exists (`ExternalTools.cs` shells out via
  `ProcessStartInfo`/`Process.Start`, e.g. `uevr_uesave`, `uevr_patternsleuth`).

## The gap

The **new** `render::StereoForensics` session layer is not exposed anywhere:

- `uevr_render_diag_stereo_forensics_json` exists in the C API but is **not
  resolved or routed** in the plugin.
- There is **no C API** for the runtime controls the new layer needs: arm/reset
  the capture window, runtime enable toggle, live limiter status, `is_capturing`.
- The session **bundle** (`events.jsonl`, `eye_diff.json`, `lineage.json`,
  `resources.json`, `descriptors.json`, `manifest.json` with `limiter_status`)
  is written to `UEVR_STEREO_FORENSICS_DIR\session_*` and is not surfaced.
- The **Python analysis tools** (`UEVRJ/tools/stereo_forensics_*.py`:
  `db`, `query`, `ab_loop`, `experiment`, `run_experiments`, `compile_rule`,
  `shader_semantics`) are standalone CLIs, not MCP tools.
- Experiment rules are file-based (`UEVR_STEREO_EXPERIMENTS_FILE`, hot-reloaded
  every ~30 frames) — nothing writes that file on demand.

## Data-access strategy

Three transports, picked per data type:

- **Live control + live state** → plugin HTTP (`/api/render/forensics/*`).
  Needs small C API additions in UEVRJ.
- **Bundle file contents** (eye_diff/lineage/manifest/limiter_status) → read
  **directly from `session_dir` by the C# server** (same machine). Get the path
  once from the status endpoint, then read files. Avoids piping big JSON through
  the in-game HTTP server.
- **Offline analysis** → C# shells out to the existing Python tools
  (`python <UEVRJ>\tools\stereo_forensics_*.py ...`), same as `ExternalTools.cs`.
  These read the on-disk bundle and need no game running.

Experiment rules: the MCP tool **writes the watched rules file** the game
hot-reloads — no new C API needed for that path.

## Cross-repo dependencies (do these in UEVRJ first)

These are the only changes required **outside** this repo. They are small.

1. **Route the existing export.** Resolve `uevr_render_diag_stereo_forensics_json`
   in `render_routes.cpp` and serve it at `/api/render/forensics/status`.
   (Zero new C API — function already exists.)
2. **Add `StereoForensics::arm_capture(uint32_t num_frames)`** + C API
   `uevr_render_diag_forensics_arm_capture_json(int)` that resets
   `captured_frames`/`skipped_frames`/`capture_stopped` and sets a fresh
   `start_frame = current_frame` so the burst lands *now*. This is the
   already-recommended arm-on-demand trigger; it is what makes capture usable
   (otherwise the 16-frame budget is spent during menu/load).
3. **(Optional) `uevr_render_diag_set_stereo_forensics_enabled(int)`** for runtime
   on/off without relaunch. Nice-to-have; env gate already works at startup.
4. **(Optional) fold a live `limiter_status` + `is_capturing` blob into the
   status JSON** so the MCP doesn't have to read `manifest.json` for progress.

Everything else is MCP-side only.

## Tool inventory (this repo)

New file `mcp-server/StereoForensicsTools.cs` (`[McpServerToolType]`). Naming
follows the existing `uevr_render_*` convention; suggest `uevr_forensics_*`.

### Phase 1 — Live session control & state (plugin HTTP)
| Tool | Transport | Backing |
|---|---|---|
| `uevr_forensics_status` | GET `/api/render/forensics/status` | `uevr_render_diag_stereo_forensics_json` (+ optional live limiter blob) |
| `uevr_forensics_arm` | POST `/api/render/forensics/arm` `{frames}` | new `arm_capture` C API (dep #2) |
| `uevr_forensics_enable` | POST `/api/render/forensics/enable` `{enabled}` | optional dep #3 |

### Phase 2 — Bundle readout (C# reads session_dir directly)
| Tool | Source |
|---|---|
| `uevr_forensics_eye_diff` | `<session_dir>/eye_diff.json` (latest) |
| `uevr_forensics_lineage` | `<session_dir>/lineage.json` |
| `uevr_forensics_manifest` | `<session_dir>/manifest.json` (incl. `limiter_status`) |
| `uevr_forensics_frame_summary` | `<session_dir>/frames/frame_<N>_summary.json` |

`session_dir` comes from `uevr_forensics_status`. These are read-only file pulls;
cap returned size and offer a `--issues-only` projection of `eye_diff`.

### Phase 3 — Offline analysis (C# → Python CLIs)
| Tool | Wraps |
|---|---|
| `uevr_forensics_ingest` | `stereo_forensics_db.py analyze-session <session> --db <db>` |
| `uevr_forensics_suspects` | `stereo_forensics_db.py rank-suspects` |
| `uevr_forensics_query` | `stereo_forensics_query.py {summary,issues,event,shader,lineage,alias}` |
| `uevr_forensics_generate_rules` | `stereo_forensics_run_experiments.py generate --mode {probes,mutations}` |
| `uevr_forensics_shader_semantics` | `stereo_forensics_shader_semantics.py analyze[-dir]` |
| `uevr_forensics_compile_rule` | `stereo_forensics_compile_rule.py` |

Add a server config value for the UEVRJ tools dir + python exe (don't hardcode
`E:\Github\UEVRJ`). Mirror how `ExternalTools.cs` resolves tool paths.

### Phase 4 — Experiment rules + closed-loop A/B
| Tool | Action |
|---|---|
| `uevr_forensics_set_experiments` | write the JSON the game watches (`UEVR_STEREO_EXPERIMENTS_FILE`); game hot-reloads |
| `uevr_forensics_clear_experiments` | write `{"rules":[]}` |
| `uevr_forensics_score` | `stereo_forensics_experiment.py score` (baseline vs trial PPMs, ROI causal control) |
| `uevr_forensics_ab_loop` | `stereo_forensics_ab_loop.py run` — capture→rule→trial→score→record/promote |

The A/B loop needs the eye-screenshot trigger (`UEVR_SN2_EYE_SCREENSHOT_*`) and a
running game; surface that as a precondition check in the tool. It already
orchestrates baseline/trial capture + scoring + DB write + optional fix-rule
promotion, so the MCP tool is mostly arg marshaling + a long-running-process
guard (these runs take minutes — return a job handle or stream progress).

## Suggested sequencing

1. **UEVRJ deps #1 + #2** (route status, add `arm_capture`). Without arm, live
   capture lands on the menu and the rest is untestable on real scenes.
2. **Phase 1 + Phase 2** MCP tools — status, arm, and bundle readout. This alone
   makes "arm in the water, read eye_diff" possible from the agent.
3. **Phase 3** — ingest/suspects/query/generate. Turns a capture into a ranked
   suspect list and candidate rules.
4. **Phase 4** — set-experiments + score + ab_loop. Closes the loop end to end.

## Risks / notes

- **Don't pipe `events.jsonl` through HTTP** — it can be 100s of MB. Only the
  small derived files (`eye_diff`, `lineage`, `manifest`, frame summaries) should
  be surfaced; for raw events use the Python query/DB tools.
- **Long-running A/B**: `uevr_forensics_ab_loop` runs for minutes. Use the
  background/job pattern (see how other long tools behave) rather than a blocking
  call; return partial `results.json` paths.
- **Path config**: the Python tools and the session dir live in UEVRJ / a tmp
  dir. Add explicit server settings (UEVRJ root, python exe, forensics dir,
  experiments-file path, db path) instead of hardcoding.
- **Outstanding UEVRJ perf item (separate from this plan)**: the hook's per-draw
  gathering isn't gated by `is_capturing_this_frame()`, so capture frames are
  cheap but skipped frames still pay. Worth landing before heavy MCP-driven
  capture sessions, but not a blocker for wiring the tools.
- **Stay additive**: new `StereoForensicsTools.cs` + a `forensics/` route group;
  leave the existing `/api/render/*` and `RenderDiagnosticsTools.cs` untouched.

## Definition of done

An agent can, with the game running, call `uevr_forensics_arm` while in an
in-water scene, `uevr_forensics_eye_diff` to see the per-eye issues,
`uevr_forensics_ingest` + `uevr_forensics_suspects` to rank them,
`uevr_forensics_generate_rules` + `uevr_forensics_set_experiments` to apply a
candidate, and `uevr_forensics_ab_loop` to score and (if causal) promote it to a
durable fix — no manual script runs.
