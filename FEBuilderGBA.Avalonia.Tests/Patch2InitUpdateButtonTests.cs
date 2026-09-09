// SPDX-License-Identifier: GPL-3.0-or-later
// #1817 — Avalonia in-app patch2 Initialize/Update surfaces. Verifies:
//   * PatchManagerViewModel.Patch2ButtonText derives Initialize/Update from the REPO-ROOT
//     config/patch2 git status (via CoreState.BaseDirectory), not the per-version subfolder;
//   * the Init/Update buttons exist (by name) on the Patch Manager and Options views headlessly.
using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using FEBuilderGBA;
using FEBuilderGBA.Avalonia.ViewModels;
using FEBuilderGBA.Avalonia.Views;
using FEBuilderGBA.Avalonia.Dialogs;
using Xunit;

namespace FEBuilderGBA.Avalonia.Tests
{
    [Collection("SharedState")]
    public class Patch2InitUpdateButtonTests
    {
        [Fact]
        public void Patch2ButtonText_NonRepoDir_ShowsInitialize()
        {
            string saved = CoreState.BaseDirectory;
            string baseDir = Path.Combine(Path.GetTempPath(), "fe_p2btn_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(baseDir, "config", "patch2")); // exists, but not a git repo
            try
            {
                CoreState.BaseDirectory = baseDir;
                var vm = new PatchManagerViewModel();
                Assert.Equal("Initialize Patch Database", vm.Patch2ButtonText);
            }
            finally
            {
                CoreState.BaseDirectory = saved;
                try { Directory.Delete(baseDir, true); } catch { }
            }
        }

        [Fact]
        public void Patch2ButtonText_GitRepoDir_ShowsUpdate()
        {
            string saved = CoreState.BaseDirectory;
            string baseDir = Path.Combine(Path.GetTempPath(), "fe_p2btn_" + Guid.NewGuid().ToString("N"));
            string patch2 = Path.Combine(baseDir, "config", "patch2");
            Directory.CreateDirectory(Path.Combine(patch2, ".git")); // .git DIRECTORY → IsGitRepo true
            try
            {
                CoreState.BaseDirectory = baseDir;
                var vm = new PatchManagerViewModel();
                Assert.Equal("Update Patch Database", vm.Patch2ButtonText);
            }
            finally
            {
                CoreState.BaseDirectory = saved;
                try { Directory.Delete(baseDir, true); } catch { }
            }
        }
    }

    public class Patch2InitUpdateViewTests
    {
        [AvaloniaFact]
        public void OfflineImportConfirmationDefaultsToNoWithoutChangingOtherDialogs()
        {
            var dialog = new MessageBoxContent("Replacement consent", "Import", MessageBoxMode.YesNo);
            PatchManagerView.SetDefaultImportConfirmation(dialog);
            Assert.Equal(MessageBoxResult.No, dialog.Result);
            Assert.True(dialog.FindControl<Button>("NoButton")!.IsDefault);
            Assert.True(dialog.FindControl<Button>("NoButton")!.IsCancel);
            Assert.False(dialog.FindControl<Button>("YesButton")!.IsDefault);
            Assert.True(dialog.FindControl<Button>("NoButton")!.TabIndex < dialog.FindControl<Button>("YesButton")!.TabIndex);
        }

        [AvaloniaFact]
        public void PatchManagerView_HasSeparateOfflineImportAndCancellationControls()
        {
            var view = new PatchManagerView();
            var import = view.FindControl<Button>("ImportPatchDatabaseButton");
            var cancel = view.FindControl<Button>("CancelPatchDatabaseImportButton");
            Assert.NotNull(import);
            Assert.NotNull(cancel);
            Assert.False(import!.IsEnabled);
            Assert.False(cancel!.IsVisible);
            Assert.NotNull(view.FindControl<Button>("InitUpdatePatch2Button"));
        }

        [AvaloniaFact]
        public void PatchManagerView_HasInitUpdateButton()
        {
            var view = new PatchManagerView();
            var btn = view.FindControl<Button>("InitUpdatePatch2Button");
            Assert.NotNull(btn);
            Assert.False(string.IsNullOrWhiteSpace(btn!.Content?.ToString()));
        }

        [AvaloniaFact]
        public void OptionsView_HasInitUpdateButton()
        {
            var view = new OptionsView();
            var btn = view.FindControl<Button>("InitUpdatePatch2Button");
            Assert.NotNull(btn);
        }

        [AvaloniaFact]
        public void OptionsView_HasFERepoAndMusicInitUpdateButtons() // #1813
        {
            var view = new OptionsView();
            Assert.NotNull(view.FindControl<Button>("InitUpdateFERepoButton"));
            Assert.NotNull(view.FindControl<Button>("InitUpdateFERepoMusicButton"));
        }
    }
}
