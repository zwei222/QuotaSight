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
    // Read lazily so a missing file fails only the tests that assert its content instead of
    // throwing in the type initializer and failing the whole class.
    private static readonly string CollectorProject = ReadRepoFile("src", "QuotaSight.Collector", "QuotaSight.Collector.csproj");
    private static readonly string CollectorReadme = ReadRepoFile("src", "QuotaSight.Collector", "README.md");
    private static readonly string CollectorSetupGuide = ReadRepoFile("docs", "collector-linux-setup.md");
    private static readonly string RootReadme = ReadRepoFile("README.md");

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
    public void Release_windows_steps_that_read_the_runtime_identifier_declare_it()
    {
        // An environment variable set on one step is not visible to the next. The archive check
        // once read $env:RID without declaring it and looked for "QuotaSight-<version>-.zip".
        var steps = Release.Split("      - name: ").Skip(1);
        foreach (var step in steps.Where(x => x.Contains("$env:RID", StringComparison.Ordinal)))
        {
            Assert.Contains("RID: ${{ matrix.rid }}", step);
        }
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
        Assert.Equal("2.7.0", (string?)foundation.Attribute("Version"));
        Assert.Equal("'$(OS)' == 'Windows_NT'", (string?)foundation.Parent?.Attribute("Condition"));
    }

    [Fact]
    public void Ui_project_pins_interactive_experiences_at_the_version_foundation_resolves_to()
    {
        var references = XDocument.Parse(UiProject).Descendants("PackageReference").ToArray();
        var reference = Assert.Single(
            references,
            element => (string?)element.Attribute("Include") == "Microsoft.WindowsAppSDK.InteractiveExperiences");
        var foundation = Assert.Single(
            references,
            element => (string?)element.Attribute("Include") == "Microsoft.WindowsAppSDK.Foundation");

        // Foundation declares its dependency as the range [2.7.0, 3.0.0). Referencing the same exact
        // version keeps a later 2.7.x publish from changing the bundled framework payload without a
        // repository change, so the UI project pins a version and never a range.
        var version = (string?)reference.Attribute("Version");
        Assert.Equal("2.7.0", version);
        Assert.Equal((string?)foundation.Attribute("Version"), version);
        Assert.DoesNotContain("[", version!);
        Assert.DoesNotContain("*", version!);
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

    [Fact]
    public void Byte_exact_wire_fixture_keeps_its_line_endings_on_every_checkout()
    {
        // The Collector and the Worker tests compare against this file byte for byte. A Windows
        // checkout rewrites line endings by default, which turned its trailing "\n" into "\r\n"
        // and failed the Windows run only.
        var attributes = File.ReadAllText(Path.Combine(Root, ".gitattributes"));
        Assert.Contains(
            "src/QuotaSight.Usage.Api/test/fixtures/wire-v2-golden.json text eol=lf",
            attributes);
    }

    [Fact]
    public void Collector_project_enables_native_aot_without_suppressing_warnings()
    {
        Assert.Contains("<PublishAot>true</PublishAot>", CollectorProject);
        Assert.DoesNotContain("PublishAot>false", CollectorProject + Release);

        // The collector ships in the same Native AOT release lane as the UI, so it keeps the same
        // policy: trimming and AOT analysis stay on and their warnings are never silenced.
        var project = XDocument.Parse(CollectorProject);
        Assert.Empty(project.Descendants("NoWarn"));
        foreach (var suppressed in new[]
                 {
                     "IL2026", "IL3050", "IL2104", "IL3053", "ILLink",
                     "<SuppressTrimAnalysisWarnings>true</SuppressTrimAnalysisWarnings>",
                     "<EnableTrimAnalyzer>false</EnableTrimAnalyzer>",
                     "<EnableAotAnalyzer>false</EnableAotAnalyzer>",
                     "<NuGetAudit>false</NuGetAudit>",
                     "<NuGetAuditMode>all</NuGetAuditMode>",
                 })
        {
            Assert.DoesNotContain(suppressed, CollectorProject);
        }

        // Warning-free is enforced repository-wide, not relaxed for this project.
        Assert.Contains(
            "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>",
            File.ReadAllText(Path.Combine(Root, "Directory.Build.props")));
    }

    [Fact]
    public void Collector_project_stops_generating_debug_symbols_in_release()
    {
        Assert.Contains("<DebugType>none</DebugType>", CollectorProject);
        Assert.Contains("<DebugSymbols>false</DebugSymbols>", CollectorProject);
        Assert.Contains("<StripSymbols>true</StripSymbols>", CollectorProject);
        Assert.Contains("<AllowedReferenceRelatedFileExtensions>none</AllowedReferenceRelatedFileExtensions>", CollectorProject);
        Assert.Contains("Condition=\"'$(Configuration)' == 'Release'\"", CollectorProject);
    }

    [Fact]
    public void Release_publishes_the_collector_as_a_native_aot_linux_binary()
    {
        Assert.Contains(
            "dotnet publish src/QuotaSight.Collector/QuotaSight.Collector.csproj -c Release -r linux-x64 --self-contained true -p:PublishAot=true -p:Version=$VERSION --no-restore -o publish-collector",
            Release);
        Assert.Contains("chmod +x publish-collector/QuotaSight.Collector", Release);
    }

    [Fact]
    public void Release_smoke_tests_the_collector_binary_against_the_synthetic_fixture()
    {
        Assert.Contains(
            "publish-collector/QuotaSight.Collector --preview --generation 1 --bucket-time-zone Asia/Tokyo",
            Release);
        Assert.Contains("tests/QuotaSight.Collector.Tests/Fixtures/graph-4.23.0-positive.json", Release);
    }

    [Fact]
    public void Release_attaches_a_versioned_collector_archive()
    {
        Assert.Contains("collector_folder=\"QuotaSight-Collector-$VERSION-linux-x64\"", Release);
        Assert.Contains("collector_zip=\"$collector_folder.zip\"", Release);
        Assert.Contains("zip -r \"$collector_zip\" \"$collector_folder\"", Release);
        Assert.Contains("name: QuotaSight-Collector-${{ env.VERSION }}-linux-x64", Release);
        Assert.Contains("path: QuotaSight-Collector-${{ env.VERSION }}-linux-x64.zip", Release);
    }

    [Fact]
    public void Release_collector_archive_holds_exactly_one_executable_and_no_symbols()
    {
        // One file, the executable, under one versioned folder: the same rule the UI archive
        // follows. Anything else (a stray runtime, a symbol file, a second binary) is refused.
        Assert.Contains("unzip -Z1 \"$collector_zip\" | grep -v '/$' | wc -l", Release);
        Assert.Contains("the collector archive must hold exactly one file", Release);
        Assert.Contains("unzip -Z1 \"$collector_zip\" | grep -v '/$'", Release);
        Assert.Contains("\"${collector_folder}/QuotaSight.Collector\"", Release);
        Assert.Contains("debug symbols leaked into $collector_zip", Release);
    }

    [Fact]
    public void Ci_publishes_and_smokes_the_collector_native_aot_binary()
    {
        Assert.Contains(
            "dotnet publish src/QuotaSight.Collector/QuotaSight.Collector.csproj --configuration Release --runtime linux-x64 --self-contained true -p:PublishAot=true --no-restore -o publish-collector",
            Ci);
        Assert.Contains(
            "./publish-collector/QuotaSight.Collector --preview --generation 1 --bucket-time-zone Asia/Tokyo",
            Ci);
        Assert.Contains("tests/QuotaSight.Collector.Tests/Fixtures/graph-4.23.0-positive.json", Ci);
    }

    [Fact]
    public void Collector_setup_guide_covers_the_release_artifact_credentials_and_timer()
    {
        Assert.NotEmpty(CollectorSetupGuide);

        foreach (var required in new[]
                 {
                     "QuotaSight-Collector-<version>-linux-x64.zip",
                     "/usr/local/bin/QuotaSight.Collector",
                     "4.23.0",
                     "npx --yes tokscale@4.23.0",
                     "systemd-creds",
                     "LoadCredentialEncrypted=",
                     "CREDENTIALS_DIRECTORY",
                     "LoadCredential=",
                     "--user",
                     "--preview",
                     "--send",
                     "journalctl",
                     "example.invalid",
                 })
        {
            Assert.Contains(required, CollectorSetupGuide);
        }

        // tokscale 4.23.0 `graph` has neither --json (it always writes JSON) nor --days; the
        // window is a date filter, and the pinned version is what the adapter accepts.
        Assert.Contains("--since \"$(date -d '-30 days' +%F)\"", CollectorSetupGuide);
        Assert.Contains("--no-spinner", CollectorSetupGuide);
        Assert.DoesNotContain("--days 30", CollectorSetupGuide);
        Assert.DoesNotContain("tokscale graph --json", CollectorSetupGuide);

        // User units are the default, and the system-wide path is declared as an alternative.
        Assert.Contains("systemctl --user enable --now", CollectorSetupGuide);
        Assert.Contains("/etc/systemd/system", CollectorSetupGuide);
        Assert.Contains("WantedBy=default.target", CollectorSetupGuide);
        Assert.Contains("WantedBy=timers.target", CollectorSetupGuide);
        Assert.Contains("StateDirectoryMode=0700", CollectorSetupGuide);
        Assert.Contains("loginctl enable-linger", CollectorSetupGuide);

        // A state command needs STATE_DIRECTORY, which only the unit provides, so the first-run
        // floor is taken inside the same transient unit environment the timer uses.
        Assert.Contains("systemd-run --user --wait --pipe --collect", CollectorSetupGuide);
        Assert.Contains("-p StateDirectory=example-collector -p StateDirectoryMode=0700", CollectorSetupGuide);
        Assert.Contains("state_initialized: next revision 1", CollectorSetupGuide);
    }

    [Fact]
    public void Collector_setup_guide_keeps_operator_values_out_of_the_public_repository()
    {
        foreach (var forbidden in new[] { "/home/", "her" + "mes", "quota-sight.example", "cf-access" })
        {
            Assert.DoesNotContain(forbidden, CollectorSetupGuide, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Collector_readme_points_at_the_release_artifact_and_the_setup_guide()
    {
        Assert.Contains("QuotaSight-Collector-<version>-linux-x64.zip", CollectorReadme);
        Assert.Contains("docs/collector-linux-setup.md", CollectorReadme);
        Assert.Contains("systemd-creds", CollectorReadme);
        Assert.DoesNotContain("/home/" + "her" + "mes", CollectorReadme);
        Assert.Contains("docs/collector-linux-setup.md", RootReadme);
    }

    private static string ReadRepoFile(params string[] relative)
    {
        var path = Path.Combine([Root, .. relative]);
        return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "QuotaSight.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("QuotaSight root not found.");
    }
}
