namespace NzbDrone.Core.Plugins.AlbumArt.Services;

public sealed record CoverArtOptions(
    int MaxDimension = 1400,
    int JpegQuality = 75,
    bool NormalizeToJpeg = true,
    bool RemoveRedundantSidecars = true,
    bool ConvertToJpeg = true,
    bool OnlyShrink = true,
    bool DryRun = false);
