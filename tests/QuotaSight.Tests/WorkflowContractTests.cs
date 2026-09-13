using Xunit;

namespace QuotaSight.Tests;

public sealed class WorkflowContractTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
    private static readonly string Release = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));

    [Fact]
    public void Ci_triggers_push_and_pull_request() =>
        Assert.Contains("push:", Ci);

    [Fact]
    public void Ci_has_pull_request_trigger() =>
        Assert.Contains("pull_request:", Ci);

    [Fact]
    public void Ci_permissions_are_read_only() =>
        Assert.Contains("contents: read", Ci);

    [Fact]
    public void Ci_matrix_is_fail_fast_false_for_both_runners()
    {
        Assert.Contains("fail-fast: false", Ci);
        Assert.Contains("ubuntu-latest", Ci);
        Assert.Contains("windows-latest", Ci);
    }

    [Fact]
    public void Ci_uses_dotnet_ten_and_solution_restore()
    {
        Assert.Contains("dotnet-version: 10.0.x", Ci);
        Assert.Contains("dotnet restore QuotaSight.slnx", Ci);
    }

    [Fact]
    public void Ci_builds_and_tests_release_without_restore()
    {
        Assert.Contains("dotnet build QuotaSight.slnx --configuration Release --no-restore", Ci);
        Assert.Contains("dotnet test QuotaSight.slnx --configuration Release --no-build --no-restore", Ci);
    }

    [Fact]
    public void Release_requires_manual_dispatch_tag_input()
    {
        Assert.Contains("workflow_dispatch:", Release);
        Assert.Contains("tag:", Release);
        Assert.Contains("required: true", Release);
    }

    [Fact]
    public void Release_validates_semver_tag()
    {
        const string pattern = "^v[0-9]+\\.[0-9]+\\.[0-9]+(-[0-9A-Za-z.-]+)?$";
        Assert.Contains(pattern, Release);
        Assert.Matches(pattern, "v1.2.3");
        Assert.Matches(pattern, "v1.2.3-rc.1");
        Assert.DoesNotMatch(pattern, "1.2.3");
        Assert.DoesNotMatch(pattern, "v1.2");
    }

    [Fact]
    public void Release_rejects_existing_remote_tag_and_release()
    {
        Assert.Contains("git ls-remote --exit-code --tags origin", Release);
        Assert.Contains("gh release view", Release);
        Assert.Contains("exit 1", Release);
    }

    [Fact]
    public void Release_restores_per_runtime_and_publishes_native_aot()
    {
        Assert.Contains("dotnet restore QuotaSight.slnx --runtime ${{ matrix.rid }}", Release);
        Assert.Contains("-r ${{ matrix.rid }}", Release);
        Assert.Contains("-p:PublishAot=true", Release);
        Assert.DoesNotContain("PublishAot=false", Release);
    }

    [Fact]
    public void Release_has_linux_and_windows_runtime_matrix()
    {
        Assert.Contains("linux-x64", Release);
        Assert.Contains("win-x64", Release);
        Assert.Contains("ubuntu-latest", Release);
        Assert.Contains("windows-latest", Release);
    }

    [Fact]
    public void Release_smoke_tests_published_binary_on_each_os()
    {
        Assert.Contains("--smoke-test", Release);
        Assert.Contains("chmod +x", Release);
        Assert.Contains(".exe", Release);
    }

    [Fact]
    public void Release_artifact_name_contains_version_and_rid()
    {
        Assert.Contains("QuotaSight-${{ env.VERSION }}-${{ matrix.rid }}.zip", Release);
        Assert.Contains("actions/upload-artifact", Release);
    }

    [Fact]
    public void Release_write_permission_is_limited_to_release_job()
    {
        Assert.Contains("contents: write", Release);
        Assert.Contains("permissions:\n  contents: read", Release.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Release_explicitly_targets_this_repository_and_built_commit()
    {
        Assert.Contains("GH_REPO: ${{ github.repository }}", Release);
        Assert.Contains("RELEASE_SHA: ${{ github.sha }}", Release);
        Assert.Contains("--target \"$RELEASE_SHA\"", Release);
    }

    [Fact]
    public void Workflows_do_not_hide_failures_or_use_eval()
    {
        Assert.DoesNotContain("continue-on-error", Ci + Release);
        Assert.DoesNotContain("eval ", Ci + Release);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuotaSight.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("QuotaSight root not found.");
    }
}
