using System.Text.RegularExpressions;

namespace FloatingTransferStation.Tests;

public sealed partial class LifecycleTests
{
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
        Assert.IsTrue(changelogSections.ContainsKey("未发布"));
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
}
