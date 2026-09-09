using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using FEBuilderGBA.Avalonia.Services;
using FEBuilderGBA.SkiaSharp;

namespace FEBuilderGBA.Avalonia
{
    public partial class App : Application
    {
        /// <summary>Config key used to persist the theme preference.</summary>
        internal const string ThemeConfigKey = "Avalonia_Theme";

        /// <summary>Returns true when the app is currently using dark mode.</summary>
        public bool IsDarkMode => RequestedThemeVariant == ThemeVariant.Dark;

        /// <summary>ROM path passed via --rom command line argument.</summary>
        public static string? StartupRomPath { get; set; }

        /// <summary>
        /// #1122 build proof: when set (via <c>--render-mainview=&lt;path&gt;</c>),
        /// the desktop app renders the Android single-view shell
        /// (<see cref="Views.MainView"/>) to a PNG using the real desktop Skia
        /// platform (the headless test platform's <c>UseHeadlessDrawing</c> does
        /// not rasterize), then exits. Lets a no-device environment capture the
        /// SAME shell the App sets under <c>ISingleViewApplicationLifetime</c>.
        /// </summary>
        public static string? RenderMainViewPath { get; set; }

        /// <summary>
        /// #1467 screenshot proof: when set (via <c>--screenshot-window=&lt;ViewName&gt;</c>
        /// plus <c>--screenshot-out=&lt;path&gt;</c>), the desktop app constructs that
        /// single editor Window, lets its <c>Opened</c> handler load (e.g. the Log
        /// Viewer reads the application log — no ROM required), renders it to a PNG
        /// with the real desktop Skia platform, then exits. Lets a no-ROM / locked
        /// environment capture a real PNG of a ROM-independent view.
        /// </summary>
        public static string? ScreenshotWindowName { get; set; }
        /// <summary>Output PNG path for <see cref="ScreenshotWindowName"/>.</summary>
        public static string? ScreenshotWindowOut { get; set; }

        /// <summary>
        /// Overrides the base directory used to resolve <c>config/</c> (#1123, Android).
        /// When non-null, <see cref="OnFrameworkInitializationCompleted"/> sets
        /// <see cref="CoreState.BaseDirectory"/> to this instead of
        /// <c>AppDomain.CurrentDomain.BaseDirectory</c>. The Android head sets it
        /// (in <c>MainActivity.OnCreate</c>, before the Avalonia app boots) to the
        /// FilesDir root where the bundled <c>config/</c> assets were extracted.
        /// Desktop NEVER sets it, so desktop base-directory resolution is unchanged.
        /// </summary>
        public static string? BaseDirectoryOverride { get; set; }

        /// <summary>Decomp project directory passed via --project command line argument (#1129).</summary>
        public static string? StartupProjectDir { get; set; }

        /// <summary>When true, run editor smoke test and exit.</summary>
        public static bool SmokeTestMode { get; set; }

        /// <summary>When true, run smoke test against ALL editors (not just Unit+Item).</summary>
        public static bool SmokeTestAll { get; set; }

        /// <summary>When true, force detailed editor mode (skip easy mode).</summary>
        public static bool ForceDetailMode { get; set; }

        /// <summary>When true, run data verification mode: open editors, load first item, verify data against raw ROM.</summary>
        public static bool DataVerifyMode { get; set; }

        /// <summary>When true, run full data verification: iterate ALL list items per editor, not just the first.</summary>
        public static bool DataVerifyFullMode { get; set; }

        /// <summary>When true, capture screenshots of all editors and save as PNG.</summary>
        public static bool ScreenshotAllMode { get; set; }

        /// <summary>When true, export decoded editor images (not full screenshots) as PNG.</summary>
        public static bool ExportEditorImagesMode { get; set; }

        /// <summary>When true, run export→import→export roundtrip validation for graphics editors.</summary>
        public static bool ValidateImportMode { get; set; }

        /// <summary>When true, run palette export→import→export roundtrip validation.</summary>
        public static bool ValidatePaletteMode { get; set; }

        /// <summary>When true, run list parity comparison between Avalonia and WinForms editors.</summary>
        public static bool ListParityMode { get; set; }

        /// <summary>Directory to save screenshots. Defaults to ./screenshots beside the exe.</summary>
        public static string? ScreenshotDir { get; set; }

        /// <summary>
        /// Optional AutomationId of a <c>TabItem</c> the <c>--screenshot-all</c>
        /// runner should select on each captured editor before rendering (so a
        /// non-default tab is shown in the PNG). Null = capture the default tab.
        /// Editors that don't host a matching tab are captured unchanged.
        /// </summary>
        public static string? ScreenshotTabAutomationId { get; set; }

        /// <summary>
        /// Optional AutomationId of a <see cref="global::Avalonia.Controls.Control"/>
        /// (typically an <c>IsVisible</c>-toggled panel) the <c>--screenshot-all</c>
        /// runner should force visible on each captured editor before rendering, so
        /// a category panel normally hidden behind a selection state shows up in the
        /// PNG. Null = no panel forced. Editors without a matching control are
        /// captured unchanged. Opt-in via <c>--screenshot-show-panel=&lt;AutomationId&gt;</c>.
        /// </summary>
        public static string? ScreenshotShowPanelAutomationId { get; set; }

        /// <summary>
        /// Optional AutomationId of a <see cref="global::Avalonia.Controls.Button"/>
        /// the <c>--screenshot-all</c> runner should INVOKE (raise Click) on each
        /// captured editor before rendering. Unlike <c>--screenshot-tab=</c> (which
        /// sets <c>TabControl.SelectedItem</c> directly and is therefore reverted by
        /// gate-aware wizards like <c>ToolInitWizardView</c>), invoking the button
        /// drives the editor's real navigation handler, so a gated wizard page (e.g.
        /// the Init Wizard's Step1 download buttons) is reached the same way a user
        /// reaches it. Null = no button invoked. Editors without a matching button
        /// are captured unchanged. Opt-in via <c>--screenshot-invoke-button=&lt;AutomationId&gt;</c>.
        /// </summary>
        public static string? ScreenshotInvokeButtonAutomationId { get; set; }

        /// <summary>
        /// Optional <c>&lt;ComboBoxAutomationId&gt;=&lt;index&gt;</c> spec the
        /// <c>--screenshot-all</c> runner applies to each captured editor before the
        /// first-item selection, setting that combo's <c>SelectedIndex</c> so a
        /// combo-driven editor is captured in a non-default state (e.g. the FE6
        /// Battle Dialogue editor's secondary boss-conversation table:
        /// <c>EventBattleTalkFE6_Table_Combo=1</c>). Editors without a matching combo
        /// are captured unchanged. Opt-in via
        /// <c>--screenshot-select-combo=&lt;AutomationId&gt;=&lt;index&gt;</c>.
        /// </summary>
        public static string? ScreenshotSelectComboSpec { get; set; }

        /// <summary>Gap-sweep mode (Phase 1 density, Phase 2 labels, …) or null if no gap-sweep flag was passed.</summary>
        public static string? GapSweepMode { get; set; }

        /// <summary>Output path for gap-sweep reports. Required when GapSweepMode != null.</summary>
        public static string? GapSweepOut { get; set; }

        /// <summary>When true, gap-sweep writes only the YAML front-matter header (no body).</summary>
        public static bool GapSweepDryRun { get; set; }

        /// <summary>
        /// Repo root override for gap-sweep scanning. Defaults to walking up from the
        /// executable directory until a `FEBuilderGBA.sln` is found, or the current
        /// directory if no solution file is found.
        /// </summary>
        public static string? GapSweepRepoRoot { get; set; }

        /// <summary>Phase 3: directory of WinForms `--screenshot-all` PNG output.</summary>
        public static string? GapSweepWfDir { get; set; }

        /// <summary>Phase 3: directory of Avalonia `--screenshot-all` PNG output.</summary>
        public static string? GapSweepAvDir { get; set; }

        /// <summary>Phase 3: ROM tag used as filename suffix (e.g. "FE8U") on both runners.</summary>
        public static string? GapSweepRomTag { get; set; }

        /// <summary>
        /// Phase 6: comma-separated list of target-language codes to join the AXAML
        /// literal inventory against (e.g. "ja,zh,ko"). Defaults to
        /// L10nScanner.DefaultLanguages when not supplied (cref kept as plain text
        /// because L10nScanner lives in the desktop-only GapSweep partial, #1121).
        /// English is the source for AXAML literals so it never appears here.
        /// </summary>
        public static string? GapSweepLanguages { get; set; }

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        internal static string PatchDatabaseRecoveryNotice { get; private set; } = "";
        internal static void ClearPatchDatabaseRecoveryNotice() => PatchDatabaseRecoveryNotice = "";

        public override void OnFrameworkInitializationCompleted()
        {
            // Register code pages for Shift-JIS, etc.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            // Set up Core state. On Android the head extracts config/ assets to
            // FilesDir and sets BaseDirectoryOverride before the app boots (#1123);
            // on desktop the override is always null, so this resolves to the
            // exe-adjacent loose config layout exactly as before.
            string baseDir = BaseDirectoryOverride ?? AppDomain.CurrentDomain.BaseDirectory;
            CoreState.BaseDirectory = baseDir;
            CoreState.Services = new AvaloniaAppServices();
            CoreState.ImageService = new SkiaImageService();

            var recovery = PatchDatabaseImportCore.RecoverPending(baseDir);
            PatchDatabaseRecoveryNotice = recovery.Success ? "" :
                R._("Patch database recovery needs attention: {0}", recovery.Message);
            if (!string.IsNullOrEmpty(PatchDatabaseRecoveryNotice))
                Log.Error(PatchDatabaseRecoveryNotice);

            // Wire headless caches
            CoreState.CommentCache = new HeadlessEtcCache();
            CoreState.LintCache = new HeadlessEtcCache();
            CoreState.WorkSupportCache = new HeadlessEtcCache();
            CoreState.SystemTextEncoder = new HeadlessSystemTextEncoder();
            // #428: AsmMap cache stub — the WF AsmMapFileAsmCache lives in
            // FEBuilderGBA.WinForms and depends on the full patch/symbol
            // pipeline. Avalonia/CLI use this no-op stub so `IsHardCodeUnit`
            // call sites null-check cleanly and the [HardCoding] hyperlink
            // simply stays hidden on the unit editors.
            CoreState.AsmMapFileAsmCache = new HeadlessAsmMapCache();
            // ResourceCache is string-keyed (different shape from the
            // IEtcCache uint-keyed caches above) — back it with the
            // EtcCacheResource WF type directly. Used by ImageBattleBG +
            // image-import views to record source-file paths.
            CoreState.ResourceCache ??= new FEBuilderGBA.EtcCacheResource();

            // #796: wire the headless free-space allocator that RecycleAddress
            // falls back to when no recycled region fits. WinForms wires this to
            // InputFormRef.AppendBinaryData (Program.cs); the Avalonia runtime
            // had it UNSET, so any RecycleAddress.Write with an empty recycle
            // list silently returned U.NOT_FOUND — e.g. the font auto-generator
            // (ImportFonts -> FontCore.MakeNewFontData -> RecycleAddress.Write)
            // would fail to append a freshly rasterized glyph. Shared with the
            // CLI + tests via the Core helper.
            CoreState.WireHeadlessAppendBinaryData();

            // Load config
            // #1124: baseDir is the exe dir on desktop and Context.FilesDir (app-private) on Android (#1123), so config.xml is already redirected to app-private storage on Android.
            // #1799: always create the Config (even when config.xml doesn't exist yet on a
            // fresh install) so CoreState.Config is never null and the Options dialog can
            // persist settings. Previously the File.Exists guard left it null on first run,
            // silently discarding tool paths, theme, recent files, etc.
            string configPath = Path.Combine(baseDir, "config", "config.xml");
            CoreState.Config = Config.LoadOrCreate(configPath);

            // Initialize language from config (backward-compatible: try "Language", then "func_lang")
            if (CoreState.Config != null)
            {
                string lang = CoreState.Config.at("Language", CoreState.Config.at("func_lang", "auto"));
                CoreState.Language = lang;
            }
            ViewModels.OptionsViewModel.ReloadTranslations();

            // Apply saved theme preference
            ApplySavedTheme();

            // Parse command line arguments
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                ParseArgs(desktop.Args);

                // Gap-sweep flags are pure static-analysis (no ROM, no UI required).
                // Execute them BEFORE creating MainWindow and exit immediately so
                // they're fast (~1-2 seconds total) and don't pop a window in CI.
                // GapSweep (RunGapSweep + scanners) is Roslyn dev-tooling excluded
                // from the head TFMs (android #1121, iOS #1859, browser #1864), so the
                // dispatch is guarded — on a head the desktop lifetime cast above is false
                // anyway, so this branch never executes there at runtime.
#if !ANDROID && !IOS && !BROWSER
                if (GapSweepMode != null)
                {
                    int code = RunGapSweep();
                    Environment.Exit(code);
                    return;
                }
#endif

                // #1122 build proof: render the single-view shell to a PNG using
                // the real desktop Skia platform, then exit. Never runs on Android
                // (this is the desktop lifetime branch).
                if (!string.IsNullOrEmpty(RenderMainViewPath))
                {
                    int code = RenderMainViewToPng(RenderMainViewPath!);
                    Environment.Exit(code);
                    return;
                }

                // #1467: render a single ROM-independent editor Window to a PNG.
                if (!string.IsNullOrEmpty(ScreenshotWindowName) && !string.IsNullOrEmpty(ScreenshotWindowOut))
                {
                    int code = RenderEditorWindowToPng(ScreenshotWindowName!, ScreenshotWindowOut!);
                    Environment.Exit(code);
                    return;
                }

                desktop.MainWindow = new Views.MainWindow();
            }
            // #1122: Android single-activity / single-view lifetime. The desktop
            // branch above is UNCHANGED; this additional branch sets a MainView
            // (the single-view shell that hosts the page/view-stack navigation
            // host) so the booted Android app presents UI. On desktop the
            // lifetime is always IClassicDesktopStyleApplicationLifetime, so this
            // branch never runs there.
            else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            {
                singleView.MainView = new Views.MainView();
            }

            base.OnFrameworkInitializationCompleted();
        }

        /// <summary>
        /// #1122 build proof: render the Android single-view shell
        /// (<see cref="Views.MainView"/>) — launcher root + a navigated page so
        /// the back bar lights — to <paramref name="path"/> using the real
        /// desktop Skia render target. Returns 0 on success, non-zero on error.
        /// Desktop-only (called from the classic-desktop branch); never on Android.
        /// </summary>
        static int RenderMainViewToPng(string path)
        {
            try
            {
                // #1891: if a ROM was supplied (--rom=<path>), load it so the launcher renders the
                // FULL editor catalog instead of the empty "no ROM loaded" hint. Best-effort — a
                // failure still renders the shell chrome.
                TryLoadRomForMainViewRender();

                // Install the single-view nav service so MainView wires its host.
                Services.WindowManager.Instance.SetService(new Services.AndroidNavigationService());

                var view = new Views.MainView
                {
                    Background = global::Avalonia.Media.Brushes.White,
                };

                // Host the UserControl in a Window so the FluentTheme styles are
                // applied and a full layout pass runs (a detached control renders
                // blank). The window is shown off the visible area, rendered, and
                // closed — never presented to a user.
                var host = new global::Avalonia.Controls.Window
                {
                    Width = 360,
                    Height = 640,
                    SystemDecorations = global::Avalonia.Controls.SystemDecorations.None,
                    ShowInTaskbar = false,
                    Position = new global::Avalonia.PixelPoint(-4000, -4000),
                    Content = view,
                };
                host.Show();
                host.UpdateLayout();

                string fullPath = Path.GetFullPath(path);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Frame 1: the launcher root page (the editor menu / Home).
                string launcherPath = Path.Combine(
                    dir ?? ".",
                    Path.GetFileNameWithoutExtension(fullPath) + "-home" + Path.GetExtension(fullPath));
                RenderViewToFile(view, launcherPath);

                // Frame 2: navigate to one editor so the page host + back bar are
                // populated (empty without a ROM — fine for a shell chrome render).
                try { Services.WindowManager.Instance.Open<Views.UnitEditorView>(); }
                catch (Exception ex) { Log.Error("RenderMainView open editor: ", ex.ToString()); }
                host.UpdateLayout();
                RenderViewToFile(view, fullPath);

                host.Close();
                Log.Notify("Rendered single-view shell to ", path);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("RenderMainViewToPng failed: ", ex.ToString());
                return 1;
            }
        }

        /// <summary>
        /// #1891: best-effort load the <c>--rom=</c> ROM before rendering the single-view shell, so
        /// the launcher shows the full editor catalog (version-gated to the ROM) rather than the
        /// empty "no ROM loaded" hint. Mirrors the shared post-load init the app/tests use. Any
        /// failure is logged and swallowed — the render then shows the empty shell chrome.
        /// </summary>
        static void TryLoadRomForMainViewRender()
        {
            if (string.IsNullOrEmpty(StartupRomPath) || !File.Exists(StartupRomPath))
                return;
            try
            {
                if (string.IsNullOrEmpty(CoreState.BaseDirectory))
                    CoreState.BaseDirectory = AppContext.BaseDirectory;
                var rom = new ROM();
                using var stream = File.OpenRead(StartupRomPath);
                var (ok, _) = rom.LoadFromStreamAsync(stream, StartupRomPath).GetAwaiter().GetResult();
                if (ok)
                {
                    // Set CoreState.ROM first so the launcher renders the catalog even if the
                    // cache-populating post-load init degrades without a full config/ tree.
                    CoreState.ROM = rom;
                    try { Services.RomFileService.InitializeLoadedRom(rom); }
                    catch (Exception initEx) { Log.Error("RenderMainView post-load init (non-fatal): ", initEx.ToString()); }
                }
            }
            catch (Exception ex)
            {
                Log.Error("RenderMainView ROM load failed (rendering empty shell): ", ex.ToString());
            }
        }

        static void RenderViewToFile(global::Avalonia.Controls.Control view, string outPath)
        {
            view.Measure(new global::Avalonia.Size(360, 640));
            view.Arrange(new global::Avalonia.Rect(0, 0, 360, 640));
            using var rtb = new global::Avalonia.Media.Imaging.RenderTargetBitmap(
                new global::Avalonia.PixelSize(360, 640), new global::Avalonia.Vector(96, 96));
            rtb.Render(view);
            rtb.Save(outPath);
        }

        /// <summary>
        /// #1467: construct a named editor Window, show it off-screen so its
        /// <c>Opened</c> handler runs (the Log Viewer loads the application log;
        /// no ROM required) and the FluentTheme + a full layout pass apply, then
        /// render it to a PNG with the real desktop Skia rasterizer and exit.
        /// </summary>
        static int RenderEditorWindowToPng(string viewName, string outPath)
        {
            try
            {
                global::Avalonia.Controls.Window window = viewName switch
                {
                    "LogViewerView" => new Services.EditorHostWindow(new Views.LogViewerView()),
                    // #1700 PR proof: render the two cosmetic-fix editors with the
                    // real desktop Skia rasterizer (the empty editor already lays
                    // out the geometry under test — AIScript's MinWidth address
                    // link and the palette index columns + swatches; no ROM needed).
                    "AIScriptView" => new Services.EditorHostWindow(new Views.AIScriptView()),
                    "ImagePalletView" => new Services.EditorHostWindow(new Views.ImagePalletView()),
                    // #1793 PR proof: the "unknown ROM" chooser is ROM-independent
                    // (its VM.Initialize() is self-contained) and has 7 tall (66px)
                    // action buttons that were NOT touched by #1727 — so it renders
                    // real before/after PNG proof of the global Button vertical-
                    // centering style.
                    "ErrorUnknownROMView" => new EditorHostWindow(new Views.ErrorUnknownROMView()),
                    // #1681: the alignment-fix clone dialogs are ROM-independent
                    // (each constructs its own ViewModel + Load(0)/Initialize()),
                    // so they render real PNG proof of the centered Apply button.
                    "EventUnitColorView" => new Services.EditorHostWindow(new Views.EventUnitColorView()),
                    "PackedMemorySlotView" => new Services.EditorHostWindow(new Views.PackedMemorySlotView()),
                    "UbyteBitFlagView" => new Services.EditorHostWindow(new Views.UbyteBitFlagView()),
                    "UshortBitFlagView" => new Services.EditorHostWindow(new Views.UshortBitFlagView()),
                    "UwordBitFlagView" => new Services.EditorHostWindow(new Views.UwordBitFlagView()),
                    // #1714/#1716 PR proof: the empty editors already lay out the
                    // geometry under test — the fixed-size window (SizeToContent="Manual"
                    // + MinWidth/MinHeight floor) and the CatalogCombo toolbar with the
                    // fixed-width dropdown style. No ROM needed.
                    "EventScriptView" => new EditorHostWindow(new Views.EventScriptView()),
                    "ProcsScriptView" => new EditorHostWindow(new Views.ProcsScriptView()),
                    // #1772 PR proof: the map-resize dialog family is ROM-independent
                    // (each VM's Initialize() just sets IsLoaded=true), so they render
                    // real PNG proof of the centered Resize / Apply action buttons.
                    "MapEditorResizeDialogView" => new Services.EditorHostWindow(new Views.MapEditorResizeDialogView()),
                    "MapEditorMarSizeDialogView" => new Services.EditorHostWindow(new Views.MapEditorMarSizeDialogView()),
                    // #1781 PR proof: the Options window is ROM-independent (its Opened
                    // handler loads config, not a ROM), so it renders real PNG proof that
                    // the window is a screen-safe fixed height with OK/Cancel visible.
                    "OptionsView" => new EditorHostWindow(new Views.OptionsView()),
                    // #1784 PR proof: MapSettingDifficultyDialogView is ROM-independent, so
                    // it renders real PNG proof of the centered "Apply" action button.
                    "MapSettingDifficultyDialogView" => new Services.EditorHostWindow(new Views.MapSettingDifficultyDialogView()),
                    // #1817 PR proof: the Patch Manager is ROM-independent for the empty-state
                    // screenshot (LoadPatches catches a missing ROM/patch2), so it renders real
                    // PNG proof of the new "Initialize / Update Patch Database" button and the
                    // empty-state notice in the left panel.
                    "PatchManagerView" => new EditorHostWindow(new Views.PatchManagerView()),
                    // #1913 PR proof: render the Event Unit Placement editor at a
                    // FIXED narrow width (override the view's SizeToContent=WidthAndHeight,
                    // which would otherwise auto-grow to fit content) so the always-present
                    // Unit-Info "Growth Rate" row overflows the detail column, exercising the
                    // new HorizontalScrollBarVisibility="Auto" on the detail ScrollViewer.
                    "EventUnitView" => BuildEventUnitDetailScrollHost(),
                    _ => throw new ArgumentException($"Unsupported --screenshot-window view: {viewName}"),
                };

                window.SystemDecorations = global::Avalonia.Controls.SystemDecorations.None;
                window.ShowInTaskbar = false;
                window.Position = new global::Avalonia.PixelPoint(-4000, -4000);
                window.Show(); // raises Opened → the view loads the log
                window.UpdateLayout();

                string fullPath = Path.GetFullPath(outPath);
                string? dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // #1681: SizeToContent="WidthAndHeight" windows can report a
                // non-finite (NaN or Infinity) Width/Height here; Math.Max(NaN, n)
                // is NaN, which would yield an invalid PixelSize. Fall back to the
                // window's arranged Bounds (then the declared min) whenever the
                // resolved size is not finite or non-positive. double.IsFinite
                // (net9) rejects NaN AND ±Infinity in one check.
                double rawW = window.Width;
                double rawH = window.Height;
                if (!double.IsFinite(rawW) || rawW <= 0) rawW = window.Bounds.Width;
                if (!double.IsFinite(rawH) || rawH <= 0) rawH = window.Bounds.Height;
                int w = (int)Math.Max(double.IsFinite(rawW) ? rawW : 0, 400);
                int h = (int)Math.Max(double.IsFinite(rawH) ? rawH : 0, 300);
                window.Measure(new global::Avalonia.Size(w, h));
                window.Arrange(new global::Avalonia.Rect(0, 0, w, h));
                using var rtb = new global::Avalonia.Media.Imaging.RenderTargetBitmap(
                    new global::Avalonia.PixelSize(w, h), new global::Avalonia.Vector(96, 96));
                rtb.Render(window);
                rtb.Save(fullPath);

                window.Close();
                Log.Notify("Rendered editor window to ", outPath);
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("RenderEditorWindowToPng failed: ", ex.ToString());
                return 1;
            }
        }

        /// <summary>
        /// #1913 PR proof host: the Event Unit Placement editor's detail panel scrolls
        /// horizontally only when its content is wider than the detail column. The
        /// view declares SizeToContent=WidthAndHeight, so a normal host auto-grows to
        /// fit content (no overflow, no scrollbar). Force SizeToContent=Manual at a
        /// fixed narrow width so the always-present Unit-Info "Growth Rate" row
        /// overflows and the new HorizontalScrollBarVisibility="Auto" engages.
        /// </summary>
        static global::Avalonia.Controls.Window BuildEventUnitDetailScrollHost()
        {
            var view = new Views.EventUnitView();
            return new global::Avalonia.Controls.Window
            {
                Title = "Event Unit Placement",
                SizeToContent = global::Avalonia.Controls.SizeToContent.Manual,
                Width = 1150,
                Height = 980,
                MinWidth = 1150,
                Content = view,
            };
        }

        /// <summary>Toggle between light and dark mode, updating dynamic resources and persisting the choice.</summary>
        public void ToggleTheme()
        {
            bool switchToDark = !IsDarkMode;
            RequestedThemeVariant = switchToDark ? ThemeVariant.Dark : ThemeVariant.Light;
            ApplyThemeResources(switchToDark);

            // Persist
            if (CoreState.Config != null)
            {
                CoreState.Config[ThemeConfigKey] = switchToDark ? "Dark" : "Light";
                CoreState.Config.Save();
            }
        }

        void ApplySavedTheme()
        {
            bool dark = false;
            if (CoreState.Config != null)
            {
                string pref = CoreState.Config.at(ThemeConfigKey, "Light");
                dark = string.Equals(pref, "Dark", StringComparison.OrdinalIgnoreCase);
            }
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            ApplyThemeResources(dark);
        }

        void ApplyThemeResources(bool dark)
        {
            if (Resources == null) return;

            if (dark)
            {
                SetBrush("StatusBarBackgroundBrush", "#2D2D2D");
                SetBrush("ToolbarBackgroundBrush", "#333333");
                SetBrush("ToolbarBorderBrush", "#444444");
                SetBrush("SectionHeadingBrush", "#6B9BD2");
                SetBrush("CardBorderBrush", "#555555");
                SetBrush("InfoBannerBackgroundBrush", "#1A3A4A");
                SetBrush("InfoBannerBorderBrush", "#2A8A9A");
                SetBrush("InfoBannerTextBrush", "#8ECFDB");
                SetBrush("SubtlePanelBackgroundBrush", "#2A2A2A");
                SetBrush("SubtlePanelBorderBrush", "#444444");
                SetBrush("WarningBackgroundBrush", "#3D3520");
                SetBrush("WarningBorderBrush", "#AA8800");
                SetBrush("WarningTextBrush", "#D4A830");
                SetBrush("WarningTextSecondaryBrush", "#C4A040");
                SetBrush("DependencyWarningBackgroundBrush", "#3D3820");
                SetBrush("DependencyWarningBorderBrush", "#AA8800");
                SetBrush("AccentOverlayBrush", "#33FF8C00");
                SetBrush("ScriptCategoryButtonBrush", "#5070B0");
                SetBrush("ScriptCategoryOverlayBrush", "#30808080");
                SetBrush("ErrorDialogBackgroundBrush", "#3A2020");
                SetBrush("UndoInfoBackgroundBrush", "#1A2A3A");
                SetBrush("UndoInfoBorderBrush", "#406090");
                SetBrush("EmulatorWarningBackgroundBrush", "#3A3520");
                SetBrush("CharCodeHeaderBrush", "#4A6080");
                SetBrush("CharCodeCellBrush", "#3A3A3A");
                SetBrush("WelcomeBannerBrush", "#2A2A3A");
                SetBrush("WelcomeBannerBorderBrush", "#3A3A5A");
                SetBrush("DecompBadgeBrush", "#FF9A3C");
            }
            else
            {
                SetBrush("StatusBarBackgroundBrush", "#E8E8E8");
                SetBrush("ToolbarBackgroundBrush", "#F0F0F0");
                SetBrush("ToolbarBorderBrush", "#DDDDDD");
                SetBrush("SectionHeadingBrush", "#2B579A");
                SetBrush("CardBorderBrush", "#CCCCCC");
                SetBrush("InfoBannerBackgroundBrush", "#D1ECF1");
                SetBrush("InfoBannerBorderBrush", "#17A2B8");
                SetBrush("InfoBannerTextBrush", "#0C5460");
                SetBrush("SubtlePanelBackgroundBrush", "#F8F9FA");
                SetBrush("SubtlePanelBorderBrush", "#DEE2E6");
                SetBrush("WarningBackgroundBrush", "#FFF3CD");
                SetBrush("WarningBorderBrush", "#FFC107");
                SetBrush("WarningTextBrush", "#B8860B");
                SetBrush("WarningTextSecondaryBrush", "#8B6914");
                SetBrush("DependencyWarningBackgroundBrush", "#FFFDE8");
                SetBrush("DependencyWarningBorderBrush", "#F0C000");
                SetBrush("AccentOverlayBrush", "#33FF8C00");
                SetBrush("ScriptCategoryButtonBrush", "#4060A0");
                SetBrush("ScriptCategoryOverlayBrush", "#20808080");
                SetBrush("ErrorDialogBackgroundBrush", "#FFF8F8");
                SetBrush("UndoInfoBackgroundBrush", "#E3F2FD");
                SetBrush("UndoInfoBorderBrush", "#90CAF9");
                SetBrush("EmulatorWarningBackgroundBrush", "#FFF8E1");
                SetBrush("CharCodeHeaderBrush", "#FF6888A8");
                SetBrush("CharCodeCellBrush", "#FFE0E0E0");
                SetBrush("WelcomeBannerBrush", "#E8EAF6");
                SetBrush("WelcomeBannerBorderBrush", "#C5CAE9");
                SetBrush("DecompBadgeBrush", "#B85C00");
            }
        }

        void SetBrush(string key, string color)
        {
            if (Resources.ContainsKey(key) && Resources[key] is SolidColorBrush brush)
            {
                brush.Color = Color.Parse(color);
            }
        }

        static void ParseArgs(string[]? args)
        {
            if (args == null) return;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--rom" && i + 1 < args.Length)
                {
                    StartupRomPath = args[++i];
                }
                else if (args[i] == "--smoke-test")
                {
                    SmokeTestMode = true;
                }
                else if (args[i] == "--smoke-test-all")
                {
                    SmokeTestMode = true;
                    SmokeTestAll = true;
                }
                else if (args[i] == "--force-detail")
                {
                    ForceDetailMode = true;
                }
                else if (args[i] == "--data-verify")
                {
                    SmokeTestMode = true;
                    DataVerifyMode = true;
                }
                else if (args[i] == "--data-verify-full")
                {
                    SmokeTestMode = true;
                    DataVerifyMode = true;
                    DataVerifyFullMode = true;
                }
                else if (args[i] == "--screenshot-all")
                {
                    SmokeTestMode = true;
                    ScreenshotAllMode = true;
                }
                else if (args[i] == "--export-editor-images")
                {
                    SmokeTestMode = true;
                    ExportEditorImagesMode = true;
                }
                else if (args[i] == "--validate-import")
                {
                    SmokeTestMode = true;
                    ValidateImportMode = true;
                }
                else if (args[i] == "--validate-palette")
                {
                    SmokeTestMode = true;
                    ValidatePaletteMode = true;
                }
                else if (args[i] == "--list-parity")
                {
                    SmokeTestMode = true;
                    ListParityMode = true;
                }
                else if (args[i] == "--gap-sweep-density")
                {
                    SmokeTestMode = true;
                    GapSweepMode = "density";
                }
                else if (args[i] == "--gap-sweep-labels")
                {
                    SmokeTestMode = true;
                    GapSweepMode = "labels";
                }
                else if (args[i] == "--gap-sweep-jumps")
                {
                    SmokeTestMode = true;
                    GapSweepMode = "jumps";
                }
                else if (args[i] == "--gap-sweep-undo")
                {
                    SmokeTestMode = true;
                    GapSweepMode = "undo";
                }
                else if (args[i] == "--gap-sweep-l10n")
                {
                    SmokeTestMode = true;
                    GapSweepMode = "l10n";
                }
                else if (args[i] == "--gap-sweep-all")
                {
                    SmokeTestMode = true;
                    GapSweepMode = "all";
                }
                else if (args[i] == "--gap-sweep-gallery")
                {
                    SmokeTestMode = true;
                    GapSweepMode = "gallery";
                }
                else if (args[i] == "--dry-run")
                {
                    GapSweepDryRun = true;
                }
                else if (args[i].StartsWith("--out="))
                {
                    GapSweepOut = args[i].Substring("--out=".Length);
                }
                else if (args[i].StartsWith("--repo-root="))
                {
                    GapSweepRepoRoot = args[i].Substring("--repo-root=".Length);
                }
                else if (args[i].StartsWith("--wf-dir="))
                {
                    GapSweepWfDir = args[i].Substring("--wf-dir=".Length);
                }
                else if (args[i].StartsWith("--av-dir="))
                {
                    GapSweepAvDir = args[i].Substring("--av-dir=".Length);
                }
                else if (args[i].StartsWith("--rom-tag="))
                {
                    GapSweepRomTag = args[i].Substring("--rom-tag=".Length);
                }
                else if (args[i].StartsWith("--languages="))
                {
                    GapSweepLanguages = args[i].Substring("--languages=".Length);
                }
                else if (args[i].StartsWith("--screenshot-dir="))
                {
                    ScreenshotDir = args[i].Substring("--screenshot-dir=".Length);
                }
                else if (args[i].StartsWith("--screenshot-tab="))
                {
                    ScreenshotTabAutomationId = args[i].Substring("--screenshot-tab=".Length);
                }
                else if (args[i].StartsWith("--screenshot-show-panel="))
                {
                    ScreenshotShowPanelAutomationId = args[i].Substring("--screenshot-show-panel=".Length);
                }
                else if (args[i].StartsWith("--screenshot-invoke-button="))
                {
                    ScreenshotInvokeButtonAutomationId = args[i].Substring("--screenshot-invoke-button=".Length);
                }
                else if (args[i].StartsWith("--screenshot-select-combo="))
                {
                    ScreenshotSelectComboSpec = args[i].Substring("--screenshot-select-combo=".Length);
                }
                else if (args[i].StartsWith("--screenshot-window="))
                {
                    ScreenshotWindowName = args[i].Substring("--screenshot-window=".Length);
                }
                else if (args[i].StartsWith("--screenshot-out="))
                {
                    ScreenshotWindowOut = args[i].Substring("--screenshot-out=".Length);
                }
                else if (args[i] == "--lastrom")
                {
                    // Load last ROM from config
                    if (CoreState.Config != null)
                    {
                        string lastRom = CoreState.Config.at("Last_Rom_Filename", "");
                        if (!string.IsNullOrEmpty(lastRom) && File.Exists(lastRom))
                        {
                            StartupRomPath = lastRom;
                        }
                    }
                }
                else if (args[i].StartsWith("--rom="))
                {
                    StartupRomPath = args[i].Substring("--rom=".Length);
                }
                else if (args[i].StartsWith("--render-mainview="))
                {
                    RenderMainViewPath = args[i].Substring("--render-mainview=".Length);
                }
                else if (args[i].StartsWith("--project="))
                {
                    StartupProjectDir = args[i].Substring("--project=".Length);
                }
                else if (args[i] == "--project" && i + 1 < args.Length)
                {
                    StartupProjectDir = args[++i];
                }
            }
        }
    }
}
