namespace NzbDrone.Core.Plugins.AlbumArt.Services;

public interface ICoverArtFinder
{
    string? Find(string directory, ArtworkProfile profile);
}

public sealed class CoverArtFinder : ICoverArtFinder
{
    private static readonly string[] PreferredExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    public string? Find(string directory, ArtworkProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        var candidates = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupported)
            .Select(path => new
            {
                Path = path,
                NameRank = Rank(profile.PreferredNames, Path.GetFileNameWithoutExtension(path)),
                ExtensionRank = Rank(PreferredExtensions, Path.GetExtension(path))
            })
            .Where(candidate => candidate.NameRank >= 0)
            .OrderBy(candidate => candidate.NameRank)
            .ThenBy(candidate => candidate.ExtensionRank)
            .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return candidates?.Path;
    }

    private static bool IsSupported(string path)
    {
        return Rank(PreferredExtensions, Path.GetExtension(path)) >= 0;
    }

    private static int Rank(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}
