README
===

[![MSBuild](https://github.com/laqieer/FEBuilderGBA/actions/workflows/msbuild.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/msbuild.yml)
[![E2E: No ROM](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-norom.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-norom.yml)
[![E2E: FE6](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-fe6.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-fe6.yml)
[![E2E: FE7J](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-fe7j.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-fe7j.yml)
[![E2E: FE7U](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-fe7u.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-fe7u.yml)
[![E2E: FE8J](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-fe8j.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-fe8j.yml)
[![E2E: FE8U](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-fe8u.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/e2e-fe8u.yml)
[![GitHub Release](https://img.shields.io/github/v/release/laqieer/FEBuilderGBA)](https://github.com/laqieer/FEBuilderGBA/releases/latest)
[<img src="https://raw.githubusercontent.com/oprypin/nightly.link/master/logo.svg" height="16" style="height: 16px; vertical-align: sub">Nightly Build](https://nightly.link/laqieer/FEBuilderGBA/workflows/msbuild/master)
[![codecov](https://codecov.io/gh/laqieer/FEBuilderGBA/branch/master/graph/badge.svg)](https://codecov.io/gh/laqieer/FEBuilderGBA)
[![Cross-Platform](https://github.com/laqieer/FEBuilderGBA/actions/workflows/crossplatform.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/crossplatform.yml)
[![Android](https://github.com/laqieer/FEBuilderGBA/actions/workflows/android.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/android.yml)
[![Android Emulator Parity](https://github.com/laqieer/FEBuilderGBA/actions/workflows/android-emulator-parity.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/android-emulator-parity.yml)
[![iOS](https://github.com/laqieer/FEBuilderGBA/actions/workflows/ios.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/ios.yml)
[![Web (WebAssembly)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/pages.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/pages.yml)
[![Release](https://github.com/laqieer/FEBuilderGBA/actions/workflows/release.yml/badge.svg)](https://github.com/laqieer/FEBuilderGBA/actions/workflows/release.yml)

## 🚀 Getting Started

### Project Structure

| Project | Target | Description |
|---------|--------|-------------|
| `FEBuilderGBA.Core` | net10.0 | Cross-platform core library: ROM manipulation, undo, LZ77, Huffman/text encoding, patch detection, translation, caching, git/archive, event ASM/disassembler, struct export, and ~100 other per-class seams. See [docs/CORE-SEAMS.md](docs/CORE-SEAMS.md) for the full catalog. |
| `FEBuilderGBA` | net10.0-windows | WinForms GUI application — **stable; bug fixes only** (see [GUI strategy](docs/GUI-STRATEGY.md)) |
| `FEBuilderGBA.CLI` | net10.0 | Cross-platform command-line tool (72 commands<sup>[†](#cli-command-count)</sup> — UPS/patch, lint, rebuild, deterministic font-library and headless-playtest tooling, buildfile export/build/round-trip, disasm, translate, struct/data export-import, portrait/MIDI/battle-anime/palette, decomp project mode, and more). Full reference: [docs/cli-reference.md](docs/cli-reference.md) · arg table: [docs/cli-args.md](docs/cli-args.md). |
| `FEBuilderGBA.SkiaSharp` | net10.0 | SkiaSharp `IImageService` (GBA 4bpp/8bpp tiles, palette conversion) + `SkiaFontRasterizer` (cross-platform GDI-parity glyph rendering for translation-font auto-generation). |
| `FEBuilderGBA.Avalonia` | net10.0 | Cross-platform Avalonia GUI: 324 editors (unit/item/class/map/event/AI/text/audio/graphics/portrait/world-map/support/arena/monster/summon/menu/credits) with read/write + undo, image PNG import, hex editor, pointer/free-space tools, cross-editor jump/pick navigation, decomp-project mode, a selectable Help → About dialog with one-click version copying, and Help → Check for Updates to open the latest release when a newer build exists. Full editor inventory: [docs/avalonia-forms.md](docs/avalonia-forms.md) · gap analysis: [docs/avalonia-gap-analysis.md](docs/avalonia-gap-analysis.md). |
| `FEBuilderGBA.Tests` | net10.0-windows | WinForms unit and integration tests |
| `FEBuilderGBA.Core.Tests` | net10.0 | Cross-platform Core unit tests (Linux/macOS/Windows), including the SkiaSharp native-version guard and render byte-parity smoke tests. |
| `FEBuilderGBA.Avalonia.Tests` | net10.0 | Avalonia GUI / ViewModel unit tests (data verification, navigation, field completeness). |
| `FEBuilderGBA.Android.Tests` | net10.0-android | On-device instrumentation head: reflection-runs the SkiaSharp byte-parity / version-guard suites on an Android emulator (not run by `dotnet test`). |
| `FEBuilderGBA.E2ETests` | net10.0-windows | End-to-end GUI/CLI tests |

<a id="cli-command-count">†</a> **CLI command count = 72**: distinct top-level command branches in the `FEBuilderGBA.CLI/Program.cs` dispatch table, collapsing the two documented aliases (`--help`/`-h`, `--test`/`--testonly`); `--project` and `--resolve-addr` are counted as separate user-facing commands. The canonical full list is [docs/cli-reference.md](docs/cli-reference.md).

### Reproducible font libraries

`--build-font-library` turns a strict schema-v1 manifest plus hash-pinned local
font, license, and UTF-8 corpus files into ROM-independent `.fontall.txt`/PNG
libraries. It never downloads a font, selects a system fallback, or initializes
a ROM. Generation is staged beside a new destination and published with an
atomic no-replace rename; validation and round-trip modes never rasterize.
Strict generation center-samples the scaled glyph outline before applying the
existing item/text postprocessing, avoiding native antialias coverage drift.
Pinned Windows x64 and Linux x64 fixtures are therefore byte-identical; the
report exposes the payload-tree hash for comparison. The contract does not
claim byte identity across CPU architectures or different Skia versions.
Existing `--generate-font` behavior retains its legacy antialiased rasterizer.

```bash
FEBuilderGBA.CLI --build-font-library --manifest=font-library.json \
  --out=font-library --mode=generate --report=generation-report.json
FEBuilderGBA.CLI --build-font-library --manifest=font-library.json \
  --out=font-library --mode=roundtrip --report=generation-report.json
```

Each job contains `<job>.fontall.txt`, `slots.tsv`, and canonical
`<item|text>_<moji>.png` files. Reports separate packed-byte, PNG-byte, and
ordinal `/`-path SHA-256 identities: embedded `payloadTreeSha256` excludes the
report itself, while the external generation report also records
`fullTreeSha256` for every finalized package file. Reports also carry
manifest/corpus provenance and deterministic per-glyph mappings, dimensions,
widths, filenames, packed/PNG hashes, and structured failure outcomes. ZH PNGs depict the exact
declared-width 40-byte engine stream; non-representable pixel drift is detected
by non-rasterizing round-trip mode. A generation report saved outside the
package is an immutable oracle for later validation/round-trip; without one,
those modes prove internal consistency only. Dry-run verifies provenance,
planning, and style-expanded capacity without opening a face, rasterizing, or
creating/writing a package tree. Core/Avalonia imports use the canonical
filename moji; WinForms requires the strict package's validated `slots.tsv`
record before overriding character conversion, so legacy UTF-8-derived
filenames retain their fallback behavior. See the
[CLI reference](docs/cli-reference.md#--build-font-library).

### Deterministic headless playtest

The opt-in `--playtest` command drives a source-pinned mGBA 0.10.5 binding with
strict JSON input, exact frame/key/RAM actions, assertions, watchdogs, and an
optional final PNG. It emits one JSON verdict and preserves exit `0` (pass),
`1` (setup/harness error), and `2` (behavioral verification failure).
On POSIX hosts, the bounded runner isolates the worker process group and performs
one-shot descendant cleanup after a short-lived leader exits, so inherited pipes
cannot stall result capture.

```bash
FEBuilderGBA.CLI --playtest --check --python=/path/to/playtest-python
FEBuilderGBA.CLI --playtest --rom=mod.gba --scenario=scenario.json \
  --python=/path/to/playtest-python --out=result.json --artifact-dir=artifacts
```

mGBA is never bundled or installed automatically. See
[Headless Playtest](docs/HEADLESS-PLAYTEST.md) for the hash-locked,
security-patched build prerequisites, pinned-source bootstrap, scenario schema,
deterministic startup contract, safety boundary, and CI proof.

> **🧭 GUI strategy — two front-ends, two standards.** The **WinForms GUI**
> (`FEBuilderGBA`) is the mature, widely-used desktop app; its goal is
> **stability**, so it accepts **bug fixes only — no new features**. The
> **Avalonia GUI** (`FEBuilderGBA.Avalonia`) is a cross-platform **preview**,
> so **all new GUI features ship there**. `FEBuilderGBA.Core` / `FEBuilderGBA.CLI`
> are shared and cross-platform and are **not** restricted by this policy. See
> **[docs/GUI-STRATEGY.md](docs/GUI-STRATEGY.md)**.

### Event-unit origin safety

The Avalonia FE6/FE7/FE8 Event Unit editors discover placement lists from both
direct condition slots and event scripts. Rows are address-first and retain
their condition-record/script-command origin, selected map, and
script-discovered map instead
of collapsing equal addresses. FE7/FE8 expansion is offered only for a safe
direct origin after condition-entry and active event-script discovery both
complete, and revalidates that exact pointer slot immediately before the write.
Missing/incomplete discovery and script, manual, shared-address, or stale-slot
selections are refused.
FE6 remains read/edit-only with no expansion command.

### Cloning the Repository

This repository uses **git submodules** for patch management. Clone with:

```bash
git clone --recursive https://github.com/laqieer/FEBuilderGBA.git
```

Or if you already cloned without `--recursive`:

```bash
git submodule update --init --recursive
```

**Note:** The patch repository ([FEBuilderGBA-patch2](https://github.com/laqieer/FEBuilderGBA-patch2)) is maintained separately for independent versioning and faster updates.

**Bundled Tools:** [Event Assembler](https://github.com/laqieer/Event-Assembler) and [ColorzCore](https://github.com/FireEmblemUniverse/ColorzCore) are included as submodules in `tools/`. Event Assembler is treated here as an external source/prebuilt submodule; its upstream solution is stale and is not a parent-supported build target. If no external EA path is configured, FEBuilderGBA automatically uses the bundled tools. To rebuild ColorzCore locally, restore first and then build/publish with warnings as errors:
```bash
git submodule update --init tools/Event-Assembler tools/ColorzCore
dotnet restore tools/ColorzCore/ColorzCore/ColorzCore.csproj -p:Configuration=Release -p:TargetFramework=net10.0 -warnaserror
dotnet build tools/ColorzCore/ColorzCore/ColorzCore.csproj -c Release --no-restore -p:TargetFramework=net10.0 -warnaserror

# Self-contained executable used by ToolPathResolver/releases.
# Replace win-x86 with linux-x64, osx-x64, or osx-arm64 as needed.
dotnet restore tools/ColorzCore/ColorzCore/ColorzCore.csproj -r win-x86 -p:Configuration=Release -p:SelfContained=true -p:TargetFramework=net10.0 -warnaserror
dotnet publish tools/ColorzCore/ColorzCore/ColorzCore.csproj -c Release -r win-x86 --self-contained true --no-restore -p:TargetFramework=net10.0 -warnaserror -o tools/bin
```

**Runtime note:** All releases (WinForms, CLI, Avalonia) ship ColorzCore as a self-contained executable, requiring no additional .NET runtime.

**Disassembler symbol enrichment:** [fe-info](https://github.com/laqieer/fe-info) ships BSD-3-Clause
`json/{fe6,fe8}/code.json` function metadata. FEBuilderGBA includes only those `code.json`
files in app outputs and merges them behind the existing disassembler symbol-map seam so FE6/FE8
disassembly can show decompilation-derived function names when the shipped asmmap has no entry.

**Public Resources:** [FE-Repo](https://github.com/Klokinator/FE-Repo) (graphics) and [FE-Repo-Music-No-Preview](https://github.com/laqieer/FE-Repo-Music-No-Preview) (music) ship as submodules under `resources/`. They are wired into the **FE-Repo** / **FE-Repo-Music** buttons on the portrait, icon, background, CG, battle-background, skill-icon, battle-animation, and song editors (WinForms + Avalonia) to browse and import community assets directly. The **battle-animation** button filters the deeply-nested FE-Repo `Battle Animations/` tree to one preview per weapon-animation and imports the picked animation's FEditor script (`.txt`/`.bin`) in place (#1807). **They are intentionally not bundled into released artifacts** (their payload is too large to attach to every release), so the Resource Browser is empty until you fetch them on demand:

- **Source clone:** `git submodule update --init resources/FE-Repo` (and `resources/FE-Repo-Music-No-Preview` for music), or run the convenience helper `scripts/fetch-fe-repo.sh` / `pwsh scripts/fetch-fe-repo.ps1`.
- **Released build (extracted `.zip`, no git repo / no `scripts/` folder):** shallow-clone the public repo straight into the expected folder next to the executable — `git clone --depth 1 https://github.com/Klokinator/FE-Repo resources/FE-Repo` (and `... https://github.com/laqieer/FE-Repo-Music-No-Preview resources/FE-Repo-Music-No-Preview` for music) — or manually download/extract the repo there.

Until then the Resource Browser shows an actionable empty-state listing **both** the `git submodule update` (source) and `git clone` (released-build) commands, with a **Copy git command** button, instead of an empty tree. See [docs/RELEASE.md → FE-Repo / FE-Repo-Music resources](docs/RELEASE.md#fe-repo--fe-repo-music-resources-on-demand) for the release-notes wording.

On first run, or whenever `config/patch2`, `resources/FE-Repo`, or `resources/FE-Repo-Music-No-Preview` are empty, both the WinForms and Avalonia GUIs show a **Content Repository Setup** wizard. It lets you review or customize each repository URL, initialize/update with Git when available, or follow manual ZIP download/extract instructions when Git is unavailable. Released empty directory stubs are initialized in place; a failed clone restores the original stub layout (or preserves a nonempty directory through a backup/restore transaction). You can re-open it later — in the **Avalonia** GUI from **Tools → Content Repositories…**; in the **WinForms** GUI, either from the Settings menu (next to the tool-setup wizard entry, on every main form) or by clicking the **Content Repositories** label on the Options dialog's **Path** tab. Explicit WinForms URL edits are saved when the wizard closes; clearing a field restores the floating default and updates an existing local repo's origin when Git is available. There is no separate, inline repository-URL block on any other Options tab (e.g. Function3) — the wizard is the single place to view or edit these URLs in WinForms.

**Avalonia portrait imports:** the Portrait Editor / Portrait Import Wizard accept 96×80 face PNGs, FE-Repo Standard Hackboxes (128×112), and FE8 HALFBODY Halfbody Hackboxes (160×160) when the HALFBODY portrait-extension patch is installed. FE-Repo Twoparter Hackboxes (144×304) are rejected with guidance until a verified PART1/PART2 layout is implemented. The wizard's Step 2 source preview and its per-frame live preview both render from one shared, color-keyed + auto-quantized buffer built once per picked file (#1980), so the frame preview's transparent background always matches the source preview instead of showing the raw opaque background color. Step 2's preview always shows the picked SOURCE file — it never changes when you select a different row in the target-slot list, and no ROM data is written until you click Import; the wizard's copy and a bordered preview call this out explicitly (#1981). Step 2 now also shows a side-by-side **CURRENT ROM TARGET** preview next to the SOURCE preview, rendering the 96×80 portrait currently stored in the selected slot straight from the ROM (falling back to its 32×32 mini face when a full render isn't available), so you can compare before/after without leaving the wizard; it refreshes on every target-slot selection change and again right after a successful Import, and clears to blank on deselect/filter-out/an unrenderable slot — it never shows the staged SOURCE image (#2016). The Frame selector cycles 0↔10 in both directions (increase at the max frame wraps to 0, decrease at 0 wraps to the max frame) instead of stopping at the bounds (#1985). Standard D0/D4/D8/D12 portrait fields and palettes (Avalonia and CLI `--import-portrait`/`--import-portrait-all`) now write through portrait-only Core helpers instead of the generic mid-ROM free-space search, because that generic search could select a live vanilla 0x00/0xFF-fill run — e.g. the previously reported Battle-Screen-Layout corruption at `0x00802178`/`0x00802238` on a canonical FE8U ROM — instead of genuinely free space. A recognized FE6/FE7/FE8 expansion-area floor (FE6 `0x00800000`, FE7/FE8 `0x01000000`) still allows safe in-place reuse/growth there via the existing relocation logic; below the floor, on an unrecognized ROM, or when metadata can't be validated, the write is append-only (bounded ROM growth per import; only a validated four-byte D0 header may be read to preserve raw-vs-LZ77 format, while the old target is never size-parsed, sharing-scanned, reused, or cleared). FE6's `+12` field is coordinates, not a mouth-frame pointer, and is never routed through the mouth/D12 write path. `SharePalette` mode still skips the D8 write (it dereferences the existing pointer instead). Each CLI portrait import is its own real, atomic undo transaction — a failure partway through (e.g. the ROM-size cap) rolls back every byte, pointer, and the ROM's length, so `--import-portrait-all` isolates one failed file without disturbing already-imported ones; success leaves the change in place. This does not change the residual mid-ROM free-space-collision risk for other (non-portrait) generic-writer callers.

**Avalonia launcher search:** the main-menu filter box ("Type to filter editors…") matches your query case-insensitively against the button label **and** the actual display title of the editor window/page that button opens, so typing `Visual` now finds the **Map Editor** button (it opens the *Visual Map Editor*) instead of returning nothing ([#2019](https://github.com/laqieer/FEBuilderGBA/issues/2019)). The desktop launcher also still matches category headers; Easy Mode applies the same title aliases at category level, so `Visual` reveals its **Maps** card; the single-view (Android / iOS / browser) launcher matches labels and titles only. Trade-off: a button that dispatches to several game variants (e.g. **Map Settings** → FE6 / FE7 / FE7U / generic) is searchable by *any* of those variant titles, so `FE7U` can reveal a shared button on an FE6 ROM. This is additive discoverability only — the existing ROM-version/patch visibility gating stays authoritative about which buttons exist at all, and clicking the button still opens the variant that matches your loaded ROM. The searchable titles are a checked-in mirror (`FEBuilderGBA.Avalonia/Services/EditorSearchIndex.cs`) so filtering never constructs an editor while you type; parity tests fail CI if a title is renamed without updating it.

**Avalonia desktop save shortcut:** on the desktop `MainWindow` only, exact `Ctrl+S` or `⌘S` invokes the existing **Save ROM** action when that menu item is enabled. Separate editor windows keep their own shortcuts, and the single-view Android/iOS/browser shells continue to use their own Save UI.

### Warning-clean Build Matrix (Linux / macOS / Windows)

The supported matrix is:

- Debug + Release x86 solution builds
- Cross-platform `net10.0` projects and tests
- Android + `FEBuilderGBA.Android.Tests`
- Browser `wasm-tools` publish
- Helper tools
- iOS on the macOS CI boundary
- ColorzCore

The Core library, CLI, SkiaSharp backend, and Avalonia GUI scaffold all target `net10.0` and build on any platform:

```bash
# Build Core library
dotnet restore FEBuilderGBA.Core/FEBuilderGBA.Core.csproj
dotnet build FEBuilderGBA.Core/FEBuilderGBA.Core.csproj --no-restore -warnaserror

# Build cross-platform CLI
dotnet restore FEBuilderGBA.CLI/FEBuilderGBA.CLI.csproj
dotnet build FEBuilderGBA.CLI/FEBuilderGBA.CLI.csproj --no-restore -warnaserror

# Run CLI
dotnet run --project FEBuilderGBA.CLI -- --version
dotnet run --project FEBuilderGBA.CLI -- --makeups=out.ups --rom=modified.gba --fromrom=original.gba
dotnet run --project FEBuilderGBA.CLI -- --applyups=output.gba --rom=original.gba --patch=patch.ups
dotnet run --project FEBuilderGBA.CLI -- --lint --rom=rom.gba
dotnet run --project FEBuilderGBA.CLI -- --disasm=output.asm --rom=rom.gba
dotnet run --project FEBuilderGBA.CLI -- --decreasecolor --in=input.png --out=output.png --paletteno=16 --json
dotnet run --project FEBuilderGBA.CLI -- --pointercalc --rom=source.gba --target=target.gba --address=0x1234
dotnet run --project FEBuilderGBA.CLI -- --rebuild --rom=modified.gba --fromrom=vanilla.gba
dotnet run --project FEBuilderGBA.CLI -- --export-buildfile --rom=modified.gba --clean=original.gba --out=project/
# buildfile.json + data/ are authoritative; derived main.event is real-toolchain tested when bundled EA is available.
# main.event emits ColorzCore's byte-wise FILL Amount Value form.
# Each range's gbaAddress is always 0x08000000 + offset, including header offsets 0 and 1.
# ROM inputs are opened no-follow, identity-compared, bounded, and read through exact handles.
# The force-version value is mandatory; valueless forms fail before command I/O.
# Browser builds fail closed because exact no-follow ROM opening is unavailable.
# --with-source caps manifests at 16 MiB with bounded lines/directives, then defers sidecar bodies to bounded capture.
# Projection text files are capped at 16 MiB and strict UTF-8; UTF-8 BOM is accepted, UTF-16/32 BOMs are rejected.
# Projection scratch is deleted before the stage exists; test-mutated source candidates are rematerialized.
# Stage/scratch names use a bounded stable hash and are atomically reserved; collisions are never reused.
# Final publication is an atomic no-replace rename; a race-created destination is never replaced.
# On Windows, use standard drive/UNC paths; device namespaces (\\?\, \\.\, and \??\) are rejected.
# ROM aliases retain platform-accurate link semantics; Windows long paths keep handle-level identity checks.
# Windows paths and exact opened handles use 128-bit file identity with a capability-only 64-bit fallback.
# The advisory patch inventory (patches.installed + nested params) and warnings share ONE
# internal 16,384-item resource-safety budget; an oversized patch library DEGRADES the advisory
# patch inventory to "unavailable" (a stable, path-free reason) rather than truncating it or
# failing export — the authoritative recipe/payloads/manifest still export successfully.
# Each PATCH_*.txt definition is also byte-bounded: 16 MiB per file, rejected on raw BYTES
# BEFORE a single line is decoded. An oversized or sparse-reported FileStream.Length above
# 16 MiB is rejected up front, before any read is issued. Once that length check is passed,
# a separate cap+1 probe (reading at most cap+1 bytes on the same handle) still detects a
# file that GROWS beyond the cap after the length check, again before the surplus byte is
# ever written or decoded. The reader also enforces exact length-drift detection on that same
# handle: accepted bytes reaching decode must equal the FileStream.Length captured at open time
# EXACTLY. Any byte read past that captured length is rejected before it is written or decoded,
# even when the running total is still comfortably within the 16 MiB cap, and reaching genuine
# EOF with FEWER bytes than that captured length (a premature EOF) is rejected the same way.
# bytesRead stays monotonic and visible to the caller on either rejection; a false length-drift
# result immediately degrades/clears/stops the enclosing advisory pass, so no later file in that
# pass can exploit any remaining aggregate byte budget. This is a length-drift check, not an
# immutable-snapshot guarantee: an in-place mutation that leaves the file's length unchanged, or
# bytes appended strictly AFTER THE FINAL OBSERVED EOF, are both outside what this check can
# detect — an append visible BEFORE that EOF is instead read as surplus and rejected the same
# way. The reader never allocates a buffer sized to the full 16 MiB cap
# for every file: it issues fixed 64 KiB ArrayPool-rented chunk requests, and its initial
# accepted in-memory storage capacity is based on the already-validated FileStream.Length
# (so at most 16 MiB, never the unvalidated raw/sparse-reported value), growing further only
# as bytes are actually accepted — it never blindly pre-allocates the full 16 MiB cap for a
# small file. Raw lines are counted against an independent raw-line cap DURING decoding, not
# after, so no partial line list is ever produced or returned on a breach. The metadata scan
# and the raw-params scan each additionally enforce their OWN separate 64 MiB aggregate byte budget across every file
# in that pass (128 MiB worst case across both passes combined); any byte-bound breach
# (per-file or aggregate) degrades the WHOLE advisory patch inventory the same way the
# 16,384-item budget does, and accepted files still decode byte-identically to the legacy
# File.ReadLines/File.ReadAllLines APIs. The one deliberate behavioral difference is
# install-marker CLASSIFICATION: this bounded metadata scan classifies a file-backed $FGREP
# install marker (one the shared resolver would resolve by opening the external file it names)
# as "unknown" BEFORE any filename access, whereas the public/unbounded Patch Manager, CLI,
# patch scanner, and rebuild paths still resolve it (shipped patches depend on that legacy
# behavior); the bounded raw advisory params read from the patch definition itself are unchanged.
# Each PATCH_*.txt FINAL entry is opened no-follow through the same exact-regular-file
# primitive used for ROM/projection I/O: a symlink, reparse point, or other non-regular-file
# type is refused before any byte is read (ancestor directories of the patch library are out
# of scope for this guarantee), and Browser hosts fail closed instead of falling back to an
# unsafe open. The shared bounded reader itself never swallows a missing entry: it lets
# FileNotFoundException/DirectoryNotFoundException propagate and each caller decides. A missing
# patch ROOT at INITIAL discovery is a successful empty listing; a definition file that was
# already discovered but is missing or faulting when the bounded METADATA pass opens it degrades
# the WHOLE advisory inventory to "unavailable" with a generic, path-free filesystem reason
# (NOT success-empty and NOT a resource-budget reason); only the later bounded raw-PARAMS pass
# maps a missing file to successful, empty params.
# The published buildfile.json itself is also capped at the SAME shared 16 MiB the consumer
# enforces (see --build-buildfile below) — measured by exact UTF-8 byte count, including the
# trailing newline, before both the initial write and any later rewrite. Over cap with an
# available advisory patch inventory degrades it ENTIRELY (never a partial/truncated list);
# still over cap afterward (or nothing left to degrade) aborts the export before publication
# rather than ever writing an oversized manifest — closing a prior producer/consumer gap where
# only the consumer enforced this bound.

dotnet run --project FEBuilderGBA.CLI -- --build-buildfile --clean=original.gba --project=project/ --out=rebuilt.gba
# Rebuilds ONLY from buildfile.json + data/; main.event/ColorzCore/source/patches/projection are never used.
# Enforces exact clean identity + version, schema v1, UTF-8/JSON/dup-key, exact object members/types, and every size/range/path/hash/changed-byte bound.
# data/ is captured no-follow/handle-relative; symlinks, subdirs, missing/extra/mismatched payloads are rejected.
# buildfile.json is bounded to 16 MiB — the SAME shared cap --export-buildfile now also enforces
# on every manifest it publishes, so an exporter-produced manifest is never rejected here SOLELY
# because exporter-owned serialization exceeded that shared cap; post-publication mutation and
# every other structural/identity/payload/filesystem validation below remain independently
# enforced regardless of origin. A hand-edited or third-party-produced buildfile.json is not
# exempt and is rejected if oversized.
# The SAME shared 16,384-item advisory budget applies here as a hard structural-validation
# failure: a supplied buildfile.json whose combined patches.installed + nested params +
# warnings exceeds the cap is rejected BEFORE any of those advisory arrays are materialized
# into POCOs/lists (fails closed rather than allocating unboundedly for a hostile/corrupt file).
# Staging cleanup/collision classification never uses File.Exists (which can misreport success
# on a non-file replacement or an inspection failure); every failure fails closed with the exact path.
# --out must remain outside project/data by physical entry identity (including aliases), rechecked before bounded-name durable no-replace publication. Exit 0/1.
dotnet run --project FEBuilderGBA.CLI -- --buildfile-roundtrip --rom=modified.gba --clean=original.gba
# Exports (source projection off) into private scratch, independently rebuilds, and byte-compares against --rom.
# The already-opened --rom bytes are the sole drift oracle; no project/output is intentionally published.
# Scratch cleanup is attempted and verified after every export/rebuild attempt; deletion failure is a hard
# error (exit 1) reporting the residual scratch path — this is not an absolute guarantee no scratch can remain
# (e.g. a crash before cleanup runs, or external interference).
# Exit 0 = byte-for-byte reproducible; 2 = reproducibility drift; 1 = usage/validation/IO/cleanup failure.
# Byte/length drift reports the first-difference offset (or first length-difference offset); a
# declared-target-identity-only drift (rebuilt bytes match --rom but the recipe's own declared target
# crc32/sha256 do not) instead reports the declared-vs-actual crc32/sha256 hashes, not an offset.
dotnet run --project FEBuilderGBA.CLI -- --songexchange --rom=dest.gba --fromrom=source.gba --fromsong=1 --tosong=2
dotnet run --project FEBuilderGBA.CLI -- --convertmap1picture --in=map.png --outImg=tiles.bin --outTSA=tsa.bin --outPal=palette.bin --json
# Run a trusted local FEMapCreator installation through the external-process adapter
# (Windows .exe, managed .dll, or an executable native file on Unix);
# FEBuilderGBA never downloads or bundles FEMapCreator or its assets. The output is
# FEBuilderGBA CSV and does not load or mutate a ROM.
dotnet run --project FEBuilderGBA.CLI -- --generate-random-map --femapcreator=C:\tools\FEMapCreator.exe --tileset="FE6 - Fields - 01020304" --width=15 --height=10 --algorithm=experimental --seed=42 --out=random-map.csv
# The Avalonia Map Editor's "Generate Random Map" button is true one-click (no dialog, no ellipsis): it uses the
# current map's dimensions and an inline, directly replayable seed. A blank seed is materialized before either
# backend starts, and the adjacent Cancel button remains enabled during generation so the operation can be
# stopped without closing the editor. Authoritative FEMapCreator profile/mapping hashes — including Options'
# Save Mapping image and generation-data identities — run off the UI thread, collect size/timestamp/hash from
# one opened file handle, and observe cancellation between bounded file reads. Generation resolves/decompresses
# the authoritative OBJ/PAL/CFG/MAP tileset snapshot on
# the same worker boundary and returns that exact fingerprint for the apply-time live-ROM guard; Map Editor no
# longer performs a separate pre-service resolve on the dispatcher (the guard still re-resolves the live ROM
# immediately before mutation).
# Discovery and Save Mapping share one exclusive busy/cancel state. Built-in generation clones the ROM and
# current grid before the worker hop, then scans cancellation-aware address/tag rows directly (without display-name
# decoding or global-ROM text reads), so corpus scanning never races live editor writes; if that grid cannot be
# decoded exactly, generation fails before either backend is invoked rather than disabling source-identity rejection. Cancellation is also
# observed while each cell's weighted candidate order is prepared. Built-in tileset loading rejects truncated
# primary/secondary OBJ, CFG, or MAP LZ77 streams, and a nonzero secondary OBJ reference must resolve completely
# instead of being silently omitted from the tileset identity. Per-tileset FEMapCreator discovery and
# mapping now live only in Options' FEMapCreator section (Map Editor's "Map Tileset..." button is just a
# shortcut that selects Options' External Tools tab and scrolls the FEMapCreator section into view with the
# current tileset pre-selected). Live path status performs path and file-metadata checks only, so opening
# Options or typing never hashes executable content on the UI thread. Both live and authoritative profile
# validation reuse the launcher's supported-program rules, so Windows accepts `.exe`/managed `.dll` paths
# and Unix native programs must still have execute access before the profile can be Configured.
# Loading Options snapshots both saved
# FEMapCreator fields before textbox change events run, so neither persisted value can clear the other.
# Editing either live FEMapCreator path
# cancels any in-flight discovery or Save Mapping operation and clears discovered choices, so Save Mapping
# always requires a fresh discovery from the same executable/assets profile. Discovery and Save Mapping both
# authoritatively re-hash even same-size/same-mtime executable replacements. Discovery re-checks cancellation
# after its final authoritative profile validation immediately before publishing any returned tilesets.
# Save Mapping persists a detached
# config snapshot to a flushed sibling temp file, atomically replaces config.xml, and updates the live config
# only after that succeeds; it re-checks cancellation after snapshot serialization and again at the flushed-temp
# replacement boundary — immediately before the atomic replaceFile, once the sibling temp is fully written and
# flushed — so a cancellation observed at that boundary still leaves the original config.xml on disk untouched
# and the temp is always cleaned. Write failures preserve the prior
# disk/live state and cannot masquerade as success.
# If a mapping is configured and current,
# Generate runs the external adapter above; once started, any launch/exit/parse failure is surfaced directly
# and never silently swapped for the built-in engine, and cancelling (or closing the editor) terminates the
# owned external process rather than letting it finish and apply. Both generation and tileset discovery re-check
# cancellation after path/launch-spec setup and immediately before invoking any process runner. A successful
# result from either backend must contain exactly width*height cells, and every returned MAR must have a complete
# TSA configuration block in the resolved tileset snapshot, with at least two distinct MAR values in the final
# layout. After FEMapCreator exits, its executable, image, and generation-data identities are authoritatively
# revalidated against the same immutable mapping snapshot before success publication. Invalid, degenerate, or
# identity-stale external output fails without falling back to the built-in engine. Built-in, external-adapter,
# and backend-neutral result storage clone generated MARs
# and return copies to callers, so later consumer mutation cannot invalidate replay or diversity metadata.
# The resolved MapTilesetSnapshot and its derived tileset corpus are immutable: they clone all MAP/OBJ/PAL/CFG
# buffers, contributing-map/candidate lists, frequency dictionaries, and directional adjacency sets before
# exposing defensive public views. Relaxed-model metatile edge signatures likewise clone all four edge arrays.
# Mutating caller sources, getter results, or cast public collection views therefore cannot alter stored data or
# diverge the tileset Fingerprint, while trusted generator/validation hot paths read internal zero-copy buffers
# to avoid repeated per-generation clones.
# The validated result is then compared with the captured source grid; a
# sequence-identical layout fails instead of applying an unchanged map as a successful undo transaction.
# Otherwise — or if the saved mapping is
# stale/invalid, which is visibly explained with a localized typed reason (on success, failure, or cancellation
# alike) while raw filesystem diagnostics remain separate rather than leaking English into the notice — it uses
# an independently designed, clean-room, deterministic built-in generator (see
# docs/CORE-SEAMS.md and docs/ENGINEERING-NOTES.md) that needs no external tool, network access, or download,
# and produces the same layout again when re-run with the displayed seed. This engine is experimental and
# visual-coherence-only: it does not guarantee gameplay/objective validity (reachable chests, doors, spawns, etc.).
# Every built-in success uses at least two distinct MAR values; adjacency models exposing fewer than two viable
# candidates fail as insufficient source data, while the stricter three-value/90%-occupancy gate still applies
# whenever the active model exposes at least three viable candidates.
# Its primary OBJ, nonzero secondary OBJ, CFG, and MAP inputs must each be complete LZ77 streams whose
# back-references point into already-produced output; truncated or out-of-history streams fail before decompression.
# Options states that limitation explicitly, and successful generation reports the exact backend name:
# "Built-in Experimental" or "FEMapCreator Experimental".
# The optional first-run tool-initialization wizard's EndPage adds a small, always-optional FEMapCreator
# row: it states plainly that FEMapCreator is an independent utility credited to bwdyeti and that
# FEBuilderGBA does not bundle, host, license, or guarantee it, and that Built-in Experimental remains
# available immediately if you skip it or leave it unconfigured. Its two buttons only ever act on an
# explicit click — one opens the fixed upstream project/setup page in your browser, the other navigates to
# and scrolls the same Options FEMapCreator section above without inventing a map fingerprint (no separate
# path/mapping form is duplicated in the wizard). Platform-specific browser-launch failures are logged and
# reported in that row instead of escaping the wizard's async UI handler.
# Dynamic random-map status, Options FEMapCreator profile/mapping status and errors, and the wizard action-error
# row are polite automation live regions so assistive technology announces actionable updates without interruption.
# Neither the wizard nor Options ever downloads, installs, searches PATH, auto-discovers, or launches
# FEMapCreator merely from being opened or displayed.
dotnet run --project FEBuilderGBA.CLI -- --translate --rom=rom.gba --out=texts.tsv
dotnet run --project FEBuilderGBA.CLI -- --translate --rom=rom.gba --in=texts.tsv
dotnet run --project FEBuilderGBA.CLI -- --translate-roundtrip --rom=rom.gba
dotnet run --project FEBuilderGBA.CLI -- --translate-roundtrip --rom=rom.gba --out=diff
dotnet run --project FEBuilderGBA.CLI -- --export-data --rom=rom.gba --table=units --out=units.tsv  # 40 tables: units, classes, items, portraits, sound_room, sound_boss_bgm, support_units, support_talks, support_attributes, event_haiku, event_battle_talk, event_force_sortie, worldmap_points, worldmap_paths, worldmap_bgm, map_settings, link_arena_deny, cc_branch, menu_definitions, item_weapon_triangle, map_exit_points, ai_map_settings, ai_perform_items, ai_perform_staff, ai_steal_items, ai_targets, generic_enemy_portraits, status_options, ed_retreat, ed_epithet, ed_epilogue_a, ed_epilogue_b, ed_epilogue_c, op_class_demo, op_class_font, op_prologue, class_alpha_names, summon_units, summons_demon_king, monster_probability
dotnet run --project FEBuilderGBA.CLI -- --export-data --rom=rom.gba --table=all --out=data
dotnet run --project FEBuilderGBA.CLI -- --import-data --rom=rom.gba --table=units --in=units.tsv
dotnet run --project FEBuilderGBA.CLI -- --export-data --rom=rom.gba --table=units --format=json --out=units.json  # LLM-backend format: JSON array of string-valued objects, keyed by Index + field name (see docs/febuilder-cli-as-llm-backend.md)
dotnet run --project FEBuilderGBA.CLI -- --export-data --rom=rom.gba --table=units --format=c --c-symbol=gUnitData --out=units.c  # GNU11 decomp-C backend; compile with arm-none-eabi-gcc -std=gnu11 (see docs/febuilder-cli-as-decomp-c-backend.md)
# --c-symbol is strict: file-scope-reserved and <stdint.h>-reserved names (for example uint8_t) are rejected before ROM/output access.
# Generated identifiers are #undef-guarded after includes, so GNU built-ins such as linux/unix and caller -D macros cannot rewrite the C source.
# C export performs one raw+typed table traversal; TSV/CSV/EA/JSON retain typed-only extraction and do not allocate raw stride copies.
# Non-empty C arrays use compiler-visible full-width ordinal designators ([0x000] =, [0x100] =, ...) plus escaped _Index label comments.
dotnet run --project FEBuilderGBA.CLI -- --import-data --rom=rom.gba --table=units --in=units.json  # format auto-detected from .json, or pass --format=json explicitly
# JSON preserves full row indices (including 0x0100+) and uses the canonical FE6/7/8 unit field offsets.
dotnet run --project FEBuilderGBA.CLI -- --data-roundtrip --rom=rom.gba --table=all
dotnet run --project FEBuilderGBA.CLI -- --lastrom
dotnet run --project FEBuilderGBA.CLI -- --force-detail
dotnet run --project FEBuilderGBA.CLI -- --translate_batch --rom=rom.gba --out=texts.tsv
dotnet run --project FEBuilderGBA.CLI -- --test --rom=rom.gba
dotnet run --project FEBuilderGBA.CLI -- --testonly --rom=rom.gba
dotnet run --project FEBuilderGBA.CLI -- --rom-info --rom=rom.gba
# --- Decomp project mode & asset export/import (preview) ---
# FEBuilderGBA can open a decompilation project and migrate edits back to source.
# Representative commands (see docs/cli-reference.md for the full set + every --kind):
dotnet run --project FEBuilderGBA.CLI -- --project=path/to/decomp --rom-info
dotnet run --project FEBuilderGBA.CLI -- --project=path/to/decomp --resolve-addr=0x801234
dotnet run --project FEBuilderGBA.CLI -- --migrate-diff --project=path/to/decomp --rom2=edited.gba
dotnet run --project FEBuilderGBA.CLI -- --write-source --project=path/to/decomp --table=units --id=1 --field=pow --value=7
dotnet run --project FEBuilderGBA.CLI -- --export-asset --project=path/to/decomp --kind=graphics --addr=0x... --out=src/
dotnet run --project FEBuilderGBA.CLI -- --decomp-audit --format=md   # round-trip coverage matrix
dotnet run --project FEBuilderGBA.CLI -- --build-project --project=path/to/decomp --reload --yes
# Full flag/--kind reference, exit codes, and source-owner rules: docs/cli-reference.md

# Build SkiaSharp image backend / Avalonia GUI
dotnet restore FEBuilderGBA.SkiaSharp/FEBuilderGBA.SkiaSharp.csproj
dotnet build FEBuilderGBA.SkiaSharp/FEBuilderGBA.SkiaSharp.csproj --no-restore -warnaserror
dotnet restore FEBuilderGBA.Avalonia/FEBuilderGBA.Avalonia.csproj
dotnet build FEBuilderGBA.Avalonia/FEBuilderGBA.Avalonia.csproj --no-restore -warnaserror

# Run the Avalonia GUI with a ROM (or open a decomp project preview)
dotnet run --project FEBuilderGBA.Avalonia -- --rom path/to/rom.gba
dotnet run --project FEBuilderGBA.Avalonia -- --project path/to/decomp

# Headless smoke / data-verification / screenshot / image+palette round-trip modes
# (used by E2E + the cross-platform gap sweeps; see docs/avalonia-gaps/README.md)
dotnet run --project FEBuilderGBA.Avalonia -- --rom path/to/rom.gba --smoke-test-all
dotnet run --project FEBuilderGBA.Avalonia -- --rom path/to/rom.gba --data-verify
dotnet run --project FEBuilderGBA.Avalonia -- --rom path/to/rom.gba --screenshot-all --screenshot-dir=./shots
dotnet run --project FEBuilderGBA.Avalonia -- --rom path/to/rom.gba --validate-image-roundtrip

# Avalonia ↔ WinForms gap sweeps (static analysis → docs/avalonia-gaps/)
# Use a dated output dir, e.g. docs/avalonia-gaps/YYYY-MM-DD
dotnet run --project FEBuilderGBA.Avalonia -- --gap-sweep-all --out=docs/avalonia-gaps/YYYY-MM-DD

# Cross-platform publish (self-contained) + tests
./scripts/publish-all.sh linux-x64 osx-arm64 win-x64
dotnet test FEBuilderGBA.Core.Tests/FEBuilderGBA.Core.Tests.csproj
```

> **Testing note (contributors):** the host-global `ProcessRunnerCore` process-tree
> scenarios live only in `FEBuilderGBA.Core.Tests`; review their
> `PROCESS_RUNNER_METRIC` rows and the collector-off/on timing gates in
> [DEVELOPMENT-WORKFLOW.md](DEVELOPMENT-WORKFLOW.md) when checking containment regressions.

> **macOS / CJK text note:** The Avalonia GUI registers cross-platform CJK font fallbacks
> (`FontManagerOptions.FontFallbacks`) so ROM-decoded names and Japanese labels render correctly
> on macOS instead of as "tofu" boxes (#1692).

### Decomp Project Support (preview)

FEBuilderGBA can open a **decompilation project** directory (a source tree that
builds a `.gba` ROM, e.g. a `fireemblem8u` / `fe6` decomp). The built ROM is loaded
as a **read-only preview** — the source is the source of truth, so saving over the
built ROM is intentionally blocked (edit the source and rebuild instead). A project is
detected by a `febuilder.project.json` manifest or by Makefile/`*.sha1`/`ldscript.txt`
heuristics.

Open it via **File → *Open Decomp Project...*** (Avalonia) or `--project=<dir>` (CLI).
In decomp mode the suite can:

- **Resolve addresses to source symbols** (`--resolve-addr`, via the project `.map`/ELF/`.sym`/JSON).
- **Migrate edits back to source** with the advisory, read-only diff assistant (`--migrate-diff`).
- **Rewrite owning C/JSON table rows in place** (`--write-source` for items/units/classes/map_settings/support/shops) instead of mutating the preview ROM.
- **Export/import/round-trip/verify assets** (`--export-asset` / `--import-asset` / `--roundtrip-asset` / `--verify-asset`) across many `--kind`s (palette, graphics, map, mapchange, objtiles, mapchipconfig, portrait-package, …).
- **Build + reload** the project ROM (`--build-project`).

The exact flags, `--kind` set, exit codes, and source-owner/ROM-only rules are documented in
**[docs/cli-reference.md](docs/cli-reference.md)**; the full feature inventory (with the PRs that
landed each slice) is in **[docs/DECOMP-FEATURE-INVENTORY.md](docs/DECOMP-FEATURE-INVENTORY.md)**.
The maintained round-trip coverage matrix below shows how each editor/action migrates back to source.


#### Round-trip coverage matrix

The decomp round-trip coverage matrix below is generated by
`--decomp-audit --format=md` (single source of truth:
`DecompRoundTripAuditCore.BuildMatrix()`; a cross-project test asserts this block stays
byte-identical). It maps each editor/action to how its edit migrates back to source —
**SourceBackedWriter** (in-place C/JSON row rewrite), **SourceTreeExporter** (export an
asset + rebuild), **ImportPreviewOnly** (view only), **ManualMigration** (hand-edit
required for variable-length / pointer / raw-binary data), **RomOnlyUnsupported**.

<!-- decomp-audit-matrix:start -->

| Editor | Table | Action | Coverage | Notes |
| --- | --- | --- | --- | --- |
| Item Editor | items | Row save | SourceBackedWriter | Main structured-row save only |
| Unit Editor | units | Row save | SourceBackedWriter | Main structured-row save only (manifest alias: characters) |
| Class Editor | classes | Row save | SourceBackedWriter | Main structured-row save only |
| Map Settings Editor | map_settings | Row save | SourceBackedWriter | Main structured-row save only |
| Support Unit Editor | support_units | Row save | SourceBackedWriter | Main structured-row save only |
| Support Attribute Editor | support_attributes | Row save | SourceBackedWriter | Main structured-row save only |
| Support Talk Editor | support_talks | Row save | SourceBackedWriter | Main structured-row save only |
| Map Settings Editor | map_settings | Chapter pointer fields (EventDataPtr, difficulty) | ManualMigration | Pointer fields (D0/EventDataPtr, D96-D108 difficulty) are not source-backed |
| Palette Editor | palette | Palette export | SourceTreeExporter | JASC .pal export (faithful, lossless round-trip) |
| Graphics Editor | graphics | Graphics export | SourceTreeExporter | Indexed PNG (color type 3) + sidecar .pal |
| Portrait Editor | portrait | Portrait export | SourceTreeExporter | Export via --export-portrait-all (PNG package) |
| Portrait Editor | portrait_package | Portrait package import/round-trip | SourceTreeExporter | Source-tree write-back of a validated 128x112 composite sheet + name-matched JASC sidecar — import (--import-asset --kind=portrait-package, identity copy into an unambiguous project-confined owner; --overwrite an existing owner, ambiguous/multi-PNG destinations refused) + structural round-trip against an explicit baseline (--roundtrip-asset --kind=portrait-package --expect=<baselineDir>); reuses the #1350/#1353 portrait-package validator; never mutates the preview ROM. Source-level structure-exact identity vs a supplied baseline; NO ROM byte-pin (no canonical ROM->128x112-sheet builder exists, so the preview ROM is never the source of truth) |
| Icon Editor | icon | Icon export | SourceTreeExporter | Indexed PNG via graphics exporter (16x16 tiles) |
| Map Editor | map | Map layout export | SourceTreeExporter | .mar tilemap + sidecar .mar.json — export AND re-import/verify (lossless u16 layout body for raw entries < 0x2000, i.e. palette/flag bits 13-15 clear); compressed container re-derived by the build, not byte-pinned |
| Map Editor | map | Map layout import/verify | SourceTreeExporter | Re-import .mar to raw uncompressed tilemap blob + roundtrip-verify; never mutates the preview ROM |
| Map Editor | map_change_overlay | Map-change overlay import/verify | SourceTreeExporter | Raw uncompressed u16 overlay tile data block — export (--export-asset --kind=mapchange) + import (--import-asset) + byte-exact ROM verify (--verify-asset --kind=mapchange) + structural roundtrip; never mutates the preview ROM. Source-level structure-exact identity AND byte-exact ROM compare; NOT the .mar layout and NOT the 12-byte change-record chain |
| Map Editor | map_tileanime2_palette | Map tile-animation-2 palette import/verify | SourceTreeExporter | Raw uncompressed u16 palette data block (count*2 bytes reached by each anime-2 entry's +0 pointer) — export (--export-asset --kind=mapanime2pal) + import (--import-asset) + byte-exact ROM verify (--verify-asset --kind=mapanime2pal) + structural roundtrip; never mutates the preview ROM. Source-level structure-exact identity AND byte-exact ROM compare; NOT the anime2 entry/PLIST table and NOT LZ77 |
| Map Editor | map_obj_tileset | OBJ tileset import/verify | SourceTreeExporter | LZ77 OBJ tile block — export the DECOMPRESSED 4bpp payload (--export-asset --kind=objtiles) + import (--import-asset) + read-only decompress-and-byte-compare ROM verify (--verify-asset --kind=objtiles) + structural roundtrip; never mutates the preview ROM. Decompressed-payload equivalence, NOT compressed-stream byte identity (FEBuilder's LZ77 packer is non-canonical so the build re-compresses); --addr is the DEREFERENCED OBJ LZ77 stream address (FE7 obj2 secondary tileset is a separate stream/address, never concatenated). NOT chipset TSA/config, NOT tile animations 1/2 |
| Map Editor | map_chipset_config | Chipset TSA/config import/verify | SourceTreeExporter | LZ77 chipset TSA/config block — export the DECOMPRESSED config payload (--export-asset --kind=mapchipconfig) + import (--import-asset) + read-only decompress-and-byte-compare ROM verify (--verify-asset --kind=mapchipconfig) + structural roundtrip; never mutates the preview ROM. Decompressed-payload equivalence, NOT compressed-stream byte identity (FEBuilder's LZ77 packer is non-canonical so the build re-compresses); --addr is the DEREFERENCED config LZ77 stream address (e.g. the CONFIG-PLIST pointer dereferenced, NOT RomInfo.map_config_pointer; FE7 split layouts use a separate per-plist --addr). NOT the anime-1/anime-2 entry tables, NOT the map-change record chain, NOT the .mar layout |
| Map Editor | map_tileanime1_graphics | Map tile-animation-1 graphics import/verify | SourceTreeExporter | Raw uncompressed 4bpp graphics data block (the entry +2 byte length, reached by each anime-1 entry's +4 pointer — the inverse of anime-2's +0) — export (--export-asset --kind=mapanime1gfx --addr=<deref +4> --length=<+2>) + import (--import-asset) + byte-exact ROM verify (--verify-asset --kind=mapanime1gfx) + structural roundtrip; never mutates the preview ROM. Source-level structure-exact identity AND byte-exact RAW ROM compare; the WF read/import/rebuild paths treat this block as raw ImageToByte16Tile 4bpp bytes (a rebuild IMG block), NOT LZ77 (so NOT the objtiles/mapchipconfig decompress pattern). NOT the anime-1 ENTRY/PLIST table and NOT the .mar layout |
| Text Editor | text | Text export | SourceTreeExporter | texts.txt + textdefs.txt (fe8u migration format, not lossless macro round-trip); FE8J emits texts/jp_texts.txt #0xNNNN instead (#1774) |
| Item Shop Editor | shops | Shop list save | ManualMigration | Decomp-mode GUI save now routes to SOURCE when the shop's ROM address resolves to a manifest u16-list owner (symbol-resolved) for BOTH literal raw-hex lists AND resolvable symbolic ITEM_* item-id-only lists (#1354) (#1347 Slice 5a); otherwise ROM-only/manual (variable-length ITEM_NONE-terminated lists via scattered hensei/worldmap/event-cond pointers; nonzero-quantity symbolic writes, unknown/ambiguous macros, and unresolved/unnamed shops degrade to --export-asset --kind=shop) |
| Item Shop Editor | shops | Shop list export | SourceTreeExporter | EA .event migration artifact via --export-asset --kind=shop; recreates each u16 ITEM_NONE-terminated list at its source address (migration aid, not source-backed in-place editing, not a byte-pinned round-trip) |
| Item Shop Editor | shops | Shop list source save | SourceBackedWriter | In-place source-backed rewrite of a u16 ITEM_NONE-terminated list (manifest list-owner: format=u16-list, symbol-resolved) via --write-shop; requires decomp-mode .map/.elf carrying the list symbol AND a manifest list-owner; degrades to --export-asset --kind=shop otherwise (#1347). Supports BOTH a LITERAL raw-hex list AND a SYMBOLIC ITEM_* (item-id-only, quantity 0) list whose macro names resolve from the constants header (owner.constantsHeader / artifacts.itemConstants / include/constants/items.h); a non-zero quantity or an id with no ITEM_* constant is an actionable refusal, not a clobber (#1354) |
| Map Editor | map_asset_binaries | Raw map asset save (GUI: anim/map-change record chain) | ManualMigration | GUI raw-ROM-save path for the remaining POINTER-HEAVY map STRUCTURAL tables: the tile-animation-1 ENTRY/PLIST table (8-byte entry rows + wait/length metadata; the per-entry raw 4bpp GRAPHICS block via the +4 pointer is now source-backed export/import/verify above), the anime-2 ENTRY/PLIST table (8-byte entry rows + wait/count/startIndex metadata; the per-entry raw PALETTE block via the +0 pointer is source-backed above) AND the 12-byte map-change RECORD chain (0xFF-terminated, width/height/flagID/PLIST metadata; the per-record raw overlay block via the +8 pointer is source-backed above) — NOT the map-change overlay tile data block NOR the anime-2 PALETTE block NOR the anime-1 GRAPHICS block NOR the OBJ tileset NOR the chipset TSA/config (all five source-backed export/import/verify above) and NOT the .mar tile layout. These ENTRY/PLIST/RECORD tables stay guarded (pointer-per-row/record, ambiguous source ownership — they need a manifest source owner not yet defined; sub-issues #1389/#1390/#1391 track each); migrate the dereferenced data blocks via --export-asset |
| Event Editor | chapter_event_pointers | Event/difficulty pointer fields | ManualMigration | Chapter pointer fields (EventDataPtr, difficulty pointers) are not source-backed |
| Battle Animation Editor | battle_anime | Animation view | ImportPreviewOnly | Preview-only in decomp mode; no source write-back (export via --export-battle-anime) |
| Song Table Editor | song_table | Song view | ImportPreviewOnly | Preview-only; song data edits must be made in source by hand |
| Magic Editor | magic_effects | Magic view | ImportPreviewOnly | Preview-only; magic effect edits are not source-backed yet |
| Hex Editor | raw_rom | Raw byte edit | RomOnlyUnsupported | Arbitrary ROM bytes; not representable as a clean source edit |
| Patch Manager | patches | Patch install/uninstall | RomOnlyUnsupported | ASM/binary patches apply to the built ROM; not a decomp source migration |

<!-- decomp-audit-matrix:end -->

#### FE8J (JP decomp) coverage notes

The matrix above is region-agnostic except where a Notes cell calls out a JP
divergence. For the **FE8J** (`fireemblem8j`, `version 8 + is_multibyte`) tree
specifically:

- **Text export** — emits `texts/jp_texts.txt` in `#0xNNNN` format (consumed by
  `msg_jp.py`), **not** the fe8u `texts.txt`/`textdefs.txt`; `jp_textdefs.txt` /
  `jp_huffman_tiebreaks.txt` are hand/ROM-maintained and left untouched (#1774).
- **Voicegroup asm export** — emits the named `.section .rodata.voicegroupNNN,
  "a", %progbits` + `.align 4` sub-section, not the fe8u `.section .rodata` /
  `.align 2` (#1775).
- **Symbol resolution** — the JP `sym_jp.txt` linker-assignment table is
  auto-discovered and parsed alongside `.map`/`.elf` (#1773).
- **Build enablement** — drop in the
  [`docs/decomp/febuilder.project.fe8j.json`](docs/decomp/febuilder.project.fe8j.json)
  manifest template to opt into `--build-project` (#1778).

All other rows are format-only and region-agnostic (palette / graphics / icon /
map / portrait), so they apply to FE8J unchanged.

Slice 1 (#1129) delivered open + preview; slice 2 (#1130) adds address-to-source
symbol resolution; slice 3 (#1131) adds the diff-to-source migration assistant;
the source-backed table writer is #1132, extended to JSON + units/classes + signed
fields + multi-field in #1141 (full-document JSON validation + width-aware signed no-op
hardening in #1145). Asset exporters (#1133) and in-app build/reload (#1134)
round out the suite.

`--decomp-audit --summary` prints the per-tier coverage counts (with an explicit
`Unclassified = N` line and the size of the maintained editor inventory). The matrix is
**complete relative to the maintained audit inventory** — a maintained classification, not
exhaustive byte-level runtime round-trip proof; full byte-level round-trip editing of every
format remains partial by design (#1150).

> **Decomp feature inventory & release status:** the full decomp project-mode feature set —
> and the PRs/commits that landed each slice — is enumerated in
> [docs/DECOMP-FEATURE-INVENTORY.md](docs/DECOMP-FEATURE-INVENTORY.md). These features
> currently live on **`master`, ahead of any tagged release** (the latest tag is
> `ver_20260204.22`; all decomp work landed after it), so they are not part of an existing
> released build yet. Build from `master` to use them.

### Running on Android (experimental)

Two distinct paths exist, both covered in detail in [docs/CROSS_PLATFORM.md → Running on Android](docs/CROSS_PLATFORM.md#running-on-android):

- **Emulation (Gamenative/Winlator)** — run the Windows *desktop* build under Wine + Box86/Box64. User-side, **experimental / unsupported / community-tested**; try the Avalonia `win-x64` build first.
- **Native Android app** — a separate port of the Avalonia GUI, tracked as exploration epic [#1070](https://github.com/laqieer/FEBuilderGBA/issues/1070). **Experimental / preview — not shipped as a full ROM-editing app.** It builds a signed APK and an emulator **boot smoke test** ([#1640](https://github.com/laqieer/FEBuilderGBA/issues/1640)) now proves the real app launches into its single-view editor-launcher shell (config first-run extraction + Avalonia boot are **emulator-validated**, not merely build-only). The Android docs track the Avalonia 11.3.18 + SkiaSharp 3.119.4 + HarfBuzz 8.3.1.5 stack and the Android 16 KB baseline. What remains on-device-unvalidated is the *interactive* ROM-editing flow — SAF ROM open/save (#1124) and reaching/using an editor need the system file picker + touch UX, which a non-interactive CI smoke test cannot drive — so the head stays experimental/preview. See the evidence-backed feasibility assessment in [docs/ANDROID.md](docs/ANDROID.md) (Avalonia.Android lifetime, SkiaSharp native pin, SAF ROM access, `config/` bundling, the multi-window→single-activity gap, the boot smoke test) and the authored head skeleton in [`FEBuilderGBA.Android/`](FEBuilderGBA.Android/README.md).
  - **Emulator boot smoke test (#1640)** — `.github/workflows/android-emulator-parity.yml` gains an `android-boot-smoke` job that builds the real signed APK, installs it on an API-34 `x86_64` emulator, launches it via its LAUNCHER intent (`monkey` — robust against the CRC-mangled .NET-for-Android activity class name), and asserts the activity reaches the RESUMED state with no fatal exception (PID/package-scoped crash detection, so unrelated system crashes don't false-fail). Advisory / non-blocking (job context `android-boot-smoke`, never the required `build`). Logic in `scripts/android-boot-smoke.sh`.
  - **Stream-based ROM I/O for SAF (#1124)** — `ROM.LoadFromStream`/`SaveToStream` (+ async) share the byte-level seam with the existing path `Load`/`Save`, so the Avalonia head can open/save a ROM picked via `IStorageProvider` even when the SAF `content://` handle has no local filesystem path (it retains the `IStorageFile` and reads/writes through `OpenReadAsync`/`OpenWriteAsync`). Desktop path I/O is unchanged. The auto-save sidecar is redirected into app-private `{BaseDirectory}/autosave/` on Android (where the ROM's parent dir is not writable); the log and `config.xml` already resolve under `BaseDirectory` (`Context.FilesDir` on Android via #1123).
  - **Single-activity navigation model (#1122)** — the desktop multi-window editor model is reworked behind an `INavigationService` abstraction so the same `FEBuilderGBA.Avalonia/Services/WindowManager` API drives two backends. Desktop uses `DesktopNavigationService` (the original `.Show()`/`.ShowDialog()` multi-window behavior, **verbatim** — regression-safe), while Android uses `AndroidNavigationService`, a **single-view page/view-stack host with a back stack** built on a pure, desktop-unit-tested `NavigationStack` (modal-as-page, `PickFromEditor` result-await). `App` sets a `Views/MainView` shell under `ISingleViewApplicationLifetime` so the booted Android app presents the editor launcher. The #1873 rollout has converted the proof editor, the simple AI batch, script-driven simple batches spanning Item/Map/Event/Menu/Sound/WorldMap/Text/Tool/Status/Unit support/resource/menu/class-demo/ending/support-talk/unit-main editors, and the first self-close dialog/tool batch to embeddable `UserControl` editors; the remaining on-device runtime UX (touch + per-editor attached-`Window` dialogs/file pickers) is still tracked under [#1873](https://github.com/laqieer/FEBuilderGBA/issues/1873).
  - **Offline patch-database ZIP import** — Avalonia Patch Manager can import a validated ZIP for the loaded ROM's canonical version through a read-only SAF stream. Confirmation defaults to No; the canonical app-private library persists across Android relaunch/config refresh. Import does not apply patches or enable unsupported EA/unknown execution. See [format, limits, Git-ownership restrictions and recovery](docs/ANDROID.md#52-offline-patch-database-zip-import). Git/HTTP delivery and FE-Repo remain unavailable on Android, and patch data is not bundled in the APK. Dedicated real-app emulator proof is separate from the boot/parity smoke tests.

### Running on iOS (experimental)

A separate native port of the Avalonia GUI, tracked as [#1859](https://github.com/laqieer/FEBuilderGBA/issues/1859) (the iOS counterpart of the Android epic [#1070](https://github.com/laqieer/FEBuilderGBA/issues/1070)). **Experimental / preview — not shipped as a full ROM-editing app.** It builds an iOS `.app` / **unsigned `.ipa`** on macOS CI ([`.github/workflows/ios.yml`](.github/workflows/ios.yml)) and reuses the *same* single-view shell, config first-run extraction, and stream-based ROM I/O seams as the Android head — the runtime pieces are platform-agnostic and shared. What remains **on-device-unvalidated** is the interactive UX (touch, the system file picker, reaching/using an editor) plus iOS's AOT/reflection runtime (mitigated with the Mono interpreter + `MtouchLink=None`, disabling linker trimming at the cost of a larger artifact), so the head stays experimental/preview. See the assessment in [docs/IOS.md](docs/IOS.md) and the head skeleton in [`FEBuilderGBA.iOS/`](FEBuilderGBA.iOS/README.md).

- **Install the preview build** — the CI/release `.ipa` is **unsigned** (no Apple Developer secret on this fork), so it is **not directly installable**. Re-sign it with your own Apple ID via a sideloading tool (**AltStore**, **Sideloadly**) or **Apple Configurator** to run it on a device. When the maintainer adds the `APPLE_*` GitHub Actions secrets, `ios.yml` switches to a release-signed build (see [docs/IOS.md §6](docs/IOS.md#6-build--ci)).
- **`config/` bundling** — `config/**` (excluding `patch2`) ships as `<BundleResource>` (structure preserved via `LogicalName`) and is extracted once on first run into an app-private writable dir via the pure `FEBuilderGBA.Core/AndroidConfigExtractorCore` + the new `FEBuilderGBA.Core/DirectoryAssetSource`, then `App.BaseDirectoryOverride` points Core there. Desktop resolution is unchanged.
- **Known iOS limitation — patch2 / FE-Repo are not bundled** — the same accepted, documented limitation as Android ([#1641](https://github.com/laqieer/FEBuilderGBA/issues/1641)): they are large git-delivered payloads, and iOS has no in-process git. On-demand on-device delivery is a follow-up.

### Running in the browser — WebAssembly (experimental)

FEBuilderGBA runs in a **browser** with no install, via **WebAssembly** — tracked as [#1864](https://github.com/laqieer/FEBuilderGBA/issues/1864). **Experimental / preview.**

> 🌐 **Try it: <https://laqieer.github.io/FEBuilderGBA/>** (deployed by [`.github/workflows/pages.yml`](.github/workflows/pages.yml) to GitHub Pages).

The `FEBuilderGBA.Browser` head builds the shared Avalonia GUI into a `net10.0-browser` wasm AppBundle, reusing the *same* single-view shell + `App.BaseDirectoryOverride` config seam as the mobile heads. It links `SkiaSharp.NativeAssets.WebAssembly` 3.119.4 + `HarfBuzzSharp.NativeAssets.WebAssembly` 8.3.1.5 (both wasm natives) + `Avalonia.Fonts.Inter` (wasm has no system fonts). `config/**` (excl. `patch2`) is zipped into `wwwroot/config.zip`, fetched over HTTP and extracted into the browser's in-memory filesystem on first run via the pure `FEBuilderGBA.Core/ZipAssetSource`. Runs **single-threaded** (GitHub Pages sends no COOP/COEP headers → no `SharedArrayBuffer`) and **untrimmed** (reflection-heavy Core). The single-view launcher exposes the **full editor catalog** — the shared `FEBuilderGBA.Avalonia/Services/EditorCatalog` mirrors the desktop's ~223 editors across 28 categories (version-gated to the loaded ROM), rendered as filterable category expanders ([#1891](https://github.com/laqieer/FEBuilderGBA/issues/1891)). The milestone is **builds + deploys + loads/renders the shell**; full in-browser ROM-editing parity (threading-dependent caches, every file-flow) is a follow-up. See [docs/WEBASSEMBLY.md](docs/WEBASSEMBLY.md) and the head at [`FEBuilderGBA.Browser/`](FEBuilderGBA.Browser/README.md).

**Map navigation order ([#2009](https://github.com/laqieer/FEBuilderGBA/issues/2009))** — the dedicated `Map Editors` category now appears immediately above `Maps` in both the desktop and shared single-view Avalonia launchers. Entries, commands, version gates, and WinForms behavior are unchanged.

**Map Editor usable at default browser zoom ([#1998](https://github.com/laqieer/FEBuilderGBA/issues/1998))** — `MapEditorView`'s upper info/toolbars/palette/tile-editor region now lives in its own bounded Auto `ScrollViewer` (`MapUpperControlsScroller`), so it scrolls independently instead of pushing the map canvas off-screen at compact/default browser viewport heights. `MapCanvasPanel` stays pinned in the layout's star row with its own inner `MapCanvasScroller`, guaranteed at least 240px tall. A guarded `MaxHeight` (driven by the arranged height of the right column, with a >0.5px jitter guard) caps the upper region without ever setting a fixed `Height` or overriding `Arrange`, so the desktop 1200×800 layout and the 250px address list are unaffected. `FEBuilderGBA.Browser/tests/smoke/smoke.mjs` covers this by default: when both `SMOKE_VIEWPORT_WIDTH`/`SMOKE_VIEWPORT_HEIGHT` are unset it runs an in-process 600×500-then-1920×852 viewport matrix in one browser process (single explicit-viewport override still supported), opens the Map Editor at each viewport, and asserts its split-scroller layout metrics via the E2E-only `MapEditorLayoutMetrics()` hook. The map canvas' ≥240px minimum height and the upper-controls overflow direction on whichever axis (height and/or width) is actually below its compact threshold are hard-asserted for **both** synthetic and real ROMs — toolbar/button layout size does not depend on ROM/chapter data. Only the data-dependent checks are real-ROM log-only rather than hard-asserted: the "desktop fits without scrolling" expectation and the both-axis inner-canvas-overflow expectation (asserted against a deliberately oversized ~2200×1200 synthetic map fixture, `SMOKE_ROM=synthetic`, opt-in only for that literal value), since a real chapter's on-screen map size and populated-palette content height are ROM-data-dependent.

**Map Editor starts in Paint Mode ([#2008](https://github.com/laqieer/FEBuilderGBA/issues/2008))** — every fresh Avalonia Map Editor opens with `Paint Mode` checked, while opening the editor alone remains read-only. With no chipset selected, the first map click acts as the eyedropper and selects that tile's chipset; a subsequent deliberate click can paint it. Clear the checkbox to restore select-only behavior. WinForms is unchanged.

**Map Editor compressed relocation is append-only ([#2017](https://github.com/laqieer/FEBuilderGBA/issues/2017))** — `ImageImportCore.WriteCompressedInPlaceOrRelocate` (shared by the Map Editor's edit/grid/resize saves) still reuses the current compressed blob **in place, byte-identically**, when a new payload fits and isn't shared with another pointer. When it must **relocate** (e.g. after a "Generate Random Map" apply that grows the compressed stream), it now **appends** the new payload at the aligned ROM end instead of asking the generic `FindAndWriteData` allocator for a mid-ROM `0x00`/`0xFF` fill run — that generic scan cannot tell a genuinely free run from a live pointer-referenced all-zero block, so reusing it for relocation could silently overwrite in-use data (the reported crash: invalid CPU jump + corrupted battle-background graphics). Allocation now fails before any mutation (`U.NOT_FOUND`, pointer/payload/old-blob all untouched) once the ROM would exceed the 32MB (`0x02000000`) cap. This is a **byte-safety** fix only: it guarantees a relocated map is never corrupted by the write itself, but it does **not** validate that a generated layout is objective/reachability-safe — that remains the Random Map Generator's separate, pre-existing visual-coherence-only boundary. `FindAndWriteData` and all other importers that call it directly are unchanged.

### Architecture Diagram

```
FEBuilderGBA.sln
├── FEBuilderGBA.Core/           net10.0    (cross-platform core)
│   ├── IAppServices.cs                     Platform abstraction
│   ├── IImageService.cs                    Image service abstraction
│   ├── Rom.cs / ROMFE*.cs                  ROM manipulation
│   ├── UPSUtil.cs                          UPS patch creation
│   ├── FELintCore.cs                       Lint validation
│   ├── PathUtil.cs                         Cross-platform paths
│   ├── PointerCalcCore.cs                 Pointer search engine
│   ├── RebuildCore.cs                     ROM defragmentation
│   ├── SongExchangeCore.cs                Cross-ROM song transplant (InstrumentMap/Rip/Burn + sample recycle)
│   ├── MapConvertCore.cs                  Map tile conversion
│   ├── NameResolver.cs                    Entity name resolution with caching
│   ├── SongNameResolverCore.cs            Song name resolution (Sound Room name + SE-list fallback)
│   └── WriteValidator.cs                  ROM write validation utilities
├── FEBuilderGBA.CLI/            net10.0    (cross-platform CLI — 72 commands)
├── FEBuilderGBA.SkiaSharp/      net10.0    (image backend)
├── FEBuilderGBA.Avalonia/       net10.0    (cross-platform GUI — 324 editors, with ambient undo, dirty tracking, data export/import, full Options dialog with 20+ external tool paths)
├── FEBuilderGBA/                net10.0-windows (WinForms GUI)
├── FEBuilderGBA.Tests/          net10.0-windows (WinForms unit tests)
├── FEBuilderGBA.Core.Tests/     net10.0    (cross-platform Core tests)
├── FEBuilderGBA.Avalonia.Tests/ net10.0    (Avalonia GUI / ViewModel tests)
├── FEBuilderGBA.Android.Tests/  net10.0-android (on-device reflection-runner parity/version-guard head)
└── FEBuilderGBA.E2ETests/       net10.0-windows (E2E tests)
```

## Testing & Coverage

- ✅ **11,000+ unit/integration tests** passing across the four desktop test projects — `FEBuilderGBA.Core.Tests` (~5.5k, cross-platform), `FEBuilderGBA.Avalonia.Tests` (~4.6k, GUI/ViewModel), `FEBuilderGBA.Tests` (~1.3k, WinForms), and `FEBuilderGBA.E2ETests` (E2E). `[Theory]` cases expand at runtime, so the authoritative live total for these is the one reported by `dotnet test` / [CI](https://github.com/laqieer/FEBuilderGBA/actions). The fifth project, `FEBuilderGBA.Android.Tests`, is an Android **instrumentation** head (not run by `dotnet test`) — its on-device SkiaSharp byte-parity/version-guard results come from the [Android Emulator Parity workflow](https://github.com/laqieer/FEBuilderGBA/actions/workflows/android-emulator-parity.yml), whose cached-AVD install path verifies and removes any stale test package before running the current APK.
- 🧰 **Cross-platform CI reliability:** a dependency-free contract runs before the .NET jobs, every build/test/publish command disables persistent MSBuild/Roslyn servers, and CLI smoke checks invoke the already-built Release DLL. .NET-heavy macOS build/publish legs are pinned to the stable macOS 15 ARM64 image while Python MCP tests remain on `macos-latest` as a lightweight image canary; compile/test failures stay fail-closed.
- 🍎 **macOS External Tools:** Avalonia accepts native `.app` bundles such as `/Applications/mGBA.app`. The original bundle path remains in config; launch and validation safely resolve a direct executable under `Contents/MacOS` (bundle-name match or one unambiguous executable).
- ✅ **E2E suite** (`FEBuilderGBA.E2ETests`) covers CLI + GUI automation + output-log capture: a no-ROM subset runs on every PR, and the full suite (including the 324-editor Avalonia smoke test, screenshot capture for both GUIs, and CLI output-log capture for both the new CLI and WinForms executables) runs with all 5 ROMs. See the [E2E CI workflows](https://github.com/laqieer/FEBuilderGBA/actions) for the live pass/skip counts.
- 📊 [View Full Coverage Report on Codecov](https://codecov.io/gh/laqieer/FEBuilderGBA)
- 🔍 Latest test results and coverage reports available as [GitHub Actions artifacts](https://github.com/laqieer/FEBuilderGBA/actions)
- 🧪 **Test Coverage:**
  - Unit tests for core utilities (RegexCache, LZ77, U, TextEscape, CoreState, Elf, SystemTextEncoderTBLEncode, MultiByteJPUtil, MyTranslateResource, EtcCacheResource, GitUtil, GitInstaller, AddrResult, ArchSevenZip, NewEventASM, ExportFunction, UpdateInfo, TranslateManager, DisassemblerTrumb, AsmMapSt, GbaBiosCall, R, Log, Mod, PatchDetection, FETextEncode, FETextDecode, TranslateCore, DecreaseColorCore sub-flags)
  - UpdateInfo version tracking and comparison
  - Core package download logic
  - Integration tests for update system
  - E2E CLI tests (`--version` flag, exit codes, output content, `--help` coverage)
  - CLI arg parsing tests (every primary command with complete argument sets — 69 test methods, see `CliArgsE2ETests.cs`)
  - E2E GUI tests (startup window detection, child controls, graceful shutdown)
  - ROM-based E2E CLI tests (`--lint`, `--makeups` × 5 ROMs, `--rebuild` × 2 representative ROMs — skipped without ROMs)
  - ROM-based E2E GUI tests (main form loads, title, child controls × 5 ROMs — skipped without ROMs)
  - Form smoke tests (all toolbar buttons × 5 ROMs — skipped without ROMs)
  - Avalonia editor smoke tests: Unit/Item editor selection (× 5 ROMs — skipped without ROMs)
  - Avalonia all-editors smoke test: all 324 GUI editors open/close (× 5 ROMs — skipped without ROMs)
- Avalonia data verification: `--data-verify` mode cross-checks ViewModel fields against raw ROM bytes, verifies NumericUpDown UI controls display values, validates text encoding (Shift-JIS for JP ROMs, ISO-8859-1 for US ROMs), and skips helper/context-only editors when they have no comparable ROM-backed record instead of reporting false mismatches (× 5 ROMs — skipped without ROMs). `--data-verify-full` mode iterates ALL list items per editor (not just the first) and performs per-field cross-checking via `GetFieldOffsetMap()` to verify each ViewModel field maps to the correct raw ROM byte offset, reporting `FIELDMISMATCH` lines for any discrepancy.
  - **Field completeness tests**: `AvaloniaFieldCompletenessTests` compares WinForms Designer.cs ROM data field controls against Avalonia ViewModel ROM access patterns across all 170 mapped forms (1562 WinForms fields, 0 gaps). Tests are **strict** — they fail on any gap, type/offset mismatch, or unmapped ROM-field form. Includes cross-checks: `AllFormFields_TypeAndOffsetMatch` verifies ROM read types match WinForms field types, `AllViewModels_ReportMethodsAreConsistent` verifies GetDataReport/GetRawRomReport key consistency, `MappedVMs_RawRomReport_CoversRomReads` enforces ≥60% raw ROM report coverage for all mapped VMs, `NoOrphanVMs_ImplementIDataVerifiable` prevents non-data-editor VMs from implementing IDataVerifiable, and `AllDesignerFilesWithRomFields_HaveAvaloniaMapping` auto-discovers ALL Designer.cs files with ROM fields to prevent new forms from being invisible to tests. Orphan cleanup removed IDataVerifiable from 49 non-editor VMs (dialogs, tools, infrastructure). Reports in `docs/field-completeness-report.txt`

## E2E Automation Tests

The project includes a dedicated end-to-end test suite (`FEBuilderGBA.E2ETests`) that covers both CLI and GUI behavior by launching the real application executable.

### Test Categories

| Test File | ROMs required | What it tests |
|-----------|--------------|--------------|
| `Tests/CliTests.cs` | No | CLI flag `--version`: exit code 0, output contains "FEBuilderGBA" and version info |
| `Tests/CliArgsE2ETests.cs` | No | CLI primary commands via `FEBuilderGBA.CLI`: `--help/-h`, `--version`, `--makeups`, `--applyups`, `--lint`, `--disasm`, `--decreasecolor`, `--pointercalc`, `--rebuild`, `--songexchange`, `--convertmap1picture`, `--translate`, `--translate-roundtrip`, `--lastrom`, `--force-detail`, `--translate_batch`, `--test/--testonly`, `--import-battle-anime`, `--export-battle-anime`, `--diff`, `--import-portrait-all`, `--export-map-settings`, `--lz77`, `--checksum`, `--repair-header`, `--rom-info`, `--list-tables`, `--export-palette`, `--import-palette` (plus the unknown-command path) — 69 tests ([docs/cli-args.md](docs/cli-args.md)) |
| `Tests/CliPlaytestE2ETests.cs` | No | `--playtest` help, strict preflight, shipped-runner discovery, paths with spaces, timeout bounds, and synthesized JSON/output-file behavior without requiring Python, mGBA, or a proprietary ROM |
| `Tests/CliRandomMapGeneratorE2ETests.cs` | No | `--generate-random-map` help/validation plus deterministic success, external non-zero exit, malformed MAR, atomic no-output behavior, and temp cleanup through a repository-owned source-built fake FEMapCreator process |
| `Tests/GuiStartupTests.cs` | No | GUI startup: window appears within 30 s, has non-empty title, has child controls, responds to WM_CLOSE |
| `Tests/DiagnosticTests.cs` | No | Diagnostic: logs all window handles, titles (hex-encoded), and class names — always passes |
| `Tests/RomCliTests.cs` | Yes (×5/×2) | `--lint`, `--makeups` × 5 ROMs; `--rebuild` × 2 representative ROMs (FE8U, FE6) — 12 tests, skipped without ROMs |
| `Tests/RomGuiTests.cs` | Yes (×5) | Main form loads per ROM: window appears, non-empty title, ≥10 child controls — 15 tests, skipped without ROMs |
| `Tests/FormSmokeTests.cs` | Yes (×5) | All toolbar buttons clicked per ROM; verifies ≥1 opens a form — 5 tests, skipped without ROMs |
| `Tests/AvaloniaEditorSmokeTests.cs` | Yes (×5) | Avalonia: ROM load + Unit/Item editor selection per ROM — 10 tests, skipped without ROMs |
| `Tests/AvaloniaAllEditorsSmokeTests.cs` | Yes (×5) | Avalonia: all 324 GUI editors opened/closed per ROM via `--smoke-test-all` — 10 tests, skipped without ROMs ([docs/avalonia-gui-forms.md](docs/avalonia-gui-forms.md), [docs/avalonia-forms.md](docs/avalonia-forms.md)) |
| `Tests/CliOutputLogNoRomTests.cs` | No | New CLI output log capture: `--help`, `-h`, `--version`, `--force-detail`, `--test`, `--testonly`, no args, `--bogus-command` — 8 tests |
| `Tests/CliOutputLogRomPart1Tests.cs` | Yes (×5/×2) | New CLI ROM output logs: `--lint` ×5, `--disasm` ×5, `--translate` ×5, `--rebuild` ×2 — 17 tests, skipped without ROMs |
| `Tests/CliOutputLogRomPart2Tests.cs` | Yes (×5/×2) | New CLI ROM output logs: `--makeups` ×5, `--applyups` ×2, `--pointercalc` ×2, `--songexchange` ×2 — 11 tests, skipped without ROMs |
| `Tests/CliOutputLogImageTests.cs` | No | New CLI image output logs: `--decreasecolor` (5 flag variants), `--convertmap1picture` — 6 tests |
| `Tests/WinFormsCliOutputLogNoRomTests.cs` | No | WinForms CLI output log capture: `--version`, no args, `--bogus-command` — 3 tests |
| `Tests/WinFormsCliOutputLogRomTests.cs` | Yes (×5/×2) | WinForms CLI ROM output logs: `--lint` ×5, `--rebuild` ×2, `--makeups` ×5, `--disasm` ×2, `--translate` ×2, `--pointercalc` ×2, `--songexchange` ×2 — 20 tests, skipped without ROMs |
| `Tests/AvaloniaScreenshotTests.cs` | Yes (×2) | Avalonia: captures PNG screenshots of all 324 editors via `--screenshot-all` — 4 tests, skipped without ROMs |
| `Tests/WinFormsScreenshotAllTests.cs` | Yes (×2) | WinForms: screenshots of main form + all toolbar-openable editor forms — 4 tests, skipped without ROMs |
| `Tests/WinFormsScreenshotAllCliTests.cs` | Yes (×2) | WinForms: captures screenshots of all editors via `--screenshot-all` CLI flag — 4 tests, skipped without ROMs |
| `Tests/EditorImageComparisonTests.cs` | Yes (×1) | Cross-platform image export + pixel-perfect comparison for 16 editors: `--export-editor-images` on both WinForms and Avalonia — 3 tests, strict assertions, skipped without ROMs |

**Without ROMs:** 30 passed, 112 skipped. **With all 5 ROMs:** 142 passed, 0 skipped.

### Avalonia UI Automation Testing

All 361 Avalonia `.axaml` files (360 views + 1 dialog) have `AutomationProperties.AutomationId` attributes on every interactive control, enabling reliable UI automation testing with tools like Appium, FlaUI, or MCP Computer Use.

**3,132 unique AutomationIds** follow the naming convention `{EditorName}_{FieldName}_{ControlType}`:

| Suffix | Control Types |
|--------|--------------|
| `_Input` | TextBox, NumericUpDown, Slider |
| `_Combo` | ComboBox |
| `_Button` | Button, MenuItem |
| `_List` | ListBox, ListView, ItemsControl |
| `_Check` | CheckBox, ToggleButton, RadioButton, BitFlagPanel |
| `_Expander` | Expander |
| `_TabControl` / `_Tab` | TabControl, TabItem |
| `_Image` | Image, GbaImageControl, IconPreviewControl |
| `_Label` | TextBlock (dynamic/bound only) |

**Exempt files** (no AutomationIds — reusable controls instantiated multiple times):
- `Controls/BitFlagPanel.axaml`, `Controls/AddressListControl.axaml`, `Controls/GbaImageControl.axaml`, `Controls/IconPreviewControl.axaml`, `Controls/IdFieldControl.axaml`, `App.axaml`

**Scripts:**
- `scripts/add-automation-ids.ps1` — adds/refreshes AutomationIds across all .axaml files
- `scripts/validate-automation-ids.ps1` — validates coverage, naming, and uniqueness (exit 0 = pass, 1 = fail)

**Tests** (`FEBuilderGBA.Avalonia.Tests/AutomationIdTests.cs`):
- Per-editor assertions (UnitEditor, ClassEditor, ItemEditor, MessageBox)
- Naming convention compliance (>99% threshold)
- No duplicate IDs within any single view
- Minimum coverage threshold (>2000 IDs, >90% view coverage)
- Static .axaml source file checks (>95% files have IDs)
- Exempt file verification (reusable controls have no IDs)

### Running E2E Tests Locally

**Prerequisites:**  Build the main app first.

```bash
# Build the main application (Release, x86)
dotnet msbuild FEBuilderGBA.sln /m /restore /t:build /p:Configuration=Release /p:Platform=x86 /warnaserror

# Run without ROMs — 13 passed, 32 skipped (fast, ~20 s)
ROMS_DIR="" dotnet test FEBuilderGBA.E2ETests/FEBuilderGBA.E2ETests.csproj -c Release --no-build

# Run with ROMs — all 45 tests execute
ROMS_DIR=/path/to/roms dotnet test FEBuilderGBA.E2ETests/FEBuilderGBA.E2ETests.csproj -c Release --no-build
```

ROM files expected in `ROMS_DIR`: `FE6.gba`, `FE7J.gba`, `FE7U.gba`, `FE8J.gba`, `FE8U.gba`.
Buildfile success fixtures use a preferred 256 KiB sparse extension clamped to the remaining
32 MiB buildfile budget; insufficient extension headroom is a test failure, not a skip. Avoid
fixture sizing derived from the whole ROM length (for example, `clean.Length * 2`), because
private test ROMs may already contain valid expansions.

If `ROMS_DIR` is **not set at all**, `RomLocator` falls back to a `roms/` directory beside `FEBuilderGBA.sln` (useful during local development).  Set `ROMS_DIR=""` to explicitly suppress that fallback and force all ROM tests to skip.

Or point to an already-built binary:

```bash
export FEBUILDERGBA_EXE=/path/to/FEBuilderGBA.exe
ROMS_DIR="" dotnet test FEBuilderGBA.E2ETests/FEBuilderGBA.E2ETests.csproj -c Release --no-build
```

### CI/CD Integration

E2E tests are split into 6 parallel GitHub Actions workflows (`.github/workflows/e2e-*.yml`) — one no-ROM workflow and one per ROM variant (FE6, FE7J, FE7U, FE8J, FE8U). All share a reusable workflow (`e2e-run.yml`) and run in parallel, reducing wall-clock time from ~30 min to ~12 min. Each per-ROM workflow downloads `roms.zip` but keeps only its target ROM, so tests for other ROMs auto-skip.

ROM-based tests are gated on the `ROMS_URL` repository secret.  When the secret is present the workflow attempts to download `roms.zip`, validate it, extract it, and set `ROMS_DIR` for the test run.  When the secret is absent (forks, external PRs) the Download ROMs step is skipped entirely and all 35 ROM tests skip cleanly.

**ROM download — tiered failure policy:**
| Situation | Behaviour |
|-----------|-----------|
| `ROMS_URL` secret absent | Step skipped; ROM tests skip via `Assert.Skip()` |
| Network/HTTP error (unreachable URL) | Hard fail → pipeline blocked |
| Downloaded file not a valid zip (magic bytes ≠ `PK`) | Warning + exit 0; ROM tests skip |
| Zip structurally corrupt (`ZipFile::OpenRead` fails) | Warning + exit 0; ROM tests skip |
| Zip valid, all 5 ROMs extracted | All 45 tests run |

The step lists every zip entry with its uncompressed size before extraction, so the log shows exactly what is inside `roms.zip`.

**Artifacts produced:**
- `e2e-test-report` — TRX test report (viewable via the **E2E Test Results** check-run posted by `dorny/test-reporter`)
- `e2e-screenshots` — PNG screenshots of all GUI forms captured during E2E tests (Avalonia `Avalonia_*.png` + WinForms `WinForms_*.png`)
- `cli-output-logs` — `.log` files capturing stdout/stderr/exit code for every CLI command (both New CLI and WinForms CLI), useful for regression tracking

**Implementation notes:**
- Tests run sequentially (`[assembly: CollectionBehavior(DisableTestParallelization = true)]`) — each GUI test launches an exclusive app process; concurrent launches cause window-detection races
- Window detection polls **all process windows** via `EnumWindows` rather than relying on `Process.MainWindowHandle`, which can point to a transient splash/startup dialog before the main editor form appears
- Win32 `GetWindowText` P/Invoke uses `CharSet.Unicode` to correctly handle CJK characters; title-based detection is avoided for startup state (the app shows a Chinese "初始设置向导" Init Wizard on first run)
- CLI argument values must use `--key=value` (equals) syntax — `Program.ArgsDic` is built by `U.OptionMap` which only recognises the `=` separator (space-separated values are only picked up via a `File.Exists` fallback, which does not apply to output paths that don't yet exist)
- `AppRunner.Run()` calls `WaitForExit()` (no-param) after `WaitForExit(timeout)` to flush async `OutputDataReceived` events before reading captured stdout
- `RomLocator` treats any explicit `ROMS_DIR` value (even empty string) as an override — only when the variable is **absent** from the environment does the walk-up fallback activate

**Repository-hygiene guardrail:** `.github/workflows/block-binaries.yml` runs on every push (to `master`) and pull request and **fails** if a commit adds or modifies a binary executable, ROM, disk/app image, archive, or secret/certificate/key file. Harmless test fixtures (`*.bin`/`*.dmp`/`*.wav`) and the two tracked binaries (`FEBuilderGBA/lib/7-zip32.dll`, `FEBuilderGBA/test/test.elf`) are allowlisted. This mirrors the `.gitignore` block and complements ggshield + secret-scanning push protection — see [CONTRIBUTING.md](CONTRIBUTING.md#binary-rom--secret-files-do-not-commit).

## 🔄 Update System

> **Cutting a release?** See the full-suite release runbook in **[docs/RELEASE.md](docs/RELEASE.md)** — it covers tagging (`ver_YYYYMMDD.NN`), the per-platform artifacts (WinForms / CLI / Avalonia / Android), and the GitHub-release step.

FEBuilderGBA uses a two-track update model that keeps the application and patch data independent:

### How It Works

| Component | What it contains | How it updates |
|-----------|-----------------|----------------|
| **Core** | FEBuilderGBA.exe, DLLs, config data | Download `FEBuilderGBA_YYYYMMDD.HH.zip` from GitHub Releases or nightly.link |
| **Patch2** | ~44,000 patch files in `config/patch2/` | `git fetch` + `git reset --hard` via the built-in Git updater |

When you check for updates the app compares the remote version against the local assembly build date and shows only the relevant update button(s).

> **Publishing a release (maintainers):** pushing a `ver_YYYYMMDD.HH` tag triggers
> [`.github/workflows/release.yml`](.github/workflows/release.yml), which builds the WinForms desktop package,
> the per-RID self-contained CLI and Avalonia bundles, and the Android APK, then creates the GitHub Release with
> every platform package attached as a zipped asset. The release body is **auto-generated** from the
> conventional-commit history by [`scripts/generate-changelog.sh`](scripts/generate-changelog.sh) — grouped into
> Features / Bug Fixes / Documentation / CI / Maintenance sections (#1632); the full backlog log lives in
> [`CHANGELOG.md`](CHANGELOG.md). See [docs/DEPLOYMENT.md](docs/DEPLOYMENT.md#option-0-automated-tag-triggered-release-recommended) for details.

### Updating Patch2 via Git

The patch database (`config/patch2/`, ~44,000 files) is delivered over git from the separate
[FEBuilderGBA-patch2](https://github.com/laqieer/FEBuilderGBA-patch2) repository — since #1766 it is
**not** bundled in the release, so a freshly-downloaded standalone build ships empty
`FE6/FE7J/FE7U/FE8J/FE8U` stub folders that you fetch on first use.

- **In-app (WinForms):** on the **Welcome screen** click the **update button** ("Update FEBuilderGBA
  to the Latest Version"). When `config/patch2/`
  is empty (a fresh install) the dialog offers a **"Git Patch2"** button
  (`Gitでパッチデータを更新します`) — shown even when the core app is already up-to-date (#1816), and
  even if Git is not installed (it offers to auto-install Git first). Clicking it clones/updates
  patch2 (restart FEBuilderGBA afterwards to apply the new patch data). The **Patch Manager** also
  shows the *"The patch database has not been downloaded yet. Use Check for Updates / Initialize
  Repository to fetch it."* notice when `config/patch2/` is empty, instead of an error (#1811).
- **Manual (any platform):** delete the empty stub folders, then either
  `git clone --depth=1 https://github.com/laqieer/FEBuilderGBA-patch2.git config/patch2`, or
  download & extract the repo's `master.zip` into `config/patch2/`, or reuse a populated
  `config/patch2/` from an older release. To refresh an existing clone: `cd config/patch2 && git pull`.
- **Avalonia:** a discoverable in-app patch2 initialize/update flow is not available yet
  ([#1817](https://github.com/laqieer/FEBuilderGBA/issues/1817)) — the Options window can set the
  remote URL, but use one of the manual methods above to fetch patch2 for now.

The patch2 git source defaults to `github.com/laqieer/FEBuilderGBA-patch2`. To use a
different remote (for example a private mirror), set a custom `submodule_patch2_url`
value in `config.xml`; when present it overrides the default GitHub remote. See
[docs/UPDATE-GUIDE.md](docs/UPDATE-GUIDE.md#first-time-patch2-setup) for a step-by-step
first-time setup and troubleshooting guide.

### Benefits

- ✅ **Incremental patch updates** — only changed patch files are transferred via git
- ✅ **Faster patch updates** — no ZIP download or extraction required
- ✅ **Offline-friendly** — patch2 can be updated separately from the core app
- ✅ **Git history** — full audit trail of every patch data change

### Version Information

- **Core version:** Help → About
- **Patch2 version:** `git -C config/patch2 log -1 --format="%h %s"`

[This fork](https://github.com/laqieer/FEBuilderGBA/) is an integration of several forks of FEBuilderGBA and continues development based on it.

## Optional MCP Computer Use (Windows)

An optional MCP (Model Context Protocol) server gives Copilot CLI screenshot, mouse, and keyboard control for GUI testing. Windows-only, requires Python 3.10+.

### Setup

```bash
# Create venv and install dependencies
cd tools/mcp-computer-use
python -m venv .venv
.venv/Scripts/pip install -r requirements.txt

# Verify server starts (Ctrl+C to stop)
.venv/Scripts/python server.py

# Opt in for this checkout
cd ../..
cp .mcp.example.json .mcp.json
```

The tracked `.mcp.example.json` documents both optional FEBuilder servers. Local `.mcp.json` is ignored, so no workspace MCP starts by default. After copying the example, the computer-use tools (screenshot, click, type_text, key_press, mouse_move, scroll, drag, get_screen_size, wait, find_window, focus_window) appear in a fresh trusted Copilot CLI session. Remove local `.mcp.json` when the GUI session is finished.

## MCP CLI Server (cross-platform)

A second, dependency-free MCP server exposes the `agent-harness` FEBuilderGBA CLI (ROM info/validate,
data export/import/roundtrip, text search/roundtrip, name resolution, linting, image/palette conversion, LZ77, and
session management) as 21 MCP tools plus 3 resources. Unlike the computer-use server above, it needs
only a Python 3.10+ standard library — no Windows dependency, no extra packages, no MCP SDK.
Its closed schemas reject empty file paths, and multi-table exports report only the declared
per-table output names rather than unrelated files sharing the same prefix. The stdio transport
forces UTF-8, rejects non-standard JSON constants and numeric literals that overflow to non-finite
floats, and validates entity IDs as unsigned 32-bit values before backend dispatch. JSON-RPC
request IDs are bounded to 4,096-character strings or 256-bit integers, and excessive integer
tokens or JSON nesting beyond 64 levels produce parse errors without terminating the server
before the next request. JSON-RPC array params remain
structurally valid, but MCP request handlers require objects while notifications never receive
validation responses. The stdio loop removes only CR/LF framing; non-JSON whitespace is left for
the strict decoder to reject. A missing session JSON stays in memory without creating its parent
directory or lock sidecar, so stateless Click commands do not require a writable session
location. ROM headers are read only after the opened descriptor itself
is confirmed to be a regular 1..32 MiB file. MCP checksum is computed locally from that same
validated header buffer, so it never reopens a swappable path in the backend. Session ownership
recognizes symlink/hardlink aliases. Lint findings are classified only from the CLI's explicit
`[ERROR]` and `[WARNING]` severity markers, so the clean `Lint: No errors found.` summary remains
informational. Image
quantization exposes the backend's 2..256 maximum-color contract (or 1 when palette slot zero is
not reserved), and malformed launcher arguments fail before the server can fall back to the
default session. The checksum tool also rejects non-ROM paths before backend invocation while
still reporting a genuine header-checksum mismatch as advisory data; because checksum is local
and header-only, it ignores an explicit or persisted `force_version`. Mutating snapshot commit
also re-reads the validated size and probes for trailing bytes, rejecting shrink or growth before
the original descriptor is touched. MCP bounded
launch/version-probe failures are reported as ordinary unavailable status, including invalid
UTF-8, and shared
Click/MCP session history keeps stable operation identifiers. Failed session writes/deletes
restore the reloaded pre-mutation state, so later requests cannot observe phantom
open/history/close results. Stale close requests are skipped instead of deleting a session that
another process reopened concurrently. Persisted and live history entries are cycle-safe and
bounded to 16 nested levels, 100 collection members, 4,096-character strings/keys, finite JSON
scalars, and 128-character operation names; malformed persisted entries are dropped individually.
Every Click history-producing ROM command uses the same
filesystem-identity ownership rule as MCP, so explicit operations on another ROM cannot alter the
active session while hard-link aliases still count as the same ROM. In-place MCP imports repeat
that identity check while holding the session lock immediately before commit, so replacing the
session path during a hard-link-alias operation cannot mark the replacement modified. Commands
that write a separate output ROM record history without marking the active input ROM modified;
they set `modified` only when the reported destination identifies the active ROM. The `data_import` and
`palette_import` MCP tools have no output-path argument: they overwrite the resolved explicit
`rom_path`, or the active session ROM when `rom_path` is omitted. Successful backend
version probes are limited to 4,096 characters, and `names_resolve` limits each requested name to
4,096 characters with truthful per-name truncation metadata. For MCP tool calls, backend stdout
and stderr are bounded to a 65,536-character decoded prefix while both pipes are concurrently
drained; discarded remainder is still counted for truthful truncation metadata. This pipe-level
bound prevents unbounded backend buffering without changing Click callers' full-capture behavior.
MCP response limits are per field and cardinality rather than one 65,536-character aggregate
envelope: fixed schemas and documented item limits bound the total response, while JSON escaping
may make its serialized representation longer than the retained character counts.
Each bounded backend is also isolated in a POSIX process group or a Windows kill-on-close Job
Object (assigned while the process is suspended). Timeout/error cleanup terminates that whole
lifetime before joining pipe readers, and successful calls close it to reap stray descendants;
Click's legacy subprocess path is unchanged. A POSIX descendant that deliberately creates a new
session is outside this process-group contract.
MCP backend stdin is detached to `DEVNULL`, so a backend tool cannot consume pending JSON-RPC
protocol frames from the long-lived server; bounded MCP capture rejects `capture=False` before
resolver or subprocess execution. `rom_info` and `session_open` now derive metadata only from
their locally validated descriptor and permanently return `lint_output: ""` and
`lint_exit_code: -1` to mean lint was not attempted—call `rom_lint` for explicit linting.
`rom_lint` validates and copies the opened ROM descriptor to a temporary `.gba` snapshot before
the backend sees it. The same private-snapshot protection covers `data_export`, `data_roundtrip`,
`names_resolve`, `text_search`, `text_roundtrip`, and `palette_export` while an MCP request is in
flight, so none of them expose the caller's own path to the backend either. MCP `text_search`
passes its 1..500 result limit into the backend's bounded search command and parses only that
bounded stdout; it does not create a complete temporary TSV export. `data_import` and
`palette_import` mutate that private snapshot instead, and only copy a freshly revalidated result
back through the originally opened descriptor — confirmed by descriptor identity, never by
reopening the path — once the backend exits `0` and the private snapshot has been removed
successfully. The source digest is a point-in-time check immediately before write-back, not a
cross-process content lock: a same-inode writer in the digest-to-write interval remains outside
the protocol and may be overwritten. The write-back is descriptor-bound but not crash-atomic.
For a session-owned ROM, the session lock is acquired before write-back; a lock
timeout therefore leaves the original untouched, while a later session-file failure verifies that
the committed bytes are still unchanged and restores the exact pre-commit bytes through the same
descriptor before returning an error. Rollback refuses any path, size, or content change detected
before restoration; concurrent body writes during restoration remain outside the non-transactional
filesystem contract. A failed pre-write check leaves the original ROM and session history
untouched. Any snapshot path that leaks into backend stdout/stderr is rewritten back to the
caller's real path before an MCP response is returned; replacement expansion is re-capped and the
final response bound is re-enforced before serialization. Snapshot-cleanup errors are path-free
for the same reason, as are validation errors for backend-mutated private snapshots and backend
spawn/timeout/OS errors that contain a registered snapshot spelling. An MCP backend call
for any `--rom` path that was not itself registered this way is refused before the resolver or
subprocess ever runs, while Click continues to pass its own `--rom` path straight through for all
of these commands exactly as before. MCP only accepts an explicit backend, prebuilt apphost, or
prebuilt DLL (`dotnet <dll>`); it never invokes `dotnet run`, build, restore, or NuGet. Click alone
retains the development `dotnet run --project ... --` fallback.

After `pip install -e .` in `agent-harness/`, copy `.mcp.example.json` to local `.mcp.json`
to expose the platform-neutral `cli-anything-febuildergba-mcp` console script as
`febuildergba-cli`; the installation's scripts directory must be on the host `PATH`.
`agent-harness/febuildergba_mcp.py` remains the manual no-install launcher for `python`,
`python3`, or `py -3`. CI preserves the three-OS missing-backend contract and separately runs
public synthetic LZ77 and bounded text-search integrations against a required built .NET apphost.
See
[docs/MCP-SERVER.md](docs/MCP-SERVER.md) for the full tool/resource reference, protocol details,
and setup instructions.

## Context safety (Copilot CLI sessions)

Copilot CLI sessions in this repo manage their context window by **token**
count, but the underlying CAPI backend that serializes a request to the model rejects any payload
above a fixed **byte** ceiling. Those are different quantities: a handful of screenshots or a large
`gh pr diff`/CI log can add megabytes of bytes while barely moving the token count, so a session can
hit the CAPI byte ceiling and fail well before ordinary token-based compaction would ever trigger.
See issue [#1995](https://github.com/laqieer/FEBuilderGBA/issues/1995) for the original report, and
upstream discussions [github/copilot-cli#3767](https://github.com/github/copilot-cli/issues/3767)
and [github/copilot-cli#1688](https://github.com/github/copilot-cli/issues/1688).

**Safeguards in this repo:**
- `scripts/check-copilot-context-size.sh` keeps unconditionally loaded repository instructions below
  12,288 UTF-8 bytes and rejects legacy root agent-instruction files. Detailed process guidance lives
  in the on-demand `.github/skills/` entries and focused docs instead.
- `DEVELOPMENT-WORKFLOW.md`'s "Context Hygiene" section and `.github/copilot-instructions.md`'s
  "Context safety" section require every review/reviewer step to pass identifiers (issue/PR number,
  plan-comment URL, head SHA) rather than embedding full diffs, logs, or images, to spawn a fresh
  child sub-agent per screenshot or safety screen, and to bound every child's final report to ≤ 8 KiB
  of findings + citations (no raw diff hunks, logs, base64, or image bytes).
- `.github/hooks/copilot-context-budget.json` registers a best-effort `preToolUse` command hook
  (`scripts/copilot_context_guard.py`, plus `scripts/copilot-context-guard.sh` /
  `scripts/copilot-context-guard.ps1` platform wrappers) that tracks cumulative bytes of image files
  read through the `view` tool in a session and denies a further read once a conservative budget
  (default 1,250,000 bytes, overridable via `COPILOT_CONTEXT_GUARD_BUDGET_BYTES`) would be exceeded.
  It is fail-open by design: any uncertain, malformed, or infrastructure-failure path abstains
  (`{}`, exit 0) rather than blocking unrelated work, and only a definitive cumulative overflow denies
  (JSON `permissionDecision: "deny"` + a reason, exit 2). Repository hooks only execute when the
  session has repo-hook execution enabled: for any external `copilot -p ...` launch (a fresh session,
  not an already-running interactive one), explicitly **set**
  `GITHUB_COPILOT_PROMPT_MODE_REPO_HOOKS=true` (the literal string `"true"`, not `1` or unset) in the
  environment *before* launch — e.g. `GITHUB_COPILOT_PROMPT_MODE_REPO_HOOKS=true copilot -p ...` on
  bash, or `$env:GITHUB_COPILOT_PROMPT_MODE_REPO_HOOKS = "true"` before the call on PowerShell — and
  then, once inside an interactive session, confirm it took effect with `/env`.
- `.github/copilot/settings.json` pins `contextTier: default`, disables the unrelated
  Superpowers/image-generation plugins and skills for this repository, and keeps image generation's
  MCP server disabled. Per official Copilot CLI docs,
  a repository-level `contextTier` setting takes precedence over a user-level setting **only when
  the working directory is trusted** (untrusted directories fall back to the user-level setting).
  Repo scope generally outranks user scope in the settings merge order (`user` → `repo` → `local`),
  so shipping this file makes `default` context tier apply automatically for any trusted checkout of
  this repo, without requiring each contributor to set it manually — a plain user-scope setting of
  `long_context` will **not** override it. If you deliberately want `long_context` for your own
  session despite this file, the effective override is **`local` scope**, which outranks `repo` in
  the merge order: run `/settings --local contextTier long_context` from within this checkout (not
  the invalid `/settings set contextTier long_context`, which does not target a scope and will not
  beat the repo default). The session launch flag `copilot --context long_context` remains a valid
  way to request `long_context` for that one session's launch.

**Recovery commands (Copilot CLI):**
- `/context` — inspect current context usage before it becomes a problem.
- `/compact` — proactively compact instead of waiting for an automatic/emergency compaction.
- `/rewind` — discard a bad exploratory detour (e.g. after loading an oversized diff/log by mistake)
  without restarting the whole session.
- `/new` — start a clean session once a phase is truly done; session checkpoints and the session's
  `plan.md` preserve continuity so a fresh session can pick the work back up. If a session's hook
  state (`scripts/copilot_context_guard.py`'s persisted byte counters) needs a hard reset rather than
  a normal `/new`, delete its state directory explicitly (see the script's module docstring for the
  resolved path) — there is no automatic mid-session reset, by design, since state must survive
  ordinary compaction.

README for Korean character table
===

It is from an [unofficial build](https://github.com/delvier/FEBuilderGBA) of FEBuilderGBA that supports Korean character table.

The character table used is **Johab**, only for the Hangul Syllables part. If you want to use another character table like Wansung or Windows-949, you may replace __FE\[678\].tbl__ in __./config/translate/ko_tbl__.

Since this fork is incomplete, there might be some issues that raw code points appear can be occurred, e.g. '@61A0' rather than '마' (0xA061) appears. This is likely because the upper bytes from 0xA0 to 0xDF are used for single-byte representation in Shift JIS and Windows-932.

You should change "Text Encoding in ROM" in Options manually every time the ROM is loaded.

Original README
===

FE_Builder_GBA
===
This is a ROM hacking suite for the Trilogy of Fire Emblem games for the Game Boy Advance.
The editor supports
 * FE6 (The Binding Blade)
 * FE7J/FE7U (The Blazing Blade)
 * FE8J/FE8U (The Sacred Stones)
Essentially, both Japanese and North American releases of all games (with the exception of FE6 being Japan-only) are supported.

Starting from the main screen, FEBuilder supports a wide range of functions from image displaying, importing and export of most data, map remodeling, table editing, community patch management, music insertion, and much more.

This suite was made at first to help make my Kaitou patch easier to create!

The origin of the name is from 某LAND.
However, the development language is C#. (We're in this together...)

Of course, it's open source.
The license of the source code is GPL3.
Please use it freely with no limitations.

Much of this project's functions are thanks to the data collected by various communities and people.
We would like to thank our hacking predecessors who have publicly shared any analyzed data.

Details (There is a commentary at the bottom of the page, and the wiki provides other instructions)
https://laqieer.github.io/dw.ngmansion.xyz/wiki/en/guide/febuildergba/index.html

### FE8 Skill Systems

Several FE8 editors — **Spell Menu Extensions**, the **Skill** editors, and **Effectiveness (Skill Systems Rework)** — only show data once a community **Skill System** is installed on the ROM. Recommended sources:

- **FE8U** (US/International): [FireEmblemUniverse/SkillSystem_FE8](https://github.com/FireEmblemUniverse/SkillSystem_FE8) (the canonical Event Assembler buildfile) or [MokhaLeee/fe8u-cskillsys-kernel](https://github.com/MokhaLeee/fe8u-cskillsys-kernel) (a modern C kernel).
- **FE8J** (Japan): [ngmansion/FE8N](https://github.com/ngmansion/FE8N) (the de facto standard FE8J hacking base).

These projects distribute **patches / source**, not the game — apply them to a clean FE8 ROM you dumped yourself. See the [Skill Systems (FE8) wiki page](https://github.com/laqieer/FEBuilderGBA/wiki/Skill-Systems) for installation details.

Some poorly designed anti-virus software may misidentify FEBuilderGBA as a virus.
This is because FEBuilderGBA uses the WindowsDebugAPI to communicate with the emulator.
Please configure your anti-virus to exclude the FEBuilderGBA directory.
FEBuilderGBA is NOT virus.
The source code is all available on github, so you can build it yourself if you are worried.


This software has no association with the official products.
We do not need any donations as we are making this software non-commercial.

If you really want to donate to someone, donate to the charitable organization supporting the freedom of speech on the Internet, **Freedom of Expression**, including the **EFF Electronic Frontier Foundation**.

Of course, you are free to write articles about FEBuilderGBA.
In some cases, you may earn some pocket money through affiliates. :)
However, please do it at your own risk. :(

If you have something you do not understand through hacking or the editor, please read "Manual" in "Help".
If you find a bug that you cannot solve by any means, use **Help → Report a Bug…** (Avalonia GUI), or the **Report a Bug on GitHub** button in the Problem Report Tool (WinForms GUI), to open a pre-filled GitHub issue form; the tool also captures a screenshot for you to attach (drag-and-drop in Avalonia, or paste from the clipboard in WinForms). Alternatively, please create a report.7z from **File → Problem Report Tool** and consult with the community.
https://discordapp.com/invite/Yzztqqa
Do NOT send your ROM (.gba) directly.

SourceCode:
https://github.com/FEBuilderGBA/FEBuilderGBA

Installer:
https://github.com/FEBuilderGBA/FEBuilderGBA_Installer/releases/download/ver_20200130.17.1/FEBuilderGBA_Downloader.exe


FE_Builder_GBA
===
FE GBA 3部作のROMエディターです。
FE8J FE7J FE6 FE8U FE7U に対応しています。

Project_FE_GBA の画面を参考に、
新規に判明した部分を追加しました。
画像表示やインポートエクスポート、マップ改造まで幅広い機能をサポートします。

怪盗パッチを作っているときに思った、こんな機能が欲しい!!という機能をすべて入れ込みました。

名前の由来は、 某LANDのアレからです。
ただし、開発言語はC# です。 (中の人達は一緒だしね・・・)
C#でありますが、特にパフォーマンスに注意しているので、サクサク動くかと思います。

当然、オープンソース。ソースコードのライセンスは GPL3 です。
ご自由にご利用ください。

これを作るのに、いろいろいなデータ、コミニティを参考にしました。
解析したデータを公開してくれた先人にお礼を申し上げます。


詳細 (ページ下部に解説集があるよ)
https://laqieer.github.io/dw.ngmansion.xyz/wiki/guide/febuildergba/index.html

一部の出来の悪いアンチウイルスソフトが、FEBuilderGBAをウイルスと誤認することがあるようです。
これは、FEBuilderGBAがエミュレータと通信するためにWindowsDebugAPIを利用しているからだと思います。
もしそうなったら、アンチウイルスの設定で、FEBuilderGBAディレクトリを除外してください。
FEBuilderGBAはウイルスではありません。
ソースコードはすべてgithubで公開しているので、心配な場合は自分でビルドしてください。


このソフトウェアは、公式とは一切関係ありません。
私達は非営利でこのソフトウェアを作っているので、寄付を必要としません。
どうしても寄付したい方は、EFF 電子フロンティア財団を始めとする、インターネットでの言論の自由、表現の自由を支援している慈善団体にでも寄付してください。

もちろん、あなたがFEBuilderGBAに関する記事を書くのは自由です。
場合によっては、アフェリエイトでお小遣いを稼ぐこともできるでしょう。 :)
ただし、あなたの責任において実施してください。 :(

もし、hackromでわからないことがあれば、「ヘルプ」の「マニュアル」を読んでください。
どうしても解決しないバグが発生した場合は、**ヘルプ → バグを報告…**（Avalonia版）、またはWinForms版では問題報告ツールの「GitHubでバグを報告」ボタンから、GitHubの問題報告フォームを開けます（スクリーンショットも保存されるので、ドラッグまたは貼り付けで添付してください）。または、「ファイル」->「問題報告ツール」から、report.7zを作成して、コミュニティに相談してください。
https://discordapp.com/invite/Yzztqqa
(ROMは送信しないでください。)

SourceCode:
https://github.com/FEBuilderGBA/FEBuilderGBA

Installer:
https://github.com/FEBuilderGBA/FEBuilderGBA_Installer/releases/download/ver_20200130.17.1/FEBuilderGBA_Downloader.exe


FE_Builder_GBA
===
它是FE GBA三部曲的ROM编辑器。
它对应于 FE8J FE7J FE6 FE8U FE7U.

参考Project_FE_GBA的屏幕，
我添加了一个新发现的部分。
我们支持图像显示，导入导出，地图重构等功能。

当我制作一个kaitou补丁时，我想要这样的功能

这个名字的起源是来自 某LAND。
但是，开发语言是C＃。 （里面的人在一起...）
它是C＃，但我担心性能，所以我认为它会工作很好。

当然，开源。源代码的许可证是GPL3。
请自由使用。

我参考了各种数据和社区来做到这一点。
我要感谢发布分析数据的前辈。


详细信息（页面底部有评论）
https://laqieer.github.io/dw.ngmansion.xyz/wiki/zh/guide/febuildergba/index.html

Some poorly designed anti-virus software may misidentify FEBuilderGBA as a virus.
This is because FEBuilderGBA uses the WindowsDebugAPI to communicate with the emulator.
Please configure your anti-virus to exclude the FEBuilderGBA directory.
FEBuilderGBA is NOT virus.
The source code is all available on github, so you can build it yourself if you are worried.


这个软件与官方无关。
我们不需要捐赠，因为我们正在制作该软件的非营利。
如果你真的想捐赠，
捐赠给支持言论自由的慈善组织，包括EFF电子前沿基金会在内的言论自由

当然，您可以自由撰写关于FEBuilderGBA的文章。
在某些情况下，您可以通过会员赚取零用钱。 :)
但是，请自行承担风险。 :(

如果你有一些你从hackrom不能理解的东西，请阅读“帮助”中的“手册”。
如果您发现无法解决的错误，请使用 **帮助 → 报告错误…**（Avalonia版），或在WinForms版的问题报告工具中点击“在GitHub上报告错误”按钮，打开预填的GitHub问题表单（工具会保存截图，请拖放或粘贴后附加）；或在'文件' -> '问题报告工具'中创建report.7z，并咨询社区。
https://discordapp.com/invite/Yzztqqa
（请不要发送ROM。）

SourceCode:
https://github.com/FEBuilderGBA/FEBuilderGBA

Installer:
https://github.com/FEBuilderGBA/FEBuilderGBA_Installer/releases/download/ver_20200130.17.1/FEBuilderGBA_Downloader.exe
