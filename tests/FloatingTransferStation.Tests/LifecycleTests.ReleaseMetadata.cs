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
    public void PublicReleaseMaterials_DescribeInstallerLicenseAndRoadmap()
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
        foreach (var document in new[] { "LICENSE", "ROADMAP.md" })
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
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void RepoUrl_IsSingleSourcedFromProductIdentity()
    {
        // repo 地址单一来源（契约 #26）：设置窗 XAML 不得再维护仓库地址字面量，
        // 页脚链接与更新服务一律经 ProductIdentity 常量（code-behind 赋值/请求构造）。
        var repositoryRoot = FindRepositoryRoot();
        var settingsWindow = File.ReadAllText(
            Path.Combine(repositoryRoot, "src", "FloatingTransferStation", "Views", "SettingsWindow.xaml"));
        Assert.IsFalse(
            settingsWindow.Contains("github.com", StringComparison.OrdinalIgnoreCase),
            "SettingsWindow.xaml must not embed the repository URL literal; it is assigned from ProductIdentity in code-behind.");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void WeChatContact_IsSingleSourcedFromProductIdentity()
    {
        // 联系方式单一来源（2026-11-05，W5）：微信号唯一声明点是 ProductIdentity.WeChatId；
        // 应用内（src/ 的 C# 与 XAML）不得出现字面量副本——页脚与放大层经代码后置
        // 从 ProductIdentity 赋值（同 #26 repo 地址口径）。README「联系与反馈」段是
        // 对外发布面（数据即内容，无法绑定常量），展示值与二维码资产引用必须与
        // 单一来源一致，由本测试互锁。
        var repositoryRoot = FindRepositoryRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(repositoryRoot, "src"), "*.*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(file);
            if (!string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".xaml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(Path.GetFileName(file), "ProductIdentity.cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains(ProductIdentity.WeChatId, StringComparison.Ordinal))
            {
                offenders.Add(Path.GetRelativePath(repositoryRoot, file));
            }
        }

        Assert.AreEqual(
            0,
            offenders.Count,
            $"src/ 内不得出现微信号字面量副本（单一来源 ProductIdentity.WeChatId）：{string.Join("; ", offenders)}");
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        StringAssert.Contains(
            readme,
            ProductIdentity.WeChatId,
            "README「联系与反馈」段必须展示与 ProductIdentity 一致的微信号。");
        StringAssert.Contains(
            readme,
            "docs/images/wechat-qr.png",
            "README 必须引用仓库内微信二维码资产。");
        Assert.IsTrue(
            File.Exists(Path.Combine(repositoryRoot, "docs", "images", "wechat-qr.png")),
            "README 引用的微信二维码资产必须入库。");
    }

    [TestMethod]
    [TestCategory("Adversarial")]
    public void ReleaseAssetNaming_SingleSourcedAcrossInstallerBuildAndUpdater()
    {
        // 发行资产命名单一权威（契约 #26 修订①）：构建产物名＝更新器精确名资产＝README 链接名
        // ＝SHA256SUMS 条目名，四方同源于 UpdateService.InstallerAssetNamePrefix；构建脚本从
        // 实际产物派生清单，CI 附件直传。v1.71.0 事故（人工改名丢字符致资产名残缺＋手写清单
        // 条目为中文构建名 → 全体用户 AssetMissing）的根因＝两个无机器断言的人工环节，本互锁
        // 使任何一方的命名漂移在构建期即红。
        var repositoryRoot = FindRepositoryRoot();
        var prefix = Services.UpdateService.InstallerAssetNamePrefix;

        // ① 安装器脚本：OutputBaseFilename 精确行＝更新器前缀＋{#MyAppVersion}（版本段由
        // version.txt 唯一赋值），产物出生即发行资产名，人工改名环节从流程中消失。
        var installerFiles = Directory.GetFiles(Path.Combine(repositoryRoot, "installer"), "*.iss");
        Assert.HasCount(1, installerFiles);
        var installer = File.ReadAllText(installerFiles[0]);
        var expectedOutputLine = $"OutputBaseFilename={prefix}{{#MyAppVersion}}";
        StringAssert.Contains(
            installer,
            expectedOutputLine,
            $"installer .iss 的 OutputBaseFilename 必须逐字为「{expectedOutputLine}」（更新器精确名前缀＝UpdateService.InstallerAssetNamePrefix「{prefix}」），产物出生即发行资产名。");

        // ② 构建脚本：产物断言＝精确名表达式（前缀＋$version），SHA256SUMS.txt 从实际产物
        // Get-FileHash 派生——哈希与条目名取自实际文件，手写清单在结构上不可能。
        var buildScript = File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "build-release.ps1"));
        StringAssert.Contains(
            buildScript,
            $"\"{prefix}$version.exe\"",
            $"scripts/build-release.ps1 必须以精确名「{prefix}$version.exe」断言产物（与更新器精确名同源），不得回退通配匹配。");
        StringAssert.Contains(
            buildScript,
            "SHA256SUMS.txt",
            "scripts/build-release.ps1 必须生成 SHA256SUMS.txt（更新器唯一信任锚）。");
        StringAssert.Contains(
            buildScript,
            "Get-FileHash",
            "scripts/build-release.ps1 的 SHA256SUMS.txt 必须从实际产物 Get-FileHash 派生，禁止手写清单。");

        // ③ CI 附件：release-candidate 含 SHA256SUMS.txt——发行上传＝纯拷贝、取附件原名。
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github", "workflows", "ci.yml"));
        StringAssert.Contains(
            workflow,
            "artifacts/installer/SHA256SUMS.txt",
            ".github/workflows/ci.yml 的 release-candidate 附件必须包含 artifacts/installer/SHA256SUMS.txt（与安装包同附件直传）。");

        // ④ README 下载链接：资产名前缀段与更新器常量一致（与 PublicReleaseMaterials_* 的
        // 版本互锁互补，构成四方真互锁而非两处硬编码）。
        var readme = File.ReadAllText(Path.Combine(repositoryRoot, "README.md"));
        Assert.IsTrue(
            Regex.IsMatch(readme, Regex.Escape(prefix) + @"\d+\.\d+\.\d+\.exe"),
            $"README 下载链接必须引用「{prefix}{{version}}.exe」资产（前缀与更新器精确名一致）。");
    }
}
