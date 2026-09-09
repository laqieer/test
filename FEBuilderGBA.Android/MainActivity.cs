using System;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using FEBuilderGBA;

namespace FEBuilderGBA.Android
{
    /// <summary>
    /// Android entry-point activity for the FEBuilderGBA Avalonia GUI
    /// (exploration skeleton — epic #1070).
    ///
    /// <para>
    /// This is the Android equivalent of the desktop
    /// <c>FEBuilderGBA.Avalonia/Program.cs</c> <c>Main</c> +
    /// <c>StartWithClassicDesktopLifetime</c>. On Android there is no classic
    /// desktop lifetime: <see cref="AvaloniaMainActivity{TApp}"/> hosts the
    /// shared <see cref="global::FEBuilderGBA.Avalonia.App"/> under a
    /// <c>ISingleViewApplicationLifetime</c> (one Activity, one root view).
    /// </para>
    ///
    /// <para>
    /// SINGLE-VIEW UI (#1122): the shared <c>App.OnFrameworkInitializationCompleted</c>
    /// now presents the editor UI under the single-view Android lifetime — its
    /// <c>else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)</c>
    /// branch sets <c>singleView.MainView = new Views.MainView()</c> (the desktop
    /// <c>IClassicDesktopStyleApplicationLifetime</c> branch is unchanged). The
    /// <c>WindowManager</c> multi-window model is reworked behind an
    /// <c>INavigationService</c> abstraction: desktop keeps multi-window
    /// behavior, Android uses a single-view page/view-stack nav host (back stack,
    /// modal-as-page, pick-await). Build-only validated — the on-device runtime
    /// UX (touch + per-editor attached-Window dialogs) is tracked under #1873.
    /// SAF ROM I/O landed in #1124. See docs/ANDROID.md §2.
    /// </para>
    ///
    /// <para>
    /// CONFIG BOOTSTRAP (#1123): <see cref="OnCreate"/> extracts the bundled
    /// <c>config/</c> <c>AndroidAsset</c> tree into <c>Context.FilesDir</c> on first
    /// run (version-stamped + idempotent, via the pure
    /// <see cref="AndroidConfigExtractorCore"/>) and points
    /// <c>App.BaseDirectoryOverride</c> at it BEFORE the Avalonia app boots, so
    /// Core's <c>PathUtil.ConfigPath</c> resolves <c>config/&lt;sub&gt;</c> on Android.
    /// <c>config/patch2</c> is NOT bundled (deferred — see docs/ANDROID.md §5).
    /// </para>
    /// </summary>
    // AvaloniaMainActivity uses AppCompatActivity internally, so Android must
    // apply an AppCompat-derived theme before activity initialization.
    //
    // Exported = true is REQUIRED when targeting Android 12 / API 31+ for any
    // activity with an intent-filter. This project targets API 36, and the
    // MainLauncher activity has an implicit LAUNCHER filter, so without an
    // explicit android:exported aapt2 packaging fails. true is correct here
    // because this is the user-facing launcher.
    [Activity(
        Label = "FEBuilderGBA",
        Theme = "@style/FEBuilderTheme",
        MainLauncher = true,
        Exported = true,
        ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
    public class MainActivity : AvaloniaMainActivity<global::FEBuilderGBA.Avalonia.App>
    {
        const string LogTag = "FEBuilderGBA";

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            // Extract the bundled config/ assets BEFORE the Avalonia app boots
            // (base.OnCreate). The shared App.OnFrameworkInitializationCompleted
            // reads CoreState.BaseDirectory immediately to locate config/config.xml,
            // so BaseDirectoryOverride must already be set when it runs.
            ExtractConfigAndWireBaseDirectory();
            base.OnCreate(savedInstanceState);
        }

        /// <summary>
        /// First-run, version-stamped extraction of the bundled <c>config/</c>
        /// AndroidAssets into <c>Context.FilesDir</c> (#1123). On success, points
        /// <c>App.BaseDirectoryOverride</c> at the extracted root. On failure we log
        /// and RETHROW (fail fast): a booted app with missing/wrong config is worse
        /// than a visible crash, and the override is left null so desktop-style
        /// resolution does not silently mask the problem.
        /// </summary>
        void ExtractConfigAndWireBaseDirectory()
        {
            try
            {
                string targetRoot = FilesDir!.CanonicalPath;
                string version = GetAppVersionString();

                var source = new AndroidAssetSource(Assets!);
                AndroidConfigExtractorCore.ExtractionResult result =
                    AndroidConfigExtractorCore.EnsureExtracted(source, targetRoot, version, preservePatchDatabase: true);

                global::Android.Util.Log.Info(LogTag,
                    $"config extraction: {result} (version={version}, target={targetRoot})");

                // Only wire BaseDirectory AFTER a successful extraction.
                global::FEBuilderGBA.Avalonia.App.BaseDirectoryOverride = targetRoot;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Error(LogTag, "config extraction FAILED: " + ex);
                throw; // fail fast — do not boot with missing config
            }
        }

        /// <summary>
        /// App version string used as the extraction stamp. A bump forces a clean
        /// re-extract. <c>LongVersionCode</c> is API 28+, but the head supports API
        /// 21, so the version code read is API-gated with a pre-28 fallback.
        /// </summary>
        string GetAppVersionString()
        {
            PackageInfo? pi = PackageManager?.GetPackageInfo(PackageName!, 0);
            if (pi == null)
            {
                return "unknown";
            }

            long code = OperatingSystem.IsAndroidVersionAtLeast(28)
                ? pi.LongVersionCode
#pragma warning disable CA1422 // VersionCode is the correct pre-API-28 accessor
                : pi.VersionCode;
#pragma warning restore CA1422
            return (pi.VersionName ?? "0") + "-" + code.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        {
            // NOTE: the shared App.axaml already declares a <FluentTheme/>, so
            // no extra font/theme wiring is needed here. (The Avalonia
            // template's .WithInterFont() needs the separate Avalonia.Fonts.Inter
            // package and is intentionally omitted from this skeleton.)
            return base.CustomizeAppBuilder(builder)
                .LogToTrace();
        }
    }
}
