using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins.AlbumArt.Services;

internal static class ArtistIdentityResolver
{
    public static IReadOnlyList<string> GetNames(Artist? artist)
    {
        if (artist is null)
        {
            return [];
        }

        var names = new List<string?>
        {
            artist.Name,
            artist.SortName
        };

        try
        {
            var metadata = artist.Metadata?.Value;
            names.Add(metadata?.Name);
            names.AddRange(metadata?.Aliases ?? []);
        }
        catch (InvalidOperationException)
        {
            // Import messages do not always include loaded artist metadata.
        }

        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
