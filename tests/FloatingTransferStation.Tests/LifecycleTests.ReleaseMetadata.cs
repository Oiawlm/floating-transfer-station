using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FloatingTransferStation.Tests;

public sealed partial class LifecycleTests
{
    [TestMethod]
    [TestCategory("Adversarial")]
    public void ReleaseMetadata_UsesOneConsistentVersion()
    {
        var repositoryRoot = FindRepositoryRoot();
        var expectedVersion = File.ReadAllText(Path.Combine(repositoryRoot, "version.txt")).Trim();
        StringAssert.Matches(expectedVersion, new Regex(@"^\d+\.\d+\.\d+$"));
        var project = XDocument.Load(Path.Combine(
            repositoryRoot, "src", "FloatingTransferStation", "FloatingTransferStation.csproj"));
        Assert.IsFalse(project.Descendants("Version").Any(), "The project must inherit the shared version.");
        var buildProperties = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Build.props"));
        var versionProperty = buildProperties.Descendants("Version").Single().Value;
        StringAssert.Contains(versionProperty, "version.txt");
        StringAssert.Contains(versionProperty, "ReadAllText");

        var assembly = typeof(ProductIdentity).Assembly;
        Assert.AreEqual(expectedVersion, assembly.GetName().Version!.ToString(3));
        Assert.AreEqual(expectedVersion, ProductIdentity.Version);
        Assert.AreEqual(
            expectedVersion,
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0]);

        var installerFiles = Directory.GetFiles(Path.Combine(repositoryRoot, "installer"), "*.iss");
        Assert.HasCount(1, installerFiles);
        var installer = File.ReadAllText(installerFiles[0]);
        StringAssert.Contains(installer, "FileOpen(AddBackslash(SourcePath) + \"..\\version.txt\")");
        StringAssert.Contains(installer, "#define MyAppVersion Trim(FileRead(MyAppVersionFile))");
        StringAssert.Contains(installer, "FileClose(MyAppVersionFile)");
        var setupSections = GetSetupSections(installer);
        Assert.HasCount(1, setupSections);
        foreach (var setting in new[] { "AppVersion", "VersionInfoVersion" })
        {
            Assert.HasCount(1, Regex.Matches(
                setupSections[0].Groups["Body"].Value,
                $@"(?im)^\s*{setting}\s*=\s*\{{#MyAppVersion\}}\s*$"));
        }

        const string duplicateSetupSections = """
            [Setup]
            VersionInfoVersion={#MyAppVersion}

            [Setup]
            VersionInfoVersion={#MyAppVersion}
            """;
        Assert.HasCount(2, GetSetupSections(duplicateSetupSections));
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void PublicReleaseMaterials_DescribeInstallerLicenseRoadmapAndContribution()
    {
        var repositoryRoot = FindRepositoryRoot();
        var expectedVersion = File.ReadAllText(Path.Combine(repositoryRoot, "version.txt")).Trim();
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        var changelog = File.ReadAllText(Path.Combine(repositoryRoot, "CHANGELOG.md"));
        var license = File.ReadAllText(Path.Combine(repositoryRoot, "LICENSE"));
        var installerAssetNames = Regex.Matches(
                readme, @"FloatingTransferStation-Setup-\d+\.\d+\.\d+\.exe")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { $"FloatingTransferStation-Setup-{expectedVersion}.exe" },
            installerAssetNames,
            "README must identify the current installer asset.");
        StringAssert.Contains(readme, "https://github.com/Oiawlm/floating-transfer-station/releases");
        foreach (var document in new[] { "LICENSE", "CONTRIBUTING.md", "ROADMAP.md" })
        {
            StringAssert.Contains(readme, document);
            Assert.IsTrue(File.Exists(Path.Combine(repositoryRoot, document)));
        }

        // Keep the documented user contracts without locking ordinary release prose.
        StringAssert.Matches(readme, new Regex(@"(?m)^-.*`Ctrl \+ A`.*当前分类.*(?:全部|所有)"));
        StringAssert.Matches(readme, new Regex(@"(?m)^-.*`Esc`.*取消.*选择"));
        StringAssert.Matches(readme, new Regex(@"(?m)^-.*`Delete`.*`Backspace`.*只.*选中"));
        StringAssert.Matches(readme, new Regex(@"(?m)^-.*`F2`.*改名.*展开分类"));
        StringAssert.Matches(readme, new Regex(@"(?m)^-.*`Ctrl \+ P`.*展开.*编辑"));
        StringAssert.Matches(changelog, new Regex(@"(?m)^## 未发布\s*$"));
        StringAssert.Matches(changelog, new Regex($@"(?m)^## {Regex.Escape(expectedVersion)}\s*$"));
        StringAssert.Contains(license, "MIT License");
        StringAssert.Matches(license, new Regex(@"Copyright \(c\) \d{4}(?:-\d{4})? Oiawlm"));
        StringAssert.Contains(
            File.ReadAllText(Path.Combine(repositoryRoot, "CONTRIBUTING.md")),
            "dotnet.exe test FloatingTransferStation.slnx -c Release --no-restore");
    }
}
