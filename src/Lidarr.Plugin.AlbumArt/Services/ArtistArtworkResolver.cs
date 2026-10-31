using NLog;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;
using SixLabors.ImageSharp;
using IOFile = System.IO.File;

namespace NzbDrone.Core.Plugins.AlbumArt.Services;

public interface IArtistArtworkResolver
{
    ResolvedArtwork? Resolve(
        string directory,
        Artist artist,
        CoverArtOptions options);
}

internal sealed class ArtistArtworkResolver : IArtistArtworkResolver
{
    private readonly ICoverArtFinder _finder;
    private readonly IMapCoversToLocal _mediaCoverService;
    private readonly IArtworkDownloader _downloader;
    private readonly Logger _logger;

    public ArtistArtworkResolver(
        IMapCoversToLocal mediaCoverService,
        Logger logger)
        : this(
            new CoverArtFinder(),
            mediaCoverService,
            new CoverArtArchiveDownloader(logger),
            logger)
    {
    }

    internal ArtistArtworkResolver(
        ICoverArtFinder finder,
        IMapCoversToLocal mediaCoverService,
        IArtworkDownloader downloader,
        Logger logger)
    {
        _finder = finder;
        _mediaCoverService = mediaCoverService;
        _downloader = downloader;
        _logger = logger;
    }

    public ResolvedArtwork? Resolve(
        string directory,
        Artist artist,
        CoverArtOptions options)
    {
        foreach (var sidecar in _finder.FindAll(directory, ArtworkProfile.Artist))
        {
            if (IsDecodable(sidecar))
            {
                return new ResolvedArtwork(sidecar, ArtworkSourceKind.Sidecar);
            }
        }

        var images = GetArtistImages(artist);
        var cover = images
            .OrderBy(image => GetPreference(image.CoverType))
            .FirstOrDefault(image => GetPreference(image.CoverType) < int.MaxValue);
        if (cover is null)
        {
            return null;
        }

        var cachedPath = _mediaCoverService.GetCoverPath(
            artist.Id,
            MediaCoverEntity.Artist,
            cover.CoverType,
            cover.Extension);
        if (IsDecodable(cachedPath))
        {
            return new ResolvedArtwork(cachedPath, ArtworkSourceKind.ArtistLidarr);
        }

        if (options.DryRun)
        {
            return new ResolvedArtwork(
                [],
                "Lidarr artist artwork (dry run)",
                ArtworkSourceKind.ArtistLidarr);
        }

        var remoteUrl = GetSafeRemoteUrl(cover);
        if (remoteUrl is null)
        {
            return null;
        }

        try
        {
            var data = _downloader.Download(remoteUrl);
            return IsDecodable(data)
                ? new ResolvedArtwork(
                    data!,
                    "Lidarr remote artist artwork",
                    ArtworkSourceKind.ArtistLidarr)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or
            HttpRequestException or
            UnauthorizedAccessException)
        {
            _logger.Warn(
                exception,
                "AlbumArt Lidarr artist artwork lookup failed: artistId={0}",
                artist.Id);
            return null;
        }
    }

    private static IReadOnlyList<NzbDrone.Core.MediaCover.MediaCover> GetArtistImages(
        Artist artist)
    {
        try
        {
            return artist.Metadata?.Value?.Images?.ToList() ?? [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private static int GetPreference(MediaCoverTypes type)
    {
        return type switch
        {
            MediaCoverTypes.Poster => 0,
            MediaCoverTypes.Headshot => 1,
            MediaCoverTypes.Cover => 2,
            MediaCoverTypes.Fanart => 3,
            _ => int.MaxValue
        };
    }

    private static string? GetSafeRemoteUrl(
        NzbDrone.Core.MediaCover.MediaCover cover)
    {
        foreach (var value in new[] { cover.RemoteUrl, cover.Url })
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps &&
                !uri.IsLoopback)
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsDecodable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !IOFile.Exists(path))
        {
            return false;
        }

        try
        {
            var info = Image.Identify(path);
            return info.Width > 0 && info.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsDecodable(byte[]? data)
    {
        if (data is null || data.Length == 0)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(data, false);
            var info = Image.Identify(stream);
            return info.Width > 0 && info.Height > 0;
        }
        catch
        {
            return false;
        }
    }
}
