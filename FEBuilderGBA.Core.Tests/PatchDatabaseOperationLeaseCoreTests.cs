using System.Diagnostics;
using System.Reflection;

namespace FEBuilderGBA.Core.Tests;

[Collection("ContentRepoGitGuard")]
public class PatchDatabaseOperationLeaseCoreTests
{
    [Fact]
    public void GenericPatch2GitEntryPointHonorsTheBaseLease()
    {
        using var fixture = new Fixture();
        using var lease = PatchDatabaseOperationLeaseCore.Acquire(fixture.Root);
        bool invoked = false;
        var result = ContentRepoGitService.InitializeOrUpdateWithLease(
            Path.Combine(fixture.Root, "config", "patch2"), () =>
            {
                invoked = true;
                return new Patch2GitResult { Kind = Patch2GitResultKind.Success };
            });
        Assert.Equal(Patch2GitResultKind.AlreadyRunning, result.Kind);
        Assert.False(invoked);
    }

    [Fact]
    public void RelativeCanonicalPatch2GitPathsUseTheSameLease()
    {
        using var fixture = new Fixture();
        using var lease = PatchDatabaseOperationLeaseCore.Acquire(fixture.Root);
        string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.Combine(fixture.Root, "config", "patch2"));
        bool invoked = false;
        var result = ContentRepoGitService.InitializeOrUpdateWithLease(relative, () =>
        {
            invoked = true;
            return new Patch2GitResult { Kind = Patch2GitResultKind.Success };
        });
        Assert.Equal(Patch2GitResultKind.AlreadyRunning, result.Kind);
        Assert.False(invoked);
    }

    [Fact]
    public void OneBaseHasOneLeaseAcrossOperationIds()
    {
        using var fixture = new Fixture();
        using var first = PatchDatabaseOperationLeaseCore.Acquire(fixture.Root);
        Assert.Throws<PatchDatabaseOperationLeaseCore.BusyException>(() =>
            PatchDatabaseOperationLeaseCore.Acquire(fixture.Root));
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".patch2-import", "lease.lock")));
    }

    [Fact]
    public void DisposingLeaseDoesNotDeleteItsIdentityFile()
    {
        using var fixture = new Fixture();
        using (PatchDatabaseOperationLeaseCore.Acquire(fixture.Root)) { }
        Assert.True(File.Exists(Path.Combine(fixture.Root, ".patch2-import", "lease.lock")));
        using var next = PatchDatabaseOperationLeaseCore.Acquire(fixture.Root);
    }

    [Fact]
    public void Patch2RepoUsesSameBaseLease()
    {
        using var fixture = new Fixture();
        string repo = Path.Combine(fixture.Root, "config", "patch2");
        Assert.Equal(Path.GetFullPath(fixture.Root),
            PatchDatabaseOperationLeaseCore.BaseForPatch2Repository(repo));
        using var first = PatchDatabaseOperationLeaseCore.Acquire(fixture.Root);
        Assert.Throws<PatchDatabaseOperationLeaseCore.BusyException>(() =>
            PatchDatabaseOperationLeaseCore.AcquireForPatch2Repository(repo));
        Assert.Null(PatchDatabaseOperationLeaseCore.AcquireForPatch2Repository(
            Path.Combine(fixture.Root, "resources", "FE-Repo")));
    }

    [Fact]
    public void ForeignProcessCannotAcquireUntilHolderReleases()
    {
        using var fixture = new Fixture();
        using (PatchDatabaseOperationLeaseCore.Acquire(fixture.Root))
            Assert.Equal("busy", RunProbe(fixture.Root));
        Assert.Equal("acquired", RunProbe(fixture.Root));
    }

    [Fact]
    public void ChildLeaseProbe()
    {
        string? root = Environment.GetEnvironmentVariable("FEBUILDER_TEST_ZIP_LEASE_ROOT");
        if (string.IsNullOrEmpty(root)) return;
        string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "TestResults")) + Path.DirectorySeparatorChar;
        Assert.StartsWith(allowed, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        string result;
        try
        {
            using var lease = PatchDatabaseOperationLeaseCore.Acquire(root);
            result = "acquired";
        }
        catch (PatchDatabaseOperationLeaseCore.BusyException) { result = "busy"; }
        File.WriteAllText(Path.Combine(root, "child-result.txt"), result);
    }

    static string RunProbe(string root)
    {
        string result = Path.Combine(root, "child-result.txt");
        if (File.Exists(result)) File.Delete(result);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        start.ArgumentList.Add("vstest");
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--Tests:FEBuilderGBA.Core.Tests.PatchDatabaseOperationLeaseCoreTests.ChildLeaseProbe");
        start.Environment["FEBUILDER_TEST_ZIP_LEASE_ROOT"] = root;
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        using var process = Process.Start(start)!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(true);
            throw new TimeoutException("The cooperating lease probe timed out.");
        }
        Assert.True(process.ExitCode == 0, stdout.Result + stderr.Result);
        Assert.True(File.Exists(result));
        return File.ReadAllText(result);
    }

    sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "TestResults",
            "lease-" + Guid.NewGuid().ToString("N"));

        public Fixture() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
