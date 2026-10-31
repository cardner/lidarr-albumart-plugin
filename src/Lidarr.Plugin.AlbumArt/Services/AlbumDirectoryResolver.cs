using System.Text;

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
        string? rootFolderPath = null,
        bool? artistDirectoryExclusive = null)
    {
        ArgumentNullException.ThrowIfNull(trackPaths);

        var normalizedArtistPath = NormalizeDirectory(artistPath);
        var normalizedRootPath = NormalizeDirectory(rootFolderPath);
        var trackDirectories = trackPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizeDirectory(Path.GetDirectoryName(path)))
            .OfType<string>()
            .DistinctBy(GetComparisonKey, PathComparer)
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

        var safeCandidates = new List<string>();
        var rejections = new List<(string Candidate, string Cause)>();
        foreach (var candidate in candidates
                     .Select(NormalizeDirectory)
                     .OfType<string>()
                     .DistinctBy(GetComparisonKey, PathComparer))
        {
            var rejection = GetRejectionCause(
                candidate,
                trackDirectories,
                normalizedArtistPath,
                normalizedRootPath,
                artistDirectoryExclusive);
            if (rejection is null)
            {
                safeCandidates.Add(candidate);
            }
            else
            {
                rejections.Add((candidate, rejection));
            }
        }

        if (safeCandidates.Count > 0)
        {
            return new AlbumDirectoryResolution(safeCandidates);
        }

        var primaryRejection = rejections.FirstOrDefault(value =>
                                   value.Cause != "filesystem root") is { Candidate: not null } preferred
            ? preferred
            : rejections.FirstOrDefault();
        var trackDirectory = trackDirectories[0];
        return new AlbumDirectoryResolution(
            [],
            $"track directory='{trackDirectory}' candidate='{primaryRejection.Candidate ?? "(none)"}' rejected: {primaryRejection.Cause ?? "no candidates"}; " +
            $"artist='{normalizedArtistPath ?? "(unknown)"}' root='{normalizedRootPath ?? "(unknown)"}'");
    }

    internal static bool AreEquivalent(string? left, string? right)
    {
        var normalizedLeft = NormalizeDirectory(left);
        var normalizedRight = NormalizeDirectory(right);
        return normalizedLeft is not null &&
               normalizedRight is not null &&
               PathComparer.Equals(
                   GetComparisonKey(normalizedLeft),
                   GetComparisonKey(normalizedRight));
    }

    internal static string? GetIdentityKey(string? path)
    {
        var normalized = NormalizeDirectory(path);
        return normalized is null ? null : GetComparisonKey(normalized);
    }

    private static string? GetRejectionCause(
        string candidate,
        IReadOnlyList<string> trackDirectories,
        string? artistPath,
        string? rootFolderPath,
        bool? artistDirectoryExclusive)
    {
        if (IsFileSystemRoot(candidate))
        {
            return "filesystem root";
        }

        if (IsSamePath(candidate, rootFolderPath))
        {
            return "library root";
        }

        if (IsSamePath(candidate, artistPath) && artistDirectoryExclusive != true)
        {
            return artistDirectoryExclusive == false
                ? "shared by multiple albums"
                : "same as artist";
        }

        if (artistPath is not null && !IsSameOrDescendant(candidate, artistPath))
        {
            return "outside artist path";
        }

        if (rootFolderPath is not null && !IsSameOrDescendant(candidate, rootFolderPath))
        {
            return "outside library root";
        }

        return trackDirectories.All(directory => IsSameOrDescendant(directory, candidate))
            ? null
            : "does not contain all track directories";
    }

    internal static bool IsSameOrDescendant(string path, string candidate)
    {
        var pathParts = GetComparisonParts(path);
        var candidateParts = GetComparisonParts(candidate);
        if (pathParts is null ||
            candidateParts is null ||
            !PathComparer.Equals(pathParts.Value.Root, candidateParts.Value.Root) ||
            candidateParts.Value.Segments.Count > pathParts.Value.Segments.Count)
        {
            return false;
        }

        return candidateParts.Value.Segments
            .Select((segment, index) => PathComparer.Equals(segment, pathParts.Value.Segments[index]))
            .All(matches => matches);
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
        return AreEquivalent(path, other);
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

    private static string GetComparisonKey(string path)
    {
        var resolved = ResolveExistingLinks(path);
        var parts = GetPathParts(resolved);
        if (parts is null)
        {
            return FoldForComparison(resolved);
        }

        return string.Join(
            Path.DirectorySeparatorChar,
            new[] { FoldForComparison(parts.Value.Root) }
                .Concat(parts.Value.Segments.Select(FoldForComparison)));
    }

    private static (string Root, IReadOnlyList<string> Segments)? GetComparisonParts(string path)
    {
        var normalized = NormalizeDirectory(path);
        if (normalized is null)
        {
            return null;
        }

        var parts = GetPathParts(ResolveExistingLinks(normalized));
        return parts is null
            ? null
            : (
                FoldForComparison(parts.Value.Root),
                parts.Value.Segments.Select(FoldForComparison).ToList());
    }

    private static (string Root, IReadOnlyList<string> Segments)? GetPathParts(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        var relative = path[root.Length..];
        var segments = relative
            .Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        return (root, segments);
    }

    private static string ResolveExistingLinks(string path)
    {
        try
        {
            var current = path;
            for (var depth = 0; depth < 32; depth++)
            {
                var resolved = ResolveLinkPass(current);
                if (PathComparer.Equals(resolved, current))
                {
                    return resolved;
                }

                current = resolved;
            }

            return current;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            return path;
        }
    }

    private static string ResolveLinkPass(string path)
    {
        var parts = GetPathParts(path);
        if (parts is null)
        {
            return path;
        }

        var current = parts.Value.Root;
        foreach (var segment in parts.Value.Segments)
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current))
            {
                continue;
            }

            var target = new DirectoryInfo(current).ResolveLinkTarget(true);
            if (target is not null)
            {
                current = target.FullName;
            }
        }

        return NormalizeDirectory(current) ?? path;
    }

    private static string FoldForComparison(string value)
    {
        var folded = value.Normalize(NormalizationForm.FormC)
            .Select(character => character switch
            {
                '\u2018' or '\u2019' or '\u02BC' or '\uFF07' => '\'',
                '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or
                    '\u2015' or '\u2212' or '\uFE58' or '\uFE63' or '\uFF0D' => '-',
                _ => character
            })
            .ToArray();
        return new string(folded).TrimEnd(' ', '.');
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
