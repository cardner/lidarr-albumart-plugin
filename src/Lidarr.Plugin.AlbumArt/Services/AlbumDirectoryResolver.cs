namespace NzbDrone.Core.Plugins.AlbumArt.Services;

public sealed record AlbumDirectoryResolution(
    IReadOnlyList<string> Directories,
    string? RejectionReason = null)
{
    public bool HasSafeTarget => Directories.Count > 0;
}

public static class AlbumDirectoryResolver
{
    public static AlbumDirectoryResolution Resolve(
        IEnumerable<string> trackPaths,
        string? artistPath = null,
        string? rootFolderPath = null)
    {
        ArgumentNullException.ThrowIfNull(trackPaths);

        var normalizedArtistPath = NormalizeDirectory(artistPath);
        var normalizedRootPath = NormalizeDirectory(rootFolderPath);
        var trackDirectories = trackPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeDirectory(Path.GetDirectoryName(path)))
            .OfType<string>()
            .Distinct(PathComparer)
            .ToList();

        if (trackDirectories.Count == 0)
        {
            return new AlbumDirectoryResolution([], "no track directories");
        }

        var candidates = new List<string>();
        var commonDirectory = FindCommonDirectory(trackDirectories);
        if (commonDirectory is not null)
        {
            candidates.Add(commonDirectory);
        }

        candidates.AddRange(trackDirectories);

        // A single imported disc commonly stores tracks in CD1/Disc 1 while
        // album artwork belongs in the parent album directory.
        if (trackDirectories.Count == 1 && LooksLikeDiscDirectory(trackDirectories[0]))
        {
            var parent = Directory.GetParent(trackDirectories[0])?.FullName;
            if (parent is not null)
            {
                candidates.Insert(0, parent);
            }
        }

        var safeCandidates = candidates
            .Select(NormalizeDirectory)
            .OfType<string>()
            .Where(candidate => IsSafeAlbumDirectory(
                candidate,
                trackDirectories,
                normalizedArtistPath,
                normalizedRootPath))
            .Distinct(PathComparer)
            .ToList();

        return safeCandidates.Count > 0
            ? new AlbumDirectoryResolution(safeCandidates)
            : new AlbumDirectoryResolution(
                [],
                $"no safe album directory beneath artist='{normalizedArtistPath ?? "(unknown)"}' and root='{normalizedRootPath ?? "(unknown)"}'");
    }

    internal static bool IsSafeAlbumDirectory(
        string candidate,
        IReadOnlyList<string> trackDirectories,
        string? artistPath,
        string? rootFolderPath)
    {
        if (IsFileSystemRoot(candidate) ||
            IsSamePath(candidate, artistPath) ||
            IsSamePath(candidate, rootFolderPath))
        {
            return false;
        }

        if (artistPath is not null && !IsSameOrDescendant(candidate, artistPath))
        {
            return false;
        }

        if (rootFolderPath is not null && !IsSameOrDescendant(candidate, rootFolderPath))
        {
            return false;
        }

        return trackDirectories.All(directory => IsSameOrDescendant(directory, candidate));
    }

    internal static bool IsSameOrDescendant(string path, string candidate)
    {
        var relative = Path.GetRelativePath(candidate, path);
        return relative == "." ||
               (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                relative != ".." &&
                !Path.IsPathRooted(relative));
    }

    private static string? FindCommonDirectory(IReadOnlyList<string> directories)
    {
        var candidate = new DirectoryInfo(directories[0]);
        while (candidate is not null)
        {
            if (directories.All(directory => IsSameOrDescendant(directory, candidate.FullName)))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        return null;
    }

    private static bool LooksLikeDiscDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return name.StartsWith("cd", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("disc", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("disk", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFileSystemRoot(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrWhiteSpace(root) && IsSamePath(path, root);
    }

    private static bool IsSamePath(string path, string? other)
    {
        return other is not null && PathComparer.Equals(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            other.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            return root is not null && PathComparer.Equals(fullPath, root)
                ? root
                : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
