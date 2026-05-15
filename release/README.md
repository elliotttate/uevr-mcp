# UEVR-MCP — quickstart

Thanks for downloading UEVR-MCP. This zip contains everything you need to dump a UE4/UE5 game to a buildable project (USMAP + UE project + IDA/Binary Ninja bundle), or to drive a running UE game from an AI agent over the [Model Context Protocol](https://modelcontextprotocol.io).

> Full project docs and source: <https://github.com/elliotttate/uevr-mcp>

## What's in the box

```
bin/
  UevrMcpServer.exe   self-contained .NET 9 — no SDK install needed
  uevr_mcp.dll        the UEVR plugin you'll install per-game
  dumper7.dll         fallback dumper for games UEVR can't render-hook
tools/                PowerShell wrappers (quick-dump, dumper-mode, etc.)
examples/.mcp.json    template registration for MCP clients
install.ps1           first-run installer
README.md             this file
VERSION
```

UEVRBackend.dll is **not** bundled — `install.ps1` will pull the latest from [praydog/UEVR's GitHub releases](https://github.com/praydog/UEVR/releases) on first run.

## 60-second quickstart

```powershell
# 1. Unzip somewhere stable (it will live there long-term — e.g. C:\Tools\UevrMcp)
cd C:\Tools\UevrMcp-v1.0.0

# 2. Run the installer for a specific game (also downloads UEVRBackend.dll)
.\install.ps1 -GameExe "E:\SteamLibrary\steamapps\common\MyGame\...\MyGame-Win64-Shipping.exe"

# 3. Launch the game via Steam / Epic, wait for the main menu, then dump:
.\tools\quick-dump.ps1 -GameExe "E:\SteamLibrary\...\MyGame-Win64-Shipping.exe" -OutDir C:\dumps\MyGame
```

Output in `C:\dumps\MyGame\`:

- `MyGame.usmap` — FModel / CUE4Parse / UAssetAPI mappings
- `MirrorProject\` — a buildable `.uproject` with `Source/<Module>/{Public,Private}/*.h`
- `REBundle\` — jmap, `.hpp`, Binary Ninja + IDA import scripts

## Connecting an MCP client (optional)

If you want an AI agent (Claude Code, Cursor, etc.) to drive UEVR for you, `install.ps1` can write the right registration file.

```powershell
# Claude Code (workspace-scoped — writes .mcp.json in current dir)
.\install.ps1 -McpConfig claude-code-here

# Claude Code (user-scoped — writes %USERPROFILE%\.claude.json)
.\install.ps1 -McpConfig claude-code-user

# Cursor (writes %USERPROFILE%\.cursor\mcp.json)
.\install.ps1 -McpConfig cursor-user
```

After this, restart your MCP client. Then ask your agent something like *"set up UEVR-MCP for MyGame.exe, wait for the plugin, and dump the UE project to C:\dumps\MyGame"* and it will call `uevr_setup_game`, `uevr_wait_for_plugin`, `uevr_dump_usmap`, `uevr_dump_ue_project`, etc.

## Common questions

**Q: Do I need to install .NET, CMake, or Visual Studio?**
No. `UevrMcpServer.exe` is self-contained; everything else in this zip is precompiled.

**Q: Do I need to install UEVR separately?**
No. `install.ps1` will fetch `UEVRBackend.dll` from the latest UEVR release. If you already have UEVR installed, you can pass `-BackendDll` to point at it, or set the `UEVR_BACKEND_DLL` environment variable.

**Q: My game crashes on injection.**
Most fragile AAA games are dumped in *dumper mode* (UEVR loads the plugin but skips the VR render hooks). `install.ps1 -GameExe ...` enables dumper mode by default. If you want VR, run `.\tools\disable-dumper-mode.ps1 -GameExe ...`.

**Q: Where do the files go?**
- The plugin DLL goes to `%APPDATA%\UnrealVRMod\<GameName>\plugins\uevr_mcp.dll`
- The dumper-mode sentinel goes to `%APPDATA%\UnrealVRMod\<GameName>\dumper_mode`
- Dumps go wherever you point `-OutDir`

**Q: How do I update?**
Replace this directory with a newer release zip. Per-game plugin installs in `%APPDATA%\UnrealVRMod\` keep working — re-run `install.ps1 -GameExe ...` to push the new plugin DLL.

**Q: Can I use this without the bundled exe — just call the MCP server via `dotnet run` like the dev setup?**
Yes. The full repo at <https://github.com/elliotttate/uevr-mcp> has a `tools/setup.ps1` for that workflow.

## Troubleshooting

The plugin logs to `%APPDATA%\UnrealVRMod\<GameName>\log.txt` (UEVR host log). The MCP server's HTTP backend listens on `127.0.0.1:8899`. If `quick-dump.ps1` complains about no HTTP response, check that log first.

For per-game stability notes and the deep dive on dumper-mode internals, see the repo's `docs/dumper-mode.md`.
