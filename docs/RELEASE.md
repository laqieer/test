# Full-Suite Release Runbook

End-to-end runbook for cutting a FEBuilderGBA release across **all five ship targets**:

| Target | What ships | Built by | Runtime |
|--------|-----------|----------|---------|
| **WinForms** | `FEBuilderGBA.exe` + DLLs + `config/` + `tools/bin/` (bundled ColorzCore) | [`msbuild.yml`](../.github/workflows/msbuild.yml) | Windows (x86) |
| **CLI** | `cli-{rid}` self-contained bundle (+ bundled ColorzCore) | [`crossplatform.yml`](../.github/workflows/crossplatform.yml) | Linux / macOS / Windows |
| **Avalonia desktop** | `avalonia-{rid}` self-contained bundle (+ bundled ColorzCore) | [`crossplatform.yml`](../.github/workflows/crossplatform.yml) | Linux / macOS / Windows |
| **Android APK** | `*-Signed.apk` (debug keystore — see caveat) | [`android.yml`](../.github/workflows/android.yml) | Android |
| **iOS IPA** | `*-unsigned.ipa` (unsigned — see caveat; **soft/advisory**) | [`ios.yml`](../.github/workflows/ios.yml) | iOS / iPadOS |

> **What's live today:** the release process is **automated** — [`release.yml`](../.github/workflows/release.yml) ([#1629](https://github.com/laqieer/FEBuilderGBA/issues/1629)) is **merged and in-tree**. Pushing a `ver_*` tag builds every platform and runs `softprops/action-gh-release` to create the GitHub release with all assets attached (Section 5, the primary path). The manual `gh release create` flow (Section 4) remains as a **fallback** for hand-cut releases.
>
> This runbook is part of the release-readiness umbrella **[#1628](https://github.com/laqieer/FEBuilderGBA/issues/1628)**. For the WinForms split-package update system see [DEPLOYMENT.md](DEPLOYMENT.md) (and its **Status** caveat below).

---

## 1. Version & tag scheme

Releases are tagged **`ver_YYYYMMDD.NN`**:

- `YYYYMMDD` — the build date (UTC).
- `NN` — a two-digit sequence within that day, starting at `00` (or the hour of the build — both forms appear in history). Increment for each additional release on the same day.

Recent tags: `ver_20260204.22`, `ver_20250926.15`, `ver_20250423.22`. List the latest with:

```bash
git tag --sort=-creatordate | head -5
```

The WinForms **core version** is derived from the assembly build date (`YYYYMMDD.HH`); the in-app updater compares it against the release tag. The patch database (`config/patch2/`) is versioned independently via `config/patch2/version.txt` — see [DEPLOYMENT.md → Versioning Strategy](DEPLOYMENT.md#versioning-strategy).

---

## 2. CI artifact inventory (what each workflow produces)

Every push to `master` runs the build workflows and uploads artifacts via `actions/upload-artifact@v4` (≈90-day expiry, login-gated — these are **not** release assets until you attach them). Download from the **[Actions](https://github.com/laqieer/FEBuilderGBA/actions)** tab → the relevant workflow run → **Artifacts**.

| Workflow | Artifact name | Contents |
|----------|---------------|----------|
| `msbuild.yml` | `FEBuilderGBA_{build_time}` | `FEBuilderGBA.exe`, `*.dll`, `*.json`, `config/` (with empty `patch2/{FE6,FE7J,FE7U,FE8J,FE8U}` placeholders — patch2 ships via git), `tools/bin/` (self-contained ColorzCore + `lyn.exe`), `README*.md` |
| `crossplatform.yml` | `cli-linux-x64`, `cli-osx-arm64`, `cli-win-x64` | Self-contained CLI per RID + `config/` (incl. the `config/patch2/` patch database — [#1630](https://github.com/laqieer/FEBuilderGBA/issues/1630)) + `tools/bin/` ColorzCore |
| `crossplatform.yml` | `avalonia-linux-x64`, `avalonia-osx-arm64`, `avalonia-win-x64` | Self-contained Avalonia desktop per RID + `config/` (incl. the `config/patch2/` patch database — [#1630](https://github.com/laqieer/FEBuilderGBA/issues/1630)) + `tools/bin/` ColorzCore |
| `android.yml` | `febuildergba-android-apk` | `*-Signed.apk` (debug keystore) — **advisory / non-required** check (`android-build`), so a flaky Android build can never block a merge |
| `ios.yml` | `febuildergba-ios-ipa` | unsigned `*.ipa` (Payload/) — **advisory / non-required** check (`ios-build`), macOS-only; a flaky iOS build can never block a merge |

**RIDs published:** `linux-x64`, `osx-arm64`, `win-x64`.

> **Android signing caveat:** `android.yml` produces a **debug-keystore**-signed APK only. A production-signed (release) APK/AAB is **not** yet produced — tracked by [#1631](https://github.com/laqieer/FEBuilderGBA/issues/1631). Do not publish the debug APK as a trusted production download without flagging it.

> **iOS signing caveat:** `ios.yml` produces an **unsigned** `.ipa` unless the maintainer adds the `APPLE_*` secrets (see [docs/IOS.md §6](IOS.md#6-build--ci)). An unsigned `.ipa` is **not directly installable** — it is for downstream re-signing / sideloading (AltStore, Sideloadly, Apple Configurator). iOS is a **preview** and is a **soft/advisory** release asset ([#1859](https://github.com/laqieer/FEBuilderGBA/issues/1859)) — its `release.yml` job is `continue-on-error` and not in the mandatory verify-assets list, so a missing iOS asset never blocks the other platforms.

> **patch2 in cross-platform bundles:** the `cli-{rid}` / `avalonia-{rid}` bundles **do** embed the `config/patch2/` patch database ([#1630](https://github.com/laqieer/FEBuilderGBA/issues/1630)) — the CLI and Avalonia projects copy `config/patch2/**` into their output (only the submodule `.git` plumbing is excluded), so the Patch Manager is populated and `--list-patches` works on a fresh extract. This requires the patch2 submodule to be initialized at publish time (`git submodule update --init config/patch2`); the cross-platform build inits it before publishing.

### FE-Repo / FE-Repo-Music resources (on demand)

The **FE-Repo** (graphics) and **FE-Repo-Music-No-Preview** (music) public asset repositories are wired into the in-app **FE-Repo Resource Browser** (portrait / icon / background / CG / battle-background / skill-icon / song editors, WinForms + Avalonia). They are git submodules under `resources/`, but their payload is too large to attach to every release, so **no artifact bundles them** — `release.ps1`, `release.yml`, and `msbuild.yml` all stage only exe/dll/json/config/tools/README/LICENSE. As a result the Resource Browser is **empty out of the box** until the user fetches the resources on demand ([#1644](https://github.com/laqieer/FEBuilderGBA/issues/1644); the graceful empty-state is [#1380](https://github.com/laqieer/FEBuilderGBA/issues/1380)).

**Release-note text (paste into release notes):**

> The FE-Repo Resource Browser needs the public graphics/music asset repos, which are not bundled in this download. To populate it, run **one** of the following next to the executable:
> - Released `.zip` (no git repo): `git clone --depth 1 https://github.com/Klokinator/FE-Repo resources/FE-Repo` (and `git clone --depth 1 https://github.com/laqieer/FE-Repo-Music-No-Preview resources/FE-Repo-Music-No-Preview` for music).
> - Source clone: `git submodule update --init resources/FE-Repo` (and `resources/FE-Repo-Music-No-Preview`), or the helper `scripts/fetch-fe-repo.sh` / `pwsh scripts/fetch-fe-repo.ps1`.
>
> Until then the browser shows an actionable empty-state with both commands and a **Copy git command** button.

> **Note:** the `git clone` form is the **released-build path** — it needs neither a git repo nor the `scripts/` folder (which shipped artifacts do not include), only `git` on PATH, and it clones straight into the `resources/` folder the browser searches. `scripts/fetch-fe-repo.{sh,ps1}` are **source-tree convenience helpers** that auto-detect a clone (submodule init) vs. a non-git tree (shallow clone); they are not referenced from the shipped-zip UX because they are not part of the release artifacts.

---

## 3. Building the cross-platform bundles locally

If you want to produce the CLI/Avalonia bundles without waiting for CI (e.g. for a manual release), use the helper:

```bash
# Requires the tool submodules:
git submodule update --init config/patch2 tools/Event-Assembler tools/ColorzCore

# Build all three RIDs (or pass a subset):
./scripts/publish-all.sh                      # linux-x64 osx-arm64 win-x64
./scripts/publish-all.sh linux-x64

# Output: publish/cli-{rid}/, publish/avalonia-{rid}/, and per-RID .tar.gz archives
```

For the WinForms package, build the solution (`Release`/`x86`) then stage with **`release.ps1`** (Section 4.1).

---

## 4. Manual release (fallback path)

The automated tag-push flow (Section 5) is the **primary** path. This manual
process is the **fallback** for hand-cutting a release (e.g. re-attaching an
asset, or publishing outside CI). It produces a GitHub release with all-platform
assets.

### 4.1 Stage the WinForms package — `release.ps1`

After building the solution in `Release`/`x86`:

```powershell
# From repo root. Stages exe + DLLs + config + docs into .\release\
pwsh ./release.ps1                      # default -OutputDir release
pwsh ./release.ps1 -OutputDir dist -Clean
```

`release.ps1`:
- Stages `FEBuilderGBA.exe`, `*.dll`, `*.json`, the **Release** `config/` (falling back to the repo-root `config/`), and the docs `*.md`.
- Creates empty `config/etc` and `config/log` staging folders.
- If `publish/cli-*` / `publish/avalonia-*` bundles exist (from Section 3), stages them alongside too — so a single run can mirror the full-suite asset set.
- Is **idempotent** and **parameterized** (`-OutputDir`, `-WinFormsBinDir`, `-ConfigDir`, `-Clean`).

A no-build smoke test covers the staging behavior:

```powershell
pwsh -NoProfile -File scripts/release.test.ps1
```

### 4.2 Collect every platform asset

Gather one zip per platform (download CI artifacts from Section 2, or build locally):

- `FEBuilderGBA_{ver}.zip` — the WinForms package (from `release.ps1` staging, zipped).
- `cli-linux-x64.zip`, `cli-osx-arm64.zip`, `cli-win-x64.zip`.
- `avalonia-linux-x64.zip`, `avalonia-osx-arm64.zip`, `avalonia-win-x64.zip`.
- `FEBuilderGBA-android-Signed.apk` (debug-signed — see caveat in Section 2).

### 4.3 Create the GitHub release

```bash
VER="ver_$(date -u +%Y%m%d).00"      # e.g. ver_20260227.00 — bump NN as needed

git tag "$VER"
git push origin "$VER"

gh release create "$VER" -R laqieer/FEBuilderGBA \
  --title "$VER" \
  --notes "Release $VER — full-suite (WinForms + CLI + Avalonia + Android)." \
  FEBuilderGBA_${VER}.zip \
  cli-linux-x64.zip cli-osx-arm64.zip cli-win-x64.zip \
  avalonia-linux-x64.zip avalonia-osx-arm64.zip avalonia-win-x64.zip \
  FEBuilderGBA-android-Signed.apk
```

### 4.4 Verify

```bash
gh release view "$VER" -R laqieer/FEBuilderGBA --json assets --jq '.assets[].name'
```

Confirm every platform download is listed and downloadable.

---

## 5. Automated release (live — #1629, primary path)

> **Status: live.** [`release.yml`](../.github/workflows/release.yml) ([#1629](https://github.com/laqieer/FEBuilderGBA/issues/1629)) is **merged and in-tree**. This is the **primary** release path; Section 4 is the manual fallback.

The workflow is triggered on `push: tags: ['ver_*']` and

1. builds/collects the WinForms package, the per-RID `cli-{rid}` and `avalonia-{rid}` bundles, and the Android APK,
2. runs `softprops/action-gh-release` and attaches all of them as release assets (one zip per platform),
3. thereby publishes the GitHub release with all assets attached.

The release flow is therefore: **bump the tag → push it → CI does the rest.**

---

## 6. Pre-release checklist

- [ ] `master` is green on `msbuild.yml`, `crossplatform.yml`, and `check.yml`.
- [ ] Tag name follows `ver_YYYYMMDD.NN` and does not already exist.
- [ ] `config/patch2/version.txt` is current if patches changed (see [DEPLOYMENT.md](DEPLOYMENT.md#patch2-version-management)).
- [ ] For offline patch-import changes, affected Core/Avalonia suites and the real Android SAF import/rejection/relaunch/config-upgrade proof pass at the reviewed source revision. Inspect the actual screenshots and hash report; fake-adb tests or boot/parity success alone are not import proof.
- [ ] All four platform assets collected (WinForms zip, 3× CLI, 3× Avalonia, Android APK).
- [ ] `LICENSE` + `THIRD-PARTY-NOTICES.md` present inside every artifact: the WinForms zip via `release.ps1`, the CLI/Avalonia bundles via `crossplatform.yml`, and the Android APK via `<AndroidAsset>` entries in `FEBuilderGBA.Android/FEBuilderGBA.Android.csproj` (both files land under the APK's `assets/`) — GPLv3 compliance, [#1633](https://github.com/laqieer/FEBuilderGBA/issues/1633).
- [ ] Release notes / changelog drafted (changelog automation tracked by [#1632](https://github.com/laqieer/FEBuilderGBA/issues/1632)).
- [ ] `gh release create` run with every asset attached.
- [ ] GitHub release verified.

### Known release-readiness gaps (umbrella #1628)

These do **not** block a manual WinForms release, but flag them in release notes when relevant:

| Gap | Issue |
|-----|-------|
| Tag-triggered release workflow — **delivered** (`release.yml`, Section 5); the manual Section 4 is now only a fallback | [#1629](https://github.com/laqieer/FEBuilderGBA/issues/1629) (merged) |
| FE-Repo / FE-Repo-Music resources not bundled — fetch on demand (see [§2 → FE-Repo / FE-Repo-Music resources](#fe-repo--fe-repo-music-resources-on-demand)) | [#1644](https://github.com/laqieer/FEBuilderGBA/issues/1644) |
| Release-signed (non-debug) Android APK/AAB | [#1631](https://github.com/laqieer/FEBuilderGBA/issues/1631) |
| Changelog / release-notes generation | [#1632](https://github.com/laqieer/FEBuilderGBA/issues/1632) |
| Code-sign / notarize Windows + macOS artifacts — **conditional, secret-gated** ([§6.1](#61-code-signing--notarization-1634)): the CI wiring is in place, but artifacts stay **unsigned until the maintainer adds the certificate secrets** | [#1634](https://github.com/laqieer/FEBuilderGBA/issues/1634) |
| Android: Git/HTTP patch2 delivery and FE-Repo remain unavailable; the separate offline patch-database ZIP import does not bundle data or enable unsupported patch execution — see [Android storage/import scope](ANDROID.md#52-offline-patch-database-zip-import) | [#1641](https://github.com/laqieer/FEBuilderGBA/issues/1641), [#2158](https://github.com/laqieer/FEBuilderGBA/issues/2158) |
| iOS: preview head — builds an unsigned `.ipa`; on-device runtime (touch UX, file pickers, AOT/trim) unvalidated, per-editor file-flow guards not yet extended to iOS, patch2/FE-Repo not bundled (same as Android) — see [docs/IOS.md](IOS.md) | [#1859](https://github.com/laqieer/FEBuilderGBA/issues/1859) |
| iOS: release-signed (non-unsigned) `.ipa` + App Store / TestFlight distribution (needs a paid Apple Developer account + `APPLE_*` secrets) | [#1859](https://github.com/laqieer/FEBuilderGBA/issues/1859) |

---

## 6.1. Code-signing & notarization (#1634)

Code-signing (Windows Authenticode) and macOS codesign + notarization are wired
into the build workflows but are **conditional**: they activate **only when the
maintainer adds the relevant certificate secrets** to **Settings → Secrets and
variables → Actions**. This mirrors the conditional Android release signing
([#1654](https://github.com/laqieer/FEBuilderGBA/pull/1654)).

> **Default (no-secret) behaviour — the fork's current state:** with the secrets
> absent, **every signing step is skipped** and the workflows produce the same
> **unsigned** artifacts as before, so CI stays green. Unsigned artifacts still
> trigger **SmartScreen "unknown publisher"** (Windows) and **Gatekeeper
> "unidentified developer"** (macOS) — see the workaround below. This row stays a
> caveat in the gaps table until the secrets are configured and a download is
> verified to open without a hard block.

### Required secrets (set ALL of a platform's secrets, or NONE)

A **partial** set **fails the workflow fast** (clear `::error::`), so a
half-configured maintainer never gets a silent unsigned build or a confusing
publish failure.

**Windows — Authenticode** (signs `FEBuilderGBA.exe` in `msbuild.yml`, and the
`win-x64` CLI/Avalonia `.exe` in `crossplatform.yml`):

| Secret | Meaning / how to produce |
| --- | --- |
| `WINDOWS_CERT_BASE64` | base64 of the code-signing certificate `.pfx`/`.p12` — `base64 -w0 cert.pfx` (or `certutil -encode` on Windows, stripped of header/footer) |
| `WINDOWS_CERT_PASSWORD` | the `.pfx` export password |

The exe is signed with **SHA-256** + an **RFC-3161 timestamp**
(`http://timestamp.digicert.com`) so the signature outlives the certificate. The
PFX is imported into the runner's `CurrentUser\My` store and signed **by
thumbprint** with `signtool` (located from the Windows SDK), so the password is
never passed on a command line; the cert is removed afterwards.

**macOS — Developer ID codesign + notarization** (signs the `osx-arm64`
CLI/Avalonia bundles in `crossplatform.yml`):

| Secret | Meaning / how to produce |
| --- | --- |
| `APPLE_CERT_BASE64` | base64 of the **Developer ID Application** certificate `.p12` — `base64 -i cert.p12 -o -` |
| `APPLE_CERT_PASSWORD` | the `.p12` export password |
| `APPLE_ID` | the Apple ID (email) used for notarization |
| `APPLE_TEAM_ID` | the 10-character Apple Developer Team ID |
| `APPLE_APP_PASSWORD` | an **app-specific password** for that Apple ID (appleid.apple.com → Sign-In and Security) |
| `APPLE_SIGN_IDENTITY` *(optional)* | explicit signing-identity string; auto-detected from the imported cert when omitted |

The `.p12` is imported into a throwaway keychain; **every Mach-O payload** is
codesigned with the hardened runtime (**libraries first, the executable last**),
then a `ditto` zip of each bundle is submitted to `notarytool submit --wait`.

> **Stapling note (honest framing):** the artifacts are raw `dotnet publish`
> **directories**, not `.app`/`.pkg`/`.dmg` bundles, so a directory/CLI binary
> **cannot be stapled**. Notarization still attaches the ticket to Apple's online
> service, so a notarized binary passes Gatekeeper via **online validation**. The
> workflow staples only if a real `.app` bundle is present in the output.
> Shipping a stapled `.dmg`/`.pkg` installer is a possible future enhancement.

### Workaround for the unsigned (no-secret) artifacts

- **Windows:** on the SmartScreen prompt choose **More info → Run anyway**.
- **macOS:** clear the quarantine attribute after download —
  ```bash
  xattr -dr com.apple.quarantine /path/to/FEBuilderGBA-avalonia-osx-arm64
  ```
  (or right-click → Open once to add a Gatekeeper exception).

---

## 7. Rollback

If a release has critical issues, mark it as not-latest and ship a hotfix:

```bash
gh release edit "$VER" -R laqieer/FEBuilderGBA --prerelease        # demote from "latest"
# then cut a fixed release with a new tag (ver_YYYYMMDD.NN+1)
```

See [DEPLOYMENT.md → Rollback Procedure](DEPLOYMENT.md#rollback-procedure) for the split-package specifics.

---

## References

- [DEPLOYMENT.md](DEPLOYMENT.md) — WinForms split-package (FULL/CORE/PATCH2) update system.

  > **Status:** the split-package `.7z` generator (`scripts/create-split-packages.ps1`) and a `split-packages_{buildTime}` CI artifact described in DEPLOYMENT.md are **not present in the current tree** — `msbuild.yml` uploads only the single `FEBuilderGBA_{build_time}` artifact (Section 2). Treat the split-package flow in DEPLOYMENT.md as the design of the in-app updater, not a step you can run today. The live artifact set is the one in Section 2.
- Workflows: [`msbuild.yml`](../.github/workflows/msbuild.yml) · [`crossplatform.yml`](../.github/workflows/crossplatform.yml) · [`android.yml`](../.github/workflows/android.yml) · [`ios.yml`](../.github/workflows/ios.yml)
- Scripts: [`release.ps1`](../release.ps1) (WinForms staging) · [`scripts/publish-all.sh`](../scripts/publish-all.sh) (CLI/Avalonia bundles) · [`scripts/release.test.ps1`](../scripts/release.test.ps1) (release.ps1 smoke test)
- [docs/ANDROID.md](ANDROID.md) — Android build & signing details.
- Umbrella: [#1628](https://github.com/laqieer/FEBuilderGBA/issues/1628) (release readiness) · automation: [#1629](https://github.com/laqieer/FEBuilderGBA/issues/1629)
