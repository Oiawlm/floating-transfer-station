namespace FloatingTransferStation.Services;

public interface IDataDirectorySettings
{
    string? ReadDataDirectory();
}

public static class DataDirectorySettings
{
    public static string? NormalizeManagedDataDirectory(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            if (!Path.IsPathFullyQualified(candidate))
            {
                return null;
            }

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
            var owner = Directory.GetParent(full);
            return owner is not null &&
                   string.Equals(Path.GetFileName(full), "Data", comparison) &&
                   string.Equals(owner.Name, ProductIdentity.DisplayName, comparison)
                ? full
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
