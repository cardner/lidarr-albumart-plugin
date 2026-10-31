namespace NzbDrone.Core.Plugins.AlbumArt.Services;

public enum CoverArtOutcome
{
    NoArtwork,
    Skipped,
    WouldOptimize,
    Optimized,
    Failed
}

public enum ArtworkSourceKind
{
    Sidecar,
    Lidarr,
    Embedded,
    CoverArtArchive,
    Discogs,
    ArtistLidarr
}

public sealed record CoverArtResult(
    CoverArtOutcome Outcome,
    string? SourcePath = null,
    string? DestinationPath = null,
    int OriginalWidth = 0,
    int OriginalHeight = 0,
    int OutputWidth = 0,
    int OutputHeight = 0,
    long OriginalBytes = 0,
    long OutputBytes = 0,
    string? Error = null,
    ArtworkSourceKind? SourceKind = null);
