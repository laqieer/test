# FEBuilderGBA.Android (Avalonia.Android head)

> **Builds an APK and boots on an emulator (#1640); interactive ROM-editing UX
> remains preview/on-device-unvalidated.** Part of epic
> [#1070](https://github.com/laqieer/FEBuilderGBA/issues/1070). The structural
> prerequisite landed in
> [#1121](https://github.com/laqieer/FEBuilderGBA/issues/1121) — this head now
> builds a real APK against the shared, conditionally multi-targeted
> `FEBuilderGBA.Avalonia`. Read the full feasibility assessment first:
> **[docs/ANDROID.md](../docs/ANDROID.md)**.

This is a minimal Avalonia.Android *head* that reuses the shared
`FEBuilderGBA.Avalonia` UI (which transitively pulls in `FEBuilderGBA.Core` +
`FEBuilderGBA.SkiaSharp`). The WinForms project is **never** referenced — it is
not Android-capable.

## Why it is NOT in `FEBuilderGBA.sln`

`.github/workflows/check.yml` builds the **entire solution** on a `windows-latest`
runner that does **not** have the `android` .NET workload. Adding this
`net10.0-android` project to `FEBuilderGBA.sln` would break the required `build`
check for every unrelated PR. It is therefore intentionally excluded and built
standalone.

## Build

```bash
dotnet workload install android          # one-time
dotnet build FEBuilderGBA.Android/FEBuilderGBA.Android.csproj -c Release -p:EnableAndroidTarget=true
```

The `-p:EnableAndroidTarget=true` flag is **required**. `AdditionalProperties` on
the `ProjectReference` correctly activates the android TFM for the *build* phase,
but NuGet *restore* uses a separate static graph that ignores per-reference
`AdditionalProperties`, so without the global property restore writes a
net10.0-only assets file for `FEBuilderGBA.Avalonia` and the build fails with
`NETSDK1005`. Passing it as a global property flows it to the referenced
project's restore + build. A successful build emits
`com.laqieer.febuildergba-Signed.apk` under
`bin/Release/net10.0-android/`.

## Current build status (honest)

- ✅ A minimal `net10.0-android` project builds + packages a signed APK with the
  workload installed (toolchain verified).
- ✅ This head's own code (`MainActivity`) **compiles** against the shared
  `FEBuilderGBA.Avalonia` reference.
- ✅ **The full APK builds (#1121).** `FEBuilderGBA.Avalonia` conditionally
  multi-targets `net10.0;net10.0-android` (opt-in via `EnableAndroidTarget`,
  default OFF); the `ProjectReference` below activates the android TFM with
  `AdditionalProperties="EnableAndroidTarget=true"` (an MSBuild-verified
  requirement). `dotnet build … -c Release` produces an APK under
  `bin/Release/net10.0-android/`. On the android TFM the shared project excludes
  `Program.cs`, `Avalonia.Desktop`, `app.manifest`, the `GapSweep` Roslyn
  dev-tooling, and `Microsoft.CodeAnalysis.CSharp`.
- ✅ **The single-view editor-launcher shell (#1122) landed and boots on the
  emulator (#1640).** The boot smoke launches the real APK and reaches the
  Avalonia single-view shell.
- ✅ **config asset bundling + first-run extraction (#1123) is exercised by the
  boot smoke.**
  `config/**` (excluding `patch2`) ships as `<AndroidAsset>`; `MainActivity.OnCreate`
  extracts it once (version-stamped, idempotent, via the pure
  `FEBuilderGBA.Core/AndroidConfigExtractorCore`) into `Context.FilesDir` and
  points the android-only `App.BaseDirectoryOverride` at it before the app boots,
  so Core's `PathUtil.ConfigPath` resolves on Android. The extraction logic is
  desktop-unit-tested; the APK is verified to contain `assets/config/...`
  (excluding `patch2`), and #1640's fail-fast boot smoke catches extraction
  failures.
- ✅ **The SAF stream seam (#1124) is done.** ROM load/save uses stream APIs
  suitable for Android storage.
- **Offline patch-database ZIP import:** load a ROM and use Patch Manager's
  **Import Patch Database ZIP** action. SAF streams are validated and staged
  into the loaded version's app-private canonical library; confirmation
  defaults to No. No patches are applied. Imported data is preserved across
  relaunch and bundled-config refresh. See
  [format, limits and recovery](../docs/ANDROID.md#52-offline-patch-database-zip-import).
- ⚠️ **Interactive ROM editing remains on-device-unvalidated/preview.** The boot
  smoke does not drive the system picker, ROM open/save interaction, editors,
  touch UX, or dialogs. See
  [docs/ANDROID.md §7](../docs/ANDROID.md#7-build-status-in-this-environment).

## Known skeleton limitations

- The #1122 single-view shell is landed and booted by #1640, but interactive
  editor navigation, touch UX, and attached-window dialog flows remain
  on-device-unvalidated.
- `config/` asset bundling + first-run extraction (#1123) is exercised by the
  #1640 boot smoke. The #1124 SAF stream seam is done, but the boot smoke does
  not drive the system picker or ROM open/save/editor interaction.
- `config/patch2` is **not bundled** for Android. Offline ZIP import is separate
  from Git/HTTP delivery, which remains unavailable; FE-Repo and unsupported
  EA/unknown patch execution are not enabled by import.
- The dedicated `android-patch-import-smoke` workflow job exercises actual
  SAF/import/relaunch/config-upgrade behavior with generated fixtures and a
  tablet-sized API-34 emulator. Its report/screenshots need independent review;
  ordinary boot/parity or fake-adb test success does not establish import proof.
