namespace NzbDrone.Core.Plugins.AlbumArt.Services;

internal static class ArtistPathMappingResolver
{
    public static string? Resolve(
        IReadOnlyList<string> artistNames,
        string? rootFolderPath,
        string? mappings)
    {
        if (artistNames.Count == 0 ||
            string.IsNullOrWhiteSpace(rootFolderPath) ||
            string.IsNullOrWhiteSpace(mappings))
        {
            return null;
        }

        var mapping = Parse(mappings)
            .LastOrDefault(candidate => artistNames.Any(name =>
                AlbumDirectoryResolver.AreEquivalentArtistNames(candidate.ArtistName, name)));
        if (mapping == default)
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(rootFolderPath);
            var mappedPath = Path.GetFullPath(
                Path.IsPathRooted(mapping.Destination)
                    ? mapping.Destination
                    : Path.Combine(root, mapping.Destination));

            return !AlbumDirectoryResolver.AreEquivalent(mappedPath, root) &&
                   AlbumDirectoryResolver.IsSameOrDescendant(mappedPath, root)
                ? mappedPath
                : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            return null;
        }
    }

    internal static IReadOnlyList<(string ArtistName, string Destination)> Parse(string? mappings)
    {
        if (string.IsNullOrWhiteSpace(mappings))
        {
            return [];
        }

        return mappings
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var separator = line.IndexOf('=');
                return separator <= 0 || separator == line.Length - 1
                    ? default
                    : (
                        ArtistName: line[..separator].Trim(),
                        Destination: line[(separator + 1)..].Trim());
            })
            .Where(mapping =>
                !string.IsNullOrWhiteSpace(mapping.ArtistName) &&
                !string.IsNullOrWhiteSpace(mapping.Destination))
            .ToList();
    }
}
