using System.Text.RegularExpressions;
using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

public sealed partial class LifecycleTests
{
    [TestMethod]
    [TestCategory("Adversarial")]
    [DataRow(@"C:\Users\tester\AppData\Local\Programs\悬浮中转站")]
    [DataRow(@"D:\Custom apps\悬浮中转站")]
    public void Installer_RegistersQuotedStartupForSelectedInstallDirectory(string installDirectory)
    {
        var installerPath = Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "installer"), "*.iss").Single();
        var installer = File.ReadAllText(installerPath);
        var registrySections = GetInstallerSections(installer, "Registry");
        Assert.HasCount(1, registrySections);
        var startupEntries = registrySections[0].Groups["Body"].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains(@"Software\Microsoft\Windows\CurrentVersion\Run", StringComparison.Ordinal))
            .ToArray();
        Assert.HasCount(1, startupEntries);
        StringAssert.Contains(startupEntries[0], "Root: HKCU;");
        StringAssert.Contains(startupEntries[0], "ValueName: \"{#MyAppName}\"");
        StringAssert.Contains(startupEntries[0], "Flags: uninsdeletevalue");

        var value = Regex.Match(startupEntries[0], "ValueData:\\s*\"(?<Value>(?:\"\"|[^\"])*)\";");
        Assert.IsTrue(value.Success);
        var executableName = $"{typeof(SingleInstanceGuard).Assembly.GetName().Name}.exe";
        var expandedCommand = value.Groups["Value"].Value
            .Replace("\"\"", "\"", StringComparison.Ordinal)
            .Replace("{app}", installDirectory, StringComparison.Ordinal)
            .Replace("{#MyAppExeName}", executableName, StringComparison.Ordinal);

        Assert.AreEqual($"\"{Path.Combine(installDirectory, executableName)}\"", expandedCommand);
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
}
