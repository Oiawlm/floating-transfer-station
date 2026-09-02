using FloatingTransferStation.Services;
using System.Text.RegularExpressions;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class LifecycleTests
{
    [TestMethod]
    [TestCategory("Adversarial")]
    public void SingleInstance_SecondGuardFailsUntilFirstIsDisposed()
    {
        var name = $@"Local\FloatingTransferStation.Tests.{Guid.NewGuid():N}";

        Assert.IsTrue(SingleInstanceGuard.TryAcquire(name, out var first));
        Assert.IsFalse(SingleInstanceGuard.TryAcquire(name, out var second));
        Assert.IsNull(second);
        first!.Dispose();

        Assert.IsTrue(SingleInstanceGuard.TryAcquire(name, out var third));
        third!.Dispose();
    }

    [TestMethod]
    public void StartupRegistration_DevelopmentBuildDoesNotTouchRegistry()
    {
        var values = new FakeStartupValueStore();
        var service = new StartupRegistrationService(
            @"C:\Users\tester\AppData\Local\Programs\悬浮中转站",
            values);

        var registered = service.EnsureRegistered(@"D:\repo\artifacts\debug\悬浮中转站.exe");

        Assert.IsFalse(registered);
        Assert.AreEqual(0, values.Writes.Count);
    }

    [TestMethod]
    public void StartupRegistration_InstalledBuildWritesQuotedExecutable()
    {
        var values = new FakeStartupValueStore();
        var installDirectory = @"C:\Users\tester\AppData\Local\Programs\悬浮中转站";
        var executable = Path.Combine(installDirectory, "悬浮中转站.exe");
        var service = new StartupRegistrationService(installDirectory, values);

        var registered = service.EnsureRegistered(executable);

        Assert.IsTrue(registered);
        Assert.AreEqual(
            ("悬浮中转站", $"\"{executable}\""),
            values.Writes.Single());
    }

    [TestMethod]
    public void StartupRegistration_DefaultUsesTheCurrentExecutableDirectory()
    {
        var values = new FakeStartupValueStore();
        var executable = @"D:\应用\悬浮中转站\悬浮中转站.exe";
        var service = StartupRegistrationService.CreateDefault(executable, values);

        Assert.IsTrue(service.EnsureRegistered(executable));
        Assert.AreEqual(
            (ProductIdentity.DisplayName, $"\"{executable}\""),
            values.Writes.Single());
    }

    [TestMethod]
    public void StartupRegistration_DefaultRejectsNullBlankAndRelativeExecutablePaths()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            StartupRegistrationService.CreateDefault(null!, new FakeStartupValueStore()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            StartupRegistrationService.CreateDefault("   ", new FakeStartupValueStore()));
        Assert.ThrowsExactly<ArgumentException>(() =>
            StartupRegistrationService.CreateDefault(@"悬浮中转站\悬浮中转站.exe", new FakeStartupValueStore()));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void StartupRegistration_WrongExecutableNameInInstallDirectoryDoesNotWrite()
    {
        var values = new FakeStartupValueStore();
        var installDirectory = @"C:\Users\tester\AppData\Local\Programs\悬浮中转站";
        var service = new StartupRegistrationService(installDirectory, values);

        var registered = service.EnsureRegistered(Path.Combine(installDirectory, "debug.exe"));

        Assert.IsFalse(registered);
        Assert.AreEqual(0, values.Writes.Count);
    }

    [TestMethod]
    public void AppLifecycle_OwnsSingleInstanceAndDelegatesInstalledStartup()
    {
        var values = new FakeStartupValueStore();
        var installDirectory = @"C:\Users\tester\AppData\Local\Programs\悬浮中转站";
        var startup = new StartupRegistrationService(installDirectory, values);
        using var lifecycle = new AppLifecycleService(startup);
        var mutexName = $@"Local\FloatingTransferStation.Tests.{Guid.NewGuid():N}";

        Assert.IsTrue(lifecycle.TryStart(mutexName));
        Assert.IsTrue(lifecycle.EnsureStartup(Path.Combine(installDirectory, "悬浮中转站.exe")));
        Assert.AreEqual(1, values.Writes.Count);
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void AppLifecycle_RepeatedStartKeepsOwnershipUntilDisposed()
    {
        var mutexName = $@"Local\FloatingTransferStation.Tests.{Guid.NewGuid():N}";
        var startup = new StartupRegistrationService(
            @"C:\Users\tester\AppData\Local\Programs\悬浮中转站",
            new FakeStartupValueStore());
        var lifecycle = new AppLifecycleService(startup);

        Assert.IsTrue(lifecycle.TryStart(mutexName));
        Assert.IsTrue(lifecycle.TryStart(mutexName));
        Assert.IsFalse(SingleInstanceGuard.TryAcquire(mutexName, out var competing));
        Assert.IsNull(competing);

        lifecycle.Dispose();

        Assert.IsTrue(SingleInstanceGuard.TryAcquire(mutexName, out var afterDispose));
        afterDispose!.Dispose();
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void ReleaseMetadata_UsesOneConsistentVersion()
    {
        const string expectedVersion = "1.4.1";
        var repositoryRoot = FindRepositoryRoot();
        var project = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FloatingTransferStation",
            "FloatingTransferStation.csproj"));
        var productIdentity = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "FloatingTransferStation",
            "ProductIdentity.cs"));
        var installerFiles = Directory.GetFiles(
            Path.Combine(repositoryRoot, "installer"),
            "*.iss",
            SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, installerFiles);
        var installer = File.ReadAllText(installerFiles[0]);

        var projectVersions = Regex.Matches(
            project,
            @"(?m)^\s*<Version>([^<\r\n]+)</Version>\s*$");
        var productVersions = Regex.Matches(
            productIdentity,
            @"(?m)^\s*public const string Version = ""([^""\r\n]+)"";\s*$");
        var installerVersions = Regex.Matches(
            installer,
            @"(?m)^\s*#define\s+MyAppVersion\s+""([^""\r\n]+)""\s*$");
        var setupSections = GetSetupSections(installer);
        Assert.AreEqual(
            1,
            setupSections.Count,
            "The project must contain exactly one authoritative [Setup] section.");
        var setupFileVersions = Regex.Matches(
            setupSections[0].Groups["Body"].Value,
            @"(?im)^\s*VersionInfoVersion\s*=\s*\{#MyAppVersion\}\s*$");

        Assert.AreEqual(1, projectVersions.Count);
        Assert.AreEqual(1, productVersions.Count);
        Assert.AreEqual(1, installerVersions.Count);
        Assert.AreEqual(
            1,
            setupFileVersions.Count,
            "The Setup binary file and product versions must reuse MyAppVersion.");
        CollectionAssert.AreEqual(
            new[] { expectedVersion, expectedVersion, expectedVersion },
            new[]
            {
                projectVersions[0].Groups[1].Value,
                productVersions[0].Groups[1].Value,
                installerVersions[0].Groups[1].Value,
            });
        const string duplicateSetupSections = """
            [Setup]
            VersionInfoVersion={#MyAppVersion}

            [Setup]
            VersionInfoVersion={#MyAppVersion}
            """;

        Assert.AreEqual(
            2,
            GetSetupSections(duplicateSetupSections).Count,
            "Setup-section discovery must not silently ignore a later section.");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        var changelog = File.ReadAllText(Path.Combine(repositoryRoot, "CHANGELOG.md"));
        var license = File.ReadAllText(Path.Combine(repositoryRoot, "LICENSE"));
        var roadmap = File.ReadAllText(Path.Combine(repositoryRoot, "ROADMAP.md"));
        var contributing = File.ReadAllText(Path.Combine(repositoryRoot, "CONTRIBUTING.md"));
        var projectGuide = File.ReadAllText(Path.Combine(repositoryRoot, "PROJECT_GUIDE.md"));
        var installerAssetNames = Regex.Matches(
                readme,
                @"FloatingTransferStation-Setup-\d+\.\d+\.\d+\.exe")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var changelogSections = Regex.Matches(
                changelog,
                @"(?ms)^## (?<Name>[^\r\n]+)\r?\n(?<Body>.*?)(?=^## |\z)")
            .ToDictionary(
                match => match.Groups["Name"].Value,
                match => match.Groups["Body"].Value,
                StringComparer.Ordinal);

        CollectionAssert.AreEqual(
            new[] { "FloatingTransferStation-Setup-1.4.1.exe" },
            installerAssetNames,
            "README must name only the latest installer asset.");
        StringAssert.Contains(readme, "批量置顶或取消置顶");
        StringAssert.Contains(readme, "`Ctrl + A`：选择当前分类全部内容");
        StringAssert.Contains(readme, "`Esc`：取消当前分类的全部选择");
        StringAssert.Contains(readme, "`Delete` 或 `Backspace`：只删除选中项");
        StringAssert.Contains(readme, "`F2`：改名当前展开分类");
        StringAssert.Contains(
            readme,
            "`Ctrl + P` 只在面板展开且不在编辑分类名称时生效");
        StringAssert.Contains(readme, "1.4.1 已通过自动质量门和安装包构建验证");
        StringAssert.Contains(readme, "不属于 1.4.1 承诺");
        Assert.IsFalse(
            readme.Contains("批量置顶和批量取消置顶还没有实现", StringComparison.Ordinal),
            "README must not describe batch pinning as unimplemented.");
        StringAssert.Contains(changelog, "## 未发布");
        StringAssert.Contains(changelog, "批量置顶与批量取消置顶");
        StringAssert.Contains(changelog, "`Ctrl + A` 选择当前分类全部内容");
        StringAssert.Contains(changelog, "`Esc` 取消当前分类全部选择");
        StringAssert.Contains(changelog, "`Delete` 键删除当前选择");
        StringAssert.Contains(changelog, "## 1.4.1");
        Assert.AreEqual(
            string.Empty,
            changelogSections["未发布"].Trim(),
            "Released notes must not remain in the unpublished section.");
        StringAssert.Contains(
            changelogSections["1.4.1"],
            "面板收起或编辑分类名称时，`Ctrl + P` 不再修改保留选择");
        StringAssert.Contains(changelog, "## 1.4.0");
        StringAssert.Contains(
            changelogSections["1.4.0"],
            "`F2` 改名当前展开分类");
        StringAssert.Contains(changelog, "## 1.3.0");
        StringAssert.Contains(
            changelogSections["1.3.0"],
            "`Delete` 键删除当前选择");
        StringAssert.Contains(
            changelogSections["1.3.0"],
            "面板收起后");
        StringAssert.Contains(changelog, "## 1.2.0");
        StringAssert.Contains(changelog, "## 1.1.0");
        StringAssert.Contains(changelog, "## 1.0.0");
        StringAssert.Contains(projectGuide, "当前稳定发布为 1.4.1");
        StringAssert.Contains(roadmap, "`Delete` 删除当前选择");
        StringAssert.Contains(roadmap, "`F2` 改名当前展开分类");
        StringAssert.Contains(license, "MIT License");
        StringAssert.Contains(license, "Copyright (c) 2026 Oiawlm");
        Assert.IsFalse(
            roadmap.Contains("**批量置顶与批量取消置顶**", StringComparison.Ordinal),
            "ROADMAP must not keep delivered work in the next-work section.");
        StringAssert.Contains(
            contributing,
            "dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore");
        Assert.IsFalse(
            readme.Contains("当前发布构建为 0.10.0", StringComparison.Ordinal),
            "README must not describe 0.10.0 as the current release.");
        Assert.IsFalse(
            readme.Contains("会在最终安装态确认完成后正式开放下载", StringComparison.Ordinal),
            "README must not describe an already approved release as pending install confirmation.");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Installer_ForceClosesRunningAppBeforeMutexFallbackAndAlwaysLaunchesInteractiveReplacement()
    {
        var repositoryRoot = FindRepositoryRoot();
        var installerFiles = Directory.GetFiles(
            Path.Combine(repositoryRoot, "installer"),
            "*.iss",
            SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, installerFiles);
        var installer = File.ReadAllText(installerFiles[0]);

        string[] expectedPreprocessorDirectives =
        [
            "#define MyAppName \"悬浮中转站\"",
            "#define MyAppVersion \"1.4.1\"",
            "#define MyAppExeName \"悬浮中转站.exe\"",
            "#define MyAppMutexName \"Local\\FloatingTransferStation.App\"",
        ];
        CollectionAssert.AreEqual(
            expectedPreprocessorDirectives,
            GetInstallerPreprocessorDirectives(installer),
            "The installer must contain only the approved literal preprocessor directives in order.");

        const string expressionRedefinitionCounterexample = """
            #define MyAppName "悬浮中转站"
            #define MyAppVersion "0.10.0"
            #define MyAppExeName "悬浮中转站.exe"
            #undef MyAppExeName
            #define MyAppExeName "note" + "pad.exe"
            #define MyAppMutexName "Local\FloatingTransferStation.App"
            """;
        Assert.IsFalse(
            expectedPreprocessorDirectives.SequenceEqual(
                GetInstallerPreprocessorDirectives(expressionRedefinitionCounterexample)),
            "Directive discovery must reject undef plus expression redefinition bypasses.");
        var includeCounterexample = string.Join('\n', expectedPreprocessorDirectives) +
            "\n#include \"unexpected.iss\"";
        Assert.IsFalse(
            expectedPreprocessorDirectives.SequenceEqual(
                GetInstallerPreprocessorDirectives(includeCounterexample)),
            "Directive discovery must reject include or other extra preprocessor directives.");

        const string officialExecutableName = "悬浮中转站.exe";
        Assert.AreEqual(
            officialExecutableName,
            $"{typeof(SingleInstanceGuard).Assembly.GetName().Name}.exe",
            "The official executable name must follow the application assembly identity.");
        var executableDefinitions = Regex.Matches(
            installer,
            @"(?im)^[ \t]*#define[ \t]+MyAppExeName[ \t]+""(?<Value>[^""\r\n]+)""[ \t]*\r?$");
        Assert.AreEqual(
            1,
            executableDefinitions.Count,
            "The installer must define the active official executable exactly once.");
        Assert.AreEqual(
            officialExecutableName,
            executableDefinitions[0].Groups["Value"].Value,
            "The installer must target only the official executable name.");

        var mutexDefinitions = Regex.Matches(
            installer,
            @"(?im)^[ \t]*#define[ \t]+MyAppMutexName[ \t]+""(?<Value>[^""\r\n]+)""[ \t]*\r?$");
        Assert.AreEqual(
            1,
            mutexDefinitions.Count,
            "The installer must define the active product mutex exactly once.");
        Assert.AreEqual(
            SingleInstanceGuard.ApplicationMutexName,
            mutexDefinitions[0].Groups["Value"].Value,
            "The installer and application must use the same product mutex.");
        Assert.IsTrue(
            Regex.IsMatch(
                installer,
                @"(?im)^[ \t]*AppMutex[ \t]*=[ \t]*\{#MyAppMutexName\}[ \t]*\r?$"),
            "The product mutex must remain Inno Setup's final overwrite guard.");
        Assert.AreEqual(
            1,
            Regex.Matches(installer, @"(?i)taskkill\.exe").Count,
            "The entire installer must mention taskkill exactly once.");
        Assert.IsTrue(
            Regex.IsMatch(installer, @"(?im)^[ \t]*DisableDirPage[ \t]*=[ \t]*no[ \t]*\r?$"),
            "The native installation directory page must remain enabled.");

        const string mixedCaseDuplicateSections =
            "[Run]\n" +
            "First launch\n" +
            " [rUn]\n" +
            "Second launch\n" +
            "[Code]\n" +
            "First bootstrap\n" +
            "\t[cOdE]\n" +
            "Second bootstrap";
        var mixedRunSections = GetInstallerSections(mixedCaseDuplicateSections, "Run");
        Assert.AreEqual(
            2,
            mixedRunSections.Count,
            "Installer section discovery must treat mixed-case [Run] headers as duplicates.");
        Assert.AreEqual(
            "First launch",
            NormalizeInstallerSectionBody(mixedRunSections[0].Groups["Body"].Value),
            "A whitespace-prefixed section header must terminate the preceding [Run] body.");
        var mixedCodeSections = GetInstallerSections(mixedCaseDuplicateSections, "Code");
        Assert.AreEqual(
            2,
            mixedCodeSections.Count,
            "Installer section discovery must treat mixed-case [Code] headers as duplicates.");
        Assert.AreEqual(
            "First bootstrap",
            NormalizeInstallerSectionBody(mixedCodeSections[0].Groups["Body"].Value),
            "A whitespace-prefixed section header must terminate the preceding [Code] body.");

        string[] expectedInlinePreprocessorExpressions =
        [
            "MyAppName",
            "MyAppVersion",
            "MyAppVersion",
            "MyAppName",
            "MyAppVersion",
            "MyAppName",
            "MyAppName",
            "MyAppVersion",
            "MyAppName",
            "MyAppExeName",
            "MyAppMutexName",
            "MyAppName",
            "MyAppExeName",
            "MyAppName",
            "MyAppName",
            "MyAppExeName",
            "MyAppExeName",
            "MyAppName",
            "MyAppMutexName",
            "MyAppExeName",
        ];
        CollectionAssert.AreEqual(
            expectedInlinePreprocessorExpressions,
            GetInstallerInlinePreprocessorExpressions(installer),
            "The installer must contain only the approved inline ISPP expressions in order.");
        const string inlineAssignmentFixture =
            "AppComments={#MyAppExeName = \"notepad.exe\"}";
        Assert.IsFalse(
            expectedInlinePreprocessorExpressions.SequenceEqual(
                GetInstallerInlinePreprocessorExpressions(
                    installer + '\n' + inlineAssignmentFixture)),
            "Inline ISPP discovery must reject assignment expressions anywhere in the installer.");

        var codeSections = GetInstallerSections(installer, "Code");
        Assert.AreEqual(
            1,
            codeSections.Count,
            "The installer must define exactly one [Code] bootstrap.");
        var code = NormalizeInstallerSectionBody(codeSections[0].Groups["Body"].Value);
        Assert.AreEqual(
            1,
            Regex.Matches(code, @"CheckForMutexes\(").Count,
            "The bootstrap must check the product mutex exactly once.");
        StringAssert.Contains(code, "CreateInputDirPage(wpSelectDir");
        StringAssert.Contains(code, "PrepareToInstall");
        StringAssert.Contains(code, "CurStepChanged");
        StringAssert.Contains(code, "InitializeUninstall");
        StringAssert.Contains(code, "RegWriteStringValue(HKCU");
        StringAssert.Contains(code, "function IsFullyQualifiedPath");
        StringAssert.Contains(code, "if not IsFullyQualifiedPath(Value) then");
        Assert.IsTrue(
            code.IndexOf("if not IsFullyQualifiedPath(Value) then", StringComparison.Ordinal) <
            code.IndexOf("ExpandFileName(Trim(Value))", StringComparison.Ordinal),
            "Path validation must reject relative registry values before they can be expanded into deletable absolute paths.");
        Assert.IsFalse(
            installer.Contains(@"\\", StringComparison.Ordinal),
            "Pascal strings use a single backslash for Windows path separators; doubled separators corrupt managed paths and root checks.");
        var isManagedDataDirectoryStart = code.IndexOf(
            "function IsManagedDataDirectory",
            StringComparison.Ordinal);
        var isManagedDataDirectory = code[
            isManagedDataDirectoryStart..code.IndexOf(
                "function GetLegacyDataDirectory",
                isManagedDataDirectoryStart,
                StringComparison.Ordinal)];
        StringAssert.Contains(
            isManagedDataDirectory,
            "if IsRootDirectory(ParentDirectory) then",
            "Managed data must reject a directory directly below a drive or UNC root.");
        StringAssert.Contains(code, "function IsExtendedDevicePath");
        StringAssert.Contains(code, "if IsExtendedDevicePath(Candidate) then");
        var prepareDataDirectoryMigrationStart = code.IndexOf(
            "function PrepareDataDirectoryMigration",
            StringComparison.Ordinal);
        var prepareDataDirectoryMigration = code[
            prepareDataDirectoryMigrationStart..code.IndexOf(
                "function PrepareToInstall",
                prepareDataDirectoryMigrationStart,
                StringComparison.Ordinal)];
        StringAssert.Contains(code, "function IsPathWithinDirectory");
        StringAssert.Contains(
            prepareDataDirectoryMigration,
            "if IsPathWithinDirectory(SelectedDataDirectory,");
        StringAssert.Contains(
            prepareDataDirectoryMigration,
            "if not DirExists(SelectedDataDirectory) and");
        StringAssert.Contains(
            prepareDataDirectoryMigration,
            "not ForceDirectories(SelectedDataDirectory) then");
        var copyDirectoryStart = code.IndexOf(
            "function CopyDirectory",
            StringComparison.Ordinal);
        var copyDirectory = code[
            copyDirectoryStart..code.IndexOf(
                "function DirectoryContentsMatch",
                copyDirectoryStart,
                StringComparison.Ordinal)];
        var directoryContentsMatchStart = code.IndexOf(
            "function DirectoryContentsMatch",
            StringComparison.Ordinal);
        var directoryContentsMatch = code[
            directoryContentsMatchStart..code.IndexOf(
                "function DeleteManagedDataDirectory",
                directoryContentsMatchStart,
                StringComparison.Ordinal)];
        StringAssert.Contains(code, "ErrorFileNotFound = 2");
        StringAssert.Contains(code, "ErrorNoMoreFiles = 18");
        StringAssert.Contains(code, "FindFirstFileW@kernel32.dll");
        StringAssert.Contains(code, "FindNextFileW@kernel32.dll");
        StringAssert.Contains(code, "GetLastError@kernel32.dll");
        StringAssert.Contains(
            code,
            "GetDateTimeString('yyyymmddhhnnss', #0, #0)",
            "Probe and migration names must pass Char separators to GetDateTimeString.");
        Assert.IsFalse(
            code.Contains("GetDateTimeString('yyyymmddhhnnss', '', '')", StringComparison.Ordinal),
            "Empty strings are not valid Char separators and cause data-page validation to fail at runtime.");
        Assert.IsFalse(
            code.Contains("DLLGetLastError", StringComparison.Ordinal),
            "Migration enumeration must not read error codes through Inno's DLL-only last-error helper.");
        StringAssert.Contains(copyDirectory, "FindErrorCode := GetLastError;");
        StringAssert.Contains(copyDirectory, "if FindErrorCode <> ErrorNoMoreFiles then");
        StringAssert.Contains(copyDirectory, "Result := FindErrorCode = ErrorFileNotFound;");
        StringAssert.Contains(directoryContentsMatch, "FindErrorCode := GetLastError;");
        StringAssert.Contains(directoryContentsMatch, "if FindErrorCode <> ErrorNoMoreFiles then");
        StringAssert.Contains(directoryContentsMatch, "Result := FindErrorCode = ErrorFileNotFound;");
        var initializeUninstallStart = code.IndexOf(
            "function InitializeUninstall",
            StringComparison.Ordinal);
        var initializeUninstall = code[
            initializeUninstallStart..code.IndexOf(
                "procedure CurUninstallStepChanged",
                initializeUninstallStart,
                StringComparison.Ordinal)];
        StringAssert.Contains(
            initializeUninstall,
            "MsgBox('无法安全删除已登记的内容目录：' + RegisteredValue");
        var curUninstallStepChanged = code[code.IndexOf(
            "procedure CurUninstallStepChanged",
            StringComparison.Ordinal)..];
        Assert.IsTrue(
            curUninstallStepChanged.IndexOf(
                "if not UninstallDataDirectoryValid then",
                StringComparison.Ordinal) <
            curUninstallStepChanged.IndexOf(
                "RegDeleteValue(HKCU",
                StringComparison.Ordinal),
            "Invalid registered data must preserve both registration values during uninstall.");
        Assert.IsFalse(
            Regex.IsMatch(code, @"(?m)^\s*Abort;\s*$"),
            "Post-install registration failure must leave the existing data registration intact and let setup clean the uncommitted copy.");

        var runSections = GetInstallerSections(installer, "Run");
        Assert.AreEqual(
            1,
            runSections.Count,
            "The installer must define exactly one [Run] section.");
        var run = NormalizeInstallerSectionBody(runSections[0].Groups["Body"].Value);
        const string expectedRun =
            "Filename: \"{app}\\{#MyAppExeName}\"; Description: \"启动 {#MyAppName}\"; Flags: nowait skipifsilent";
        Assert.AreEqual(
            expectedRun,
            run,
            "The one [Run] body must contain exactly the approved interactive replacement launch.");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void Installer_ProvidesExplicitUninstallEntryAndDeletesOnlyOwnedDataRoot()
    {
        var repositoryRoot = FindRepositoryRoot();
        var installerFiles = Directory.GetFiles(
            Path.Combine(repositoryRoot, "installer"),
            "*.iss",
            SearchOption.TopDirectoryOnly);
        Assert.HasCount(1, installerFiles);
        var installer = File.ReadAllText(installerFiles[0]);

        Assert.IsFalse(
            Regex.IsMatch(installer, @"(?im)^[ \t]*Uninstallable[ \t]*=[ \t]*no[ \t]*$"),
            "The standard Windows uninstall entry must remain enabled.");
        Assert.IsTrue(
            Regex.IsMatch(
                installer,
                @"(?im)^[ \t]*Name:[ \t]*""\{autoprograms\}\\卸载\{#MyAppName\}"";[ \t]*Filename:[ \t]*""\{uninstallexe\}""[ \t]*\r?$"),
            "The Start menu must expose the standard Inno uninstaller.");
        StringAssert.Contains(
            installer,
            "Flags: uninsdeletevalue",
            "The startup value must be removed during uninstall.");

        var deletionTargets = Regex.Matches(
                installer,
                @"(?im)^[ \t]*Type:[ \t]*filesandordirs;[ \t]*Name:[ \t]*""([^""\r\n]+)""[ \t]*\r?$")
            .Select(match => match.Groups[1].Value)
            .ToArray();
        Assert.IsEmpty(
            deletionTargets,
            "Data cleanup must resolve the registered managed path dynamically, not through [UninstallDelete].");
        StringAssert.Contains(installer, "DataDirectoryRegistryValue");
        StringAssert.Contains(installer, "IsManagedDataDirectory");
        StringAssert.Contains(installer, "DelTree");
        StringAssert.Contains(installer, "CurUninstallStepChanged");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "FloatingTransferStation.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found from the test output.");
    }

    private static MatchCollection GetInstallerSections(string installer, string sectionName) =>
        Regex.Matches(
            installer,
            $@"(?ims)^[ \t]*\[{Regex.Escape(sectionName)}\][ \t]*\r?\n(?<Body>.*?)(?=^[ \t]*\[|\z)");

    private static MatchCollection GetSetupSections(string installer) =>
        GetInstallerSections(installer, "Setup");

    private static string[] GetInstallerPreprocessorDirectives(string installer) =>
        Regex.Matches(
                installer,
                @"(?m)^[ \t]*(?<Directive>#[^\r\n]*)\r?$")
            .Select(match => match.Groups["Directive"].Value.TrimEnd(' ', '\t'))
            .ToArray();

    private static string[] GetInstallerInlinePreprocessorExpressions(string installer) =>
        Regex.Matches(
                installer,
                @"\{#(?<Expression>[^}\r\n]+)\}")
            .Select(match => match.Groups["Expression"].Value)
            .ToArray();

    private static string NormalizeInstallerSectionBody(string body) =>
        body.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd('\n');

    private sealed class FakeStartupValueStore : IStartupValueStore
    {
        public List<(string Name, string Value)> Writes { get; } = [];

        public void Set(string name, string value) => Writes.Add((name, value));
    }
}
