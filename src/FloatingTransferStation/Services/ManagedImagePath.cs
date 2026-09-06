namespace FloatingTransferStation.Services;

public static class ManagedImagePath
{
    public static bool IsAllowed(string imagesDirectory, string path)
    {
        if (string.IsNullOrWhiteSpace(imagesDirectory) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(imagesDirectory));
            var fullPath = Path.GetFullPath(path);
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Configured parent directories may be links, but the managed root and
            // every component below it must be ordinary directories/files.
            if (!IsOrdinaryPath(root, mustBeDirectory: true))
            {
                return false;
            }

            var components = fullPath[prefix.Length..].Split(Path.DirectorySeparatorChar);
            var current = root;
            for (var index = 0; index < components.Length; index++)
            {
                current = Path.Combine(current, components[index]);
                if (!IsOrdinaryPath(current, mustBeDirectory: index < components.Length - 1))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or IOException or
                UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsOrdinaryPath(string path, bool mustBeDirectory)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) == 0 &&
                   ((attributes & FileAttributes.Directory) != 0) == mustBeDirectory;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }
}
