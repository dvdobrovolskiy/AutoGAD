# AutoGAD — Claude-powered AI assistant for AutoCAD 2025

A persistent, in-CAD AI agent. Ask questions about the active drawing, run engineering
calculations, and (with confirmation) modify the drawing — all from a docked chat palette.
Backed by the Claude API (`claude-opus-5` by default) — or any OpenAI-compatible API (OpenAI,
OpenRouter, a local server) — with a tool-use loop over the **live** drawing database. Sister project of [FreeGAD](../FreeGAD) (the same agent inside FreeCAD).

What Claude can do inside AutoCAD:

- **See the drawing** — a compact snapshot (meta, units, extents, layers, layouts, entity counts,
  block inventory, schedule text) goes into every conversation; tools read the live details
  (`query_entities`, `get_block_inventory`, `get_layers`, `get_tables`, `get_context`).
- **Modify the drawing** — `run_command`, `set_layer_state`, `create_text`, `replace_text` (global
  find/replace in text, attributes, MLeaders and table cells), `set_text`, `set_custom_property`.
  Every write tool shows a confirmation dialog with the exact change; tick **Auto-approve** in the
  palette (or click *Allow all this session*) to skip the dialogs.
- **Remember** — notes saved with `remember` come back in later sessions: per-user notes
  (standards, voltages, cable types, how you like answers) and per-drawing notes (project code,
  which layers/blocks mean what, decisions made). Drawings are keyed by their fingerprint GUID, so
  notes survive renames/moves. Nothing is written into the DWG.
- **Report cost** — every answer ends with a line like
  `3 calls · in 1,234 (+18,000 cached) · out 800 · ≈ $0.05 this turn, $0.21 this session`
  (list-price estimate from the token usage the API returns; OpenRouter reports the real charge,
  unknown models show `cost n/a`).

## Install

### Option A — installer (end users)

Run `AutoGADSetup.exe`. It installs per-user (no admin) to `%APPDATA%\AutoGAD\bin`, registers the
plugin for demand-loading in every AutoCAD 2025+ profile of your Windows account, and optionally
asks which provider to use and for your API key. Close AutoCAD first (the installer checks). Then start AutoCAD: an
**AutoGAD** tab appears on the ribbon (and an *AutoGAD* pull-down when `MENUBAR` is 1); `AUTOGAD`
opens the chat palette.

Build the installer with **`make_installer.bat`** (needs a .NET SDK and
[Inno Setup 6](https://jrsoftware.org/isinfo.php)). Like FreeGAD's, it bumps the patch version in
`version.txt` on every build, stamps it into `AutoGAD.csproj` (so `AUTOGADVER` and the palette show
it) and `bundle\PackageContents.xml`, builds `AutoGAD.dll`, and writes `dist\AutoGADSetup.exe`.

Uninstalling (Apps and Features → *AutoGAD for AutoCAD*) removes the DLL and the registry entries but
keeps `%APPDATA%\AutoGAD\config.json`, `memory\` and `history\`, so a reinstall keeps your key and notes.

### Option B — per-machine MSI

`make.cmd` (WiX v5) still builds `dist\AutoGAD-<version>-x64.msi`, which installs the Autodesk
Autoloader bundle for **all users** under `%ProgramFiles%\Autodesk\ApplicationPlugins\AutoGAD.bundle`
(admin, silent install with `/qn` for GPO/SCCM). See *Signing* below. **Do not install both the
MSI and the per-user installer** — AutoCAD would load the assembly twice and fail on duplicate
commands; `AutoGADSetup.exe` and `install.ps1` warn when they find the bundle. Note the bundle
autoloader only works when the AutoCAD system variable `APPAUTOLOAD` is not `0` (see
*Troubleshooting*); the per-user installer's registry demand-load works regardless.

### Option C — script (developers)

Same layout and registry entries as the installer, plus a build:

```
pwsh -File install.ps1                    # build + install + prompt for the key (if none stored)
pwsh -File install.ps1 -SetKey            # change the stored key only
pwsh -File install.ps1 -ApiKey sk-ant-... # non-interactive
pwsh -File install.ps1 -NoBuild           # install the DLL already in bin\Release
pwsh -File install.ps1 -SkipKey           # install without touching the key
pwsh -File install.ps1 -Uninstall         # unregister and remove installed files
```

## The AutoGAD menu

Ribbon tab **AutoGAD** (also the pull-down menu and the buttons in the palette's top bar):

| Item | Command | Does |
|---|---|---|
| Chat | `AUTOGAD` | Show the dockable chat palette |
| API key | `AUTOGADKEY` | Set, replace or remove the Anthropic API key (validated against the API) |
| Settings | `AUTOGADSET` | Model, effort (`low`…`max`), max output tokens, refusal fallback, auto-approve |
| Memory | `AUTOGADMEM` | What AutoGAD remembers about this drawing and about you; delete / clear; clear chat history |
| Reset | `AUTOGADRESET` | Forget the current chat and re-read the drawing on the next question |

Plus, command line only: `AUTOGADMEMCLEAR` (erase notes: drawing / user / both), `AUTOGADVER`
(version, load path, key status, settings — ask for this in bug reports), `AUTOGADCFG` (open
`config.json` in Notepad), `GADEXPORT` (full drawing → JSON dump).

## The chat palette

WhatsApp-style transcript: your messages on the right, AutoGAD's on the left (Markdown rendered —
headings, tables, code, links), system notes centred. Type a prompt and press **Enter**
(Shift+Enter = newline). The top bar shows the active drawing, an **Auto-approve** toggle, the
**effort** selector and the Key / Settings / Memory / Reset buttons; the status line shows
*Thinking…* / *Calling query_entities…* while a turn runs.

Examples:
- "Calculate the panel load, design current, and breaker per outgoing line."
- "How many VPD projectors and SKR linear fixtures are installed?"
- "Replace the project code К2.АХП-2.2025 with К2.АХП-3.2025 everywhere." *(confirmation dialog)*
- "Remember that I always size cables for 380 V and cos φ 0.95."

## API key and provider

AutoGAD talks to one of two kinds of API: **Anthropic** (Claude, the default) or any
**OpenAI-compatible** endpoint — OpenAI itself (`https://api.openai.com/v1`), OpenRouter
(`https://openrouter.ai/api/v1`, model ids like `anthropic/claude-opus-4.8`) or a local server. Pick
the provider in the key dialog or in Settings; both keys can be stored, so switching is just a
setting. A key is needed once per Windows user. Set or change it any time:

- inside AutoCAD: **AutoGAD → API key** / `AUTOGADKEY` / the **Key** button — validates the key with
  a free `GET /v1/models` call before saving, and can remove it;
- during install (`AutoGADSetup.exe` asks; `install.ps1` prompts);
- `install.ps1 -SetKey`;
- or the `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` environment variables (used when nothing is stored).

It is stored DPAPI-encrypted for the current Windows user in `%APPDATA%\AutoGAD\config.json`
(`apiKeyEnc`); a plain `apiKey` field written by hand is encrypted on first load. A changed key takes
effect on the next question — no AutoCAD restart. DPAPI ties the blob to your account: copying
`config.json` to another machine won't carry the key over.

## Settings

**AutoGAD → Settings** (`AUTOGADSET`), stored in `%APPDATA%\AutoGAD\config.json`:

```json
{ "provider": "anthropic",
  "apiKeyEnc": "AQAAANCMnd8B…", "model": "claude-opus-5",
  "openaiApiKeyEnc": "", "openaiModel": "gpt-5", "openaiBaseUrl": "https://api.openai.com/v1",
  "maxTokens": 16000, "effort": "high", "fallbacks": true, "autoApprove": false }
```

- **provider** — `anthropic` or `openai` (any OpenAI-compatible Chat Completions API).
- **model** — the Anthropic model (`claude-opus-5`, `claude-fable-5`, `claude-sonnet-5`, …).
- **openaiModel / openaiBaseUrl** — model id and endpoint for the OpenAI-compatible provider
  (`gpt-5` at `https://api.openai.com/v1`, or e.g. `anthropic/claude-opus-4.8` at `https://openrouter.ai/api/v1`).
- **effort** — `low` / `medium` / `high` / `xhigh` / `max`; the effort menu in the palette changes it too.
  For OpenAI-compatible APIs it is sent as `reasoning_effort` (low / medium / high) and dropped
  automatically if the server rejects it.
- **maxTokens** — max output tokens per API call (1,024 – 128,000).
- **fallbacks** — server-side refusal fallback: if a safety classifier declines, the request is re-run
  on the recommended substitute model instead of failing (the answer notes *served by fallback model*).
- **autoApprove** — no confirmation dialogs for write tools. *Allow all this session* in the dialog is
  the temporary variant (until AutoCAD closes) and does not change the saved setting.

## Memory

**AutoGAD → Memory** (`AUTOGADMEM`) lists notes with their ids and lets you delete or clear them.
Files live in `%APPDATA%\AutoGAD\memory\` (`user.json`, `drawings\<fingerprint-hash>.json`) as
plain JSON. Claude decides what to save (it is prompted to be proactive: describe a new drawing the
first time, record decisions after analyses/edits, note your preferences and corrections); you can
also just say *"remember that …"* or *"forget that …"*. Limits: 200 entries per store, 2,000 chars
per note, identical text updates the existing note.

## Chat history

Transcripts are saved per drawing in `%APPDATA%\AutoGAD\history\` (the last 40 lines are restored
when the palette opens; a short excerpt of the last 16 lines is given to Claude for continuity).
Recycling: 400 entries / 400 KB per file, files idle for 180 days deleted, folder capped at 25 MB.
**Memory → Clear chat history** deletes the current drawing's file.

## How it feeds Claude (architecture)

- **System prompt, in cache order:** persona → compact drawing snapshot (**prompt-cached** with
  `cache_control`) → memory notes → recent-history excerpt. A second cache breakpoint sits on the
  last message, so each tool-use iteration re-reads the growing conversation from cache.
- **Loop:** manual tool-use loop (max 40 iterations), adaptive thinking, `output_config.effort`,
  `fallbacks: "default"`; tool results over 14,000 chars are truncated, and results from earlier
  turns are shrunk to 600 chars before each new turn. `refusal` and `max_tokens` stop reasons are
  reported in the answer. Raw `HttpClient` to `/v1/messages` (no NuGet — avoids AutoCAD assembly
  load conflicts).
- **Per-drawing conversation:** each open document keeps its own message history; closing the
  drawing drops it. Tools run on AutoCAD's main thread; writes go through `ExecuteInCommandContextAsync`.

## Signing (MSI / installer)

```
pwsh -File sign.ps1 -Thumbprint 1A2B3C...    # cert from your certificate store
pwsh -File sign.ps1 -PfxFile cert.pfx        # or a .pfx (prompts for the password)
pwsh -File sign.ps1 -Path dist\AutoGADSetup.exe
```

`sign.ps1` finds `signtool.exe` (or downloads it from the Windows SDK BuildTools NuGet package into
`tools\`, ~21 MB, no admin), signs SHA-256 with an RFC-3161 timestamp, then verifies with `/pa`.
A self-signed certificate does not remove the SmartScreen warning for end users — that needs an OV/EV
certificate from a CA (hardware token / cloud HSM since 2023; EV gets reputation immediately).

> WiX **v5** is used for the MSI on purpose: v6+ requires accepting the Open Source Maintenance Fee
> EULA, a paid licence for commercial use.

## Troubleshooting

- *No AutoGAD ribbon tab / `Unknown command "AUTOGAD"`* after the per-user installer — start
  AutoCAD once if it was never run under this Windows account (the installer registers under
  existing `HKCU\Software\Autodesk\AutoCAD\R25.0\<product>` profiles), then reinstall. Or load once
  with `NETLOAD` → `%APPDATA%\AutoGAD\bin\AutoGAD.dll` and run `AUTOGADVER`.
- Same symptom after the **MSI** — almost always `APPAUTOLOAD`: if it reports `0`, enter `14` on the
  AutoCAD command line and restart (the autoloader is off, so *no* bundle can load). Also check that
  `SECURELOAD` is not `2` and that a stale dev/per-user install isn't also present.
- *"No API key set"* — AutoGAD → API key (`AUTOGADKEY`).
- The ribbon tab disappears after switching workspaces — it is re-created automatically on the next
  idle tick; if not, run any AutoGAD command.
- Edits Claude made can be undone with normal **Undo** (each tool call is one transaction).

## Files

| File | Role |
|---|---|
| `AutoGadApp.cs` | Commands, palette, ribbon/menu creation on load |
| `RibbonUi.cs` | AutoGAD ribbon tab (AdWindows) + COM pull-down menu |
| `ChatControl.cs` | WhatsApp-style chat UI (WebBrowser transcript, toolbar, input) |
| `Markdown.cs` | Small Markdown → HTML renderer for the bubbles |
| `Agent.cs` | Tool-use loop, per-drawing conversation, cost metrics, history compaction |
| `ClaudeClient.cs` | Raw HTTP client for `/v1/messages` (cache breakpoints, fallbacks) |
| `AgentTools.cs` | Tool schemas + execution (reads + gated writes + remember/forget) |
| `Approval.cs` | Confirmation dialog: Allow / Allow all this session / Deny; auto-approve gate |
| `SettingsForm.cs` | `AUTOGADSET` dialog |
| `MemoryForm.cs` | `AUTOGADMEM` dialog |
| `ApiKeyForm.cs` | `AUTOGADKEY` dialog — enter / validate / replace / remove the key |
| `Config.cs` | `config.json` loader/saver, key migration, installer key hand-off, env fallback |
| `Memory.cs` | Per-drawing and per-user note stores |
| `History.cs` | Per-drawing chat transcripts with recycling |
| `CadJson.cs` | Compact context builder + live-DB read helpers |
| `DataProtection.cs` | DPAPI encrypt/decrypt for the stored API key |
| `ExportCommand.cs` | `GADEXPORT` full JSON exporter |
| `Installer.iss` / `make_installer.bat` / `version.txt` | Per-user Inno Setup installer and its one-click build |
| `install.ps1` | Dev install: build, register, key management, uninstall |
| `make.cmd` / `build-msi.ps1` / `installer/AutoGAD.wxs` / `bundle/PackageContents.xml` | Per-machine MSI (WiX v5, Autodesk bundle) |
| `sign.ps1` | Signs + timestamps an MSI/EXE; fetches `signtool` if missing |
| `NuGet.config` | Pins nuget.org (the machine-level config lists a source that doesn't exist) |

Building needs a .NET SDK (`net8.0-windows`, AutoCAD 2025's runtime; a .NET 9 SDK builds it fine).
The project references `acmgd`, `acdbmgd`, `accoremgd`, `AcWindows` and `AdWindows` from
`C:\Program Files\Autodesk\AutoCAD 2025` with `Private=false`.

## Notes / current limits

- Responses are non-streaming (each API turn is shown when it completes; tool calls appear as they run).
- The API key is DPAPI-encrypted at rest, but any code running as your Windows user can decrypt it —
  it protects against copied files and casual snooping, not against local malware.
- Cost figures are estimates from list prices; the Anthropic console is authoritative.

## License

AGPL-3.0-or-later. Copyright (C) 2026 Dmitriy Dobrovolskiy. See `LICENSE`; every source file carries an SPDX header.
