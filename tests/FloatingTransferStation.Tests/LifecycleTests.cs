using System.Text.RegularExpressions;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed partial class LifecycleTests
{
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
}
