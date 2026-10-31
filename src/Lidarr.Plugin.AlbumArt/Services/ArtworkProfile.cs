namespace NzbDrone.Core.Plugins.AlbumArt.Services;

public sealed record ArtworkProfile(IReadOnlyList<string> PreferredNames, string NormalizedFileName)
{
    public static ArtworkProfile Album { get; } =
        new(["cover", "folder", "front", "albumart", "album", "artwork", "front cover"], "cover.jpg");

    public static ArtworkProfile Artist { get; } =
        new(["artist"], "artist.jpg");
}
