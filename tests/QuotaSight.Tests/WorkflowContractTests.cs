using System.Xml.Linq;
using Xunit;

namespace QuotaSight.Tests;

public sealed class WorkflowContractTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Ci = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "ci.yml"));
    private static readonly string Release = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "release.yml"));
    private static readonly string UiProject = File.ReadAllText(Path.Combine(Root, "src", "QuotaSight.UI", "QuotaSight.UI.csproj"));
    private static readonly string UiProgram = File.ReadAllText(Path.Combine(Root, "src", "QuotaSight.UI", "Program.cs"));

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

    [Fact]
    public void Ui_project_is_a_windows_gui_subsystem_app_only_on_windows()
    {
        Assert.Contains("<OutputType Condition=\"'$(OS)' == 'Windows_NT'\">WinExe</OutputType>", UiProject);
        Assert.Contains("<OutputType Condition=\"'$(OS)' != 'Windows_NT'\">Exe</OutputType>", UiProject);
        Assert.DoesNotContain("<OutputType>Exe</OutputType>", UiProject);
        Assert.DoesNotContain("<OutputType>WinExe</OutputType>", UiProject);
    }

    [Fact]
    public void Ui_project_keeps_native_aot_enabled()
    {
        Assert.Contains("<PublishAot>true</PublishAot>", UiProject);
        Assert.DoesNotContain("PublishAot>false", UiProject + Release);
    }

    [Fact]
    public void Console_is_attached_only_for_the_smoke_test_and_notification_probe()
    {
        Assert.Contains("if (smokeTest || notificationProbe)", UiProgram);
        Assert.Contains("[LibraryImport(\"kernel32.dll\"", UiProgram);
        Assert.Contains("SetLastError = true", UiProgram);
        Assert.Contains("AttachConsole(AttachParentProcess)", UiProgram);
        Assert.Contains("#if WINDOWS", UiProgram);
    }

    [Fact]
    public void Release_windows_smoke_test_waits_for_the_gui_process_and_checks_the_exit_code()
    {
        Assert.Contains(
            "Start-Process -FilePath .\\publish\\QuotaSight.UI.exe -ArgumentList '--smoke-test' -Wait -PassThru",
            Release);
        Assert.Contains("$process.ExitCode -ne 0", Release);
        Assert.DoesNotContain("& .\\publish\\QuotaSight.UI.exe", Release);
    }

    [Fact]
    public void Release_verifies_the_published_windows_pe_subsystem_is_gui()
    {
        Assert.Contains("[System.BitConverter]::ToInt32($bytes, 0x3C)", Release);
        Assert.Contains("[System.BitConverter]::ToUInt16($bytes, $eLfanew + 0x5C)", Release);
        Assert.Contains("$subsystem -ne 2", Release);
        Assert.Contains("WINDOWS_GUI", Release);
    }

    [Fact]
    public void Release_removes_debug_symbols_from_the_release_archives()
    {
        Assert.Contains("*.pdb", Release);
        Assert.Contains("*.dbg", Release);
        Assert.Contains("find publish -type f \\( -name '*.pdb' -o -name '*.dbg' \\) -delete", Release);
        Assert.Contains("Get-ChildItem -Path publish -Recurse -File -Include '*.pdb','*.dbg' | Remove-Item -Force", Release);
        Assert.DoesNotContain("name: symbols-", Release);
        Assert.DoesNotContain("Upload debug symbols", Release);
    }

    [Fact]
    public void Ui_project_stops_generating_debug_symbols_in_release()
    {
        Assert.Contains("<DebugType>none</DebugType>", UiProject);
        Assert.Contains("<DebugSymbols>false</DebugSymbols>", UiProject);
        Assert.Contains("<StripSymbols>true</StripSymbols>", UiProject);
        Assert.Contains("<AllowedReferenceRelatedFileExtensions>none</AllowedReferenceRelatedFileExtensions>", UiProject);
        Assert.Contains("Condition=\"'$(Configuration)' == 'Release'\"", UiProject);
    }

    [Fact]
    public void Release_fails_when_an_archive_still_contains_debug_symbols()
    {
        Assert.Contains(@"grep -Eq '\.(pdb|dbg)$'", Release);
        Assert.Contains("debug symbols leaked into $zip_file", Release);
        Assert.Contains("debug symbols leaked into $folder.zip", Release);
        Assert.Contains("grep -Evq \"^$folder/\"", Release);
    }

    [Fact]
    public void Release_archives_one_versioned_runtime_folder_on_both_platforms()
    {
        Assert.Contains("folder=\"QuotaSight-$VERSION-${{ matrix.rid }}\"", Release);
        Assert.Contains("zip -r \"$folder.zip\" \"$folder\"", Release);
        Assert.Contains("$folder = \"QuotaSight-$env:VERSION-$env:RID\"", Release);
        Assert.Contains("Compress-Archive -Path $folder -DestinationPath \"$folder.zip\"", Release);
        Assert.Contains("$_ -ne \"$folder/\" -and $_ -notlike \"$folder/*\"", Release);
        Assert.Contains("archive does not contain $folder/", Release);
        Assert.DoesNotContain("Compress-Archive -Path publish\\*", Release);
        Assert.DoesNotContain("zip -r \"QuotaSight-$VERSION-${{ matrix.rid }}.zip\" publish", Release);
    }

    [Fact]
    public void Release_keeps_the_linux_executable_bit_before_zipping()
    {
        Assert.Contains("chmod +x publish/QuotaSight.UI", Release);
        Assert.Contains("chmod +x \"$folder/QuotaSight.UI\"", Release);
        Assert.Contains("test -x \"$folder/QuotaSight.UI\"", Release);
        Assert.Contains("mv publish \"$folder\"", Release);
    }

    [Fact]
    public void Release_lists_the_archive_contents_in_the_log()
    {
        Assert.Contains("unzip -l", Release);
        Assert.Contains("[System.IO.Compression.ZipFile]::OpenRead", Release);
    }

    [Fact]
    public void Ci_windows_smoke_test_waits_for_the_gui_process()
    {
        Assert.Contains("Start-Process -FilePath .\\publish\\QuotaSight.UI.exe -ArgumentList '--smoke-test' -Wait -PassThru", Ci);
        Assert.Contains("$process.ExitCode -ne 0", Ci);
    }

    [Fact]
    public void Ui_project_references_windows_app_sdk_foundation_instead_of_the_metapackage()
    {
        var references = XDocument.Parse(UiProject).Descendants("PackageReference").ToArray();

        Assert.DoesNotContain(references, reference => (string?)reference.Attribute("Include") == "Microsoft.WindowsAppSDK");

        var foundation = Assert.Single(
            references,
            reference => (string?)reference.Attribute("Include") == "Microsoft.WindowsAppSDK.Foundation");
        Assert.Equal("2.3.12", (string?)foundation.Attribute("Version"));
        Assert.Equal("'$(OS)' == 'Windows_NT'", (string?)foundation.Parent?.Attribute("Condition"));
    }

    [Fact]
    public void Ui_project_pins_interactive_experiences_at_the_version_foundation_resolves_to()
    {
        var reference = Assert.Single(
            XDocument.Parse(UiProject).Descendants("PackageReference"),
            element => (string?)element.Attribute("Include") == "Microsoft.WindowsAppSDK.InteractiveExperiences");

        Assert.Equal("2.1.9", (string?)reference.Attribute("Version"));
        Assert.Equal("'$(OS)' == 'Windows_NT'", (string?)reference.Parent?.Attribute("Condition"));
    }

    [Fact]
    public void Ui_project_keeps_windows_app_sdk_self_contained()
    {
        Assert.Contains("<WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>", UiProject);
        Assert.DoesNotContain("WindowsAppSDKSelfContained>false", UiProject);
    }

    [Fact]
    public void Ui_project_keeps_windows_app_sdk_build_assets_private_to_referencing_projects()
    {
        // The test projects reference the UI project. If the Windows App SDK build assets flow into
        // them, Base's targets run there too: its self-contained check fails the Windows build (a
        // test project is still a library when Base decides its default, because
        // Microsoft.NET.Test.Sdk switches OutputType to Exe later), and the generated
        // auto-initializer source collides with the copy compiled into the UI assembly. The tests
        // only use the UI project's types, so the build assets stay private to it.
        var document = XDocument.Parse(UiProject);
        var properties = document.Descendants("WindowsAppSdkPrivateAssets").Single();
        var assets = properties.Value.Split(';');
        Assert.Contains("build", assets);
        Assert.Contains("buildtransitive", assets);
        Assert.DoesNotContain("all", assets);
        Assert.DoesNotContain("compile", assets);
        Assert.DoesNotContain("runtime", assets);

        foreach (var name in new[] { "Microsoft.WindowsAppSDK.Foundation", "Microsoft.WindowsAppSDK.InteractiveExperiences" })
        {
            var reference = document.Descendants("PackageReference").Single(r => (string?)r.Attribute("Include") == name);
            Assert.Equal("$(WindowsAppSdkPrivateAssets)", (string?)reference.Attribute("PrivateAssets"));
        }

        Assert.False(File.Exists(Path.Combine(Root, "tests", "Directory.Build.props")),
            "The test projects must not be turned into self-contained Windows App SDK apps.");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuotaSight.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("QuotaSight root not found.");
    }
}
