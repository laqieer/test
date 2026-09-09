// SPDX-License-Identifier: GPL-3.0-or-later
// Contract tests for AndroidResourceNoticeCore (#1641): the canonical Android
// patch2 / FE-Repo "documented limitation" messages + the injectable platform seam.
using System;
using FEBuilderGBA;
using Xunit;

namespace FEBuilderGBA.Core.Tests
{
    public class AndroidResourceNoticeCoreTests
    {
        [Fact]
        public void PatchMessage_ExplainsOfflineImportAndRemainingGitLimitation()
        {
            string msg = AndroidResourceNoticeCore.PatchLibraryUnavailableMessage;
            Assert.False(string.IsNullOrWhiteSpace(msg));
            Assert.Contains("Import Patch Database ZIP", msg);
            Assert.Contains("Android", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Git-based initialization/update is unavailable", msg);
            Assert.Contains("not bundled in the APK", msg);
            Assert.DoesNotContain("not available on Android yet", msg);
        }

        [Fact]
        public void FERepoMessage_IsNonEmpty_AndMentionsFERepoAndPlanEpic()
        {
            string msg = AndroidResourceNoticeCore.FERepoUnavailableMessage;
            Assert.False(string.IsNullOrWhiteSpace(msg));
            Assert.Contains("FE-Repo", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Android", msg, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("#1070", msg);
        }

        [Fact]
        public void IsResourceDeliverySupported_TracksTheInjectableSeam()
        {
            Func<bool>? saved = AndroidResourceNoticeCore.IsAndroidOverride;
            try
            {
                AndroidResourceNoticeCore.IsAndroidOverride = () => true;  // pretend Android
                Assert.False(AndroidResourceNoticeCore.IsResourceDeliverySupported);

                AndroidResourceNoticeCore.IsAndroidOverride = () => false; // pretend desktop
                Assert.True(AndroidResourceNoticeCore.IsResourceDeliverySupported);
            }
            finally
            {
                AndroidResourceNoticeCore.IsAndroidOverride = saved!;
            }
        }

        [Fact]
        public void Default_OnDesktopTestRunner_IsSupported()
        {
            // The test runner is a desktop OS (Linux/macOS/Windows), never Android,
            // so the default seam must report resource delivery as supported.
            Assert.True(AndroidResourceNoticeCore.IsResourceDeliverySupported);
        }

        [Fact]
        public void IsResourceDeliverySupported_DoesNotThrow_WhenOverrideIsNull()
        {
            // A null override (accidental assignment / failed test) must not NRE — it
            // falls back to the real OS check (desktop test host => supported).
            Func<bool>? saved = AndroidResourceNoticeCore.IsAndroidOverride;
            try
            {
                AndroidResourceNoticeCore.IsAndroidOverride = null!;
                bool supported = AndroidResourceNoticeCore.IsResourceDeliverySupported; // must not throw
                Assert.True(supported); // desktop test runner
            }
            finally
            {
                AndroidResourceNoticeCore.IsAndroidOverride = saved!;
            }
        }
    }
}
