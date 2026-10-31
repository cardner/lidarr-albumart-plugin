using System.Net;
using System.Net.Http.Headers;
using NLog;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;
using SixLabors.ImageSharp;
using TagLib;
using IOFile = System.IO.File;

namespace NzbDrone.Core.Plugins.AlbumArt.Services;

public sealed record AlbumArtworkLookupContext(
    Album Album,
    AlbumRelease? Release,
    IReadOnlyList<string> TrackPaths);

public sealed class ResolvedArtwork : IDisposable
{
    public ResolvedArtwork(string path, ArtworkSourceKind sourceKind)
    {
        Path = path;
        Description = path;
        SourceKind = sourceKind;
    }

    public ResolvedArtwork(byte[] data, string description, ArtworkSourceKind sourceKind)
    {
        Data = data;
        Description = description;
        SourceKind = sourceKind;
    }

    public string? Path { get; }

    public byte[]? Data { get; }

    public string Description { get; }

    public ArtworkSourceKind SourceKind { get; }

    public void Dispose()
    {
    }
}

public interface IAlbumArtworkResolver
{
    ResolvedArtwork? Resolve(
        string directory,
        AlbumArtworkLookupContext context,
        CoverArtOptions options);
}

public sealed class AlbumArtworkResolver : IAlbumArtworkResolver
{
    private readonly ICoverArtFinder _finder;
    private readonly ILidarrArtworkProvider _lidarrArtwork;
    private readonly IEmbeddedArtworkProvider _embeddedArtwork;
    private readonly IArtworkDownloader _downloader;
    private readonly Logger _logger;

    public AlbumArtworkResolver(IMapCoversToLocal mediaCoverService, Logger logger)
        : this(
            new CoverArtFinder(),
            new LidarrArtworkProvider(mediaCoverService),
            new EmbeddedArtworkProvider(),
            new CoverArtArchiveDownloader(logger),
            logger)
    {
    }

    internal AlbumArtworkResolver(
        ICoverArtFinder finder,
        ILidarrArtworkProvider lidarrArtwork,
        IEmbeddedArtworkProvider embeddedArtwork,
        IArtworkDownloader downloader,
        Logger logger)
    {
        _finder = finder;
        _lidarrArtwork = lidarrArtwork;
        _embeddedArtwork = embeddedArtwork;
        _downloader = downloader;
        _logger = logger;
    }

    public ResolvedArtwork? Resolve(
        string directory,
        AlbumArtworkLookupContext context,
        CoverArtOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        foreach (var sidecar in _finder.FindAll(directory, ArtworkProfile.Album))
        {
            if (IsDecodable(sidecar))
            {
                return new ResolvedArtwork(sidecar, ArtworkSourceKind.Sidecar);
            }

            _logger.Warn("AlbumArt ignored invalid sidecar: path='{0}'", sidecar);
        }

        if (!options.CreateMissingCover)
        {
            return null;
        }

        if (options.UseLidarrArtwork)
        {
            try
            {
                var lidarrPath = _lidarrArtwork.Find(context.Album, !options.DryRun);
                if (IsDecodable(lidarrPath))
                {
                    return new ResolvedArtwork(lidarrPath!, ArtworkSourceKind.Lidarr);
                }

                if (options.DryRun &&
                    context.Album.Images.Any(image => image.CoverType == MediaCoverTypes.Cover))
                {
                    return new ResolvedArtwork(
                        [],
                        "Lidarr album artwork (dry run)",
                        ArtworkSourceKind.Lidarr);
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                UnauthorizedAccessException or
                HttpRequestException)
            {
                _logger.Warn(exception, "AlbumArt Lidarr artwork lookup failed; trying the next source.");
            }
        }

        if (options.ExtractEmbeddedArtwork)
        {
            var embeddedData = _embeddedArtwork.Extract(context.TrackPaths);
            if (IsDecodable(embeddedData))
            {
                return new ResolvedArtwork(
                    embeddedData!,
                    "embedded front cover",
                    ArtworkSourceKind.Embedded);
            }
        }

        if (options.UseCoverArtArchive)
        {
            foreach (var url in BuildCoverArtArchiveUrls(context))
            {
                var downloadedData = _downloader.Download(url);
                if (IsDecodable(downloadedData))
                {
                    return new ResolvedArtwork(downloadedData!, url, ArtworkSourceKind.CoverArtArchive);
                }
            }
        }

        _logger.Info(
            "AlbumArt lookup unresolved: albumId={0} album='{1}' directory='{2}'",
            context.Album.Id,
            context.Album.Title,
            directory);
        return null;
    }

    internal static IReadOnlyList<string> BuildCoverArtArchiveUrls(AlbumArtworkLookupContext context)
    {
        var identifiers = new List<(string Kind, string? Id)>
        {
            ("release", context.Release?.ForeignReleaseId),
            ("release-group", context.Album.ForeignAlbumId)
        };
        var urls = new List<string>();

        foreach (var (kind, id) in identifiers)
        {
            if (!Guid.TryParse(id, out var parsed))
            {
                continue;
            }

            var baseUrl = $"https://coverartarchive.org/{kind}/{parsed:D}/front";
            urls.Add($"{baseUrl}-1200");
            urls.Add(baseUrl);
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

internal interface ILidarrArtworkProvider
{
    string? Find(Album album, bool allowDownload);
}

internal sealed class LidarrArtworkProvider : ILidarrArtworkProvider
{
    private readonly IMapCoversToLocal _mediaCoverService;

    public LidarrArtworkProvider(IMapCoversToLocal mediaCoverService)
    {
        _mediaCoverService = mediaCoverService;
    }

    public string? Find(Album album, bool allowDownload)
    {
        var cover = album.Images.FirstOrDefault(image => image.CoverType == MediaCoverTypes.Cover);
        if (cover is null)
        {
            return null;
        }

        var path = GetPath(album, cover);
        if (IOFile.Exists(path) || !allowDownload)
        {
            return IOFile.Exists(path) ? path : null;
        }

        _mediaCoverService.EnsureAlbumCovers(album);
        return IOFile.Exists(path) ? path : null;
    }

    private string GetPath(Album album, NzbDrone.Core.MediaCover.MediaCover cover)
    {
        return _mediaCoverService.GetCoverPath(
            album.Id,
            MediaCoverEntity.Album,
            cover.CoverType,
            cover.Extension);
    }
}

internal interface IEmbeddedArtworkProvider
{
    byte[]? Extract(IReadOnlyList<string> trackPaths);
}

internal sealed class EmbeddedArtworkProvider : IEmbeddedArtworkProvider
{
    private const int MaxEmbeddedArtworkBytes = 32 * 1024 * 1024;

    public byte[]? Extract(IReadOnlyList<string> trackPaths)
    {
        foreach (var trackPath in trackPaths.Where(IOFile.Exists))
        {
            try
            {
                using var track = TagLib.File.Create(trackPath);
                var pictures = track.Tag.Pictures
                    .OrderByDescending(value => value.Type == PictureType.FrontCover)
                    .ToList();
                foreach (var picture in pictures)
                {
                    if (picture.Data is null ||
                        picture.Data.Count == 0 ||
                        picture.Data.Count > MaxEmbeddedArtworkBytes)
                    {
                        continue;
                    }

                    var data = picture.Data.Data;
                    if (IsDecodable(data))
                    {
                        return data;
                    }
                }
            }
            catch (Exception exception) when (
                exception is CorruptFileException or
                UnsupportedFormatException or
                IOException or
                UnauthorizedAccessException)
            {
                // Try the next imported track.
            }
        }

        return null;
    }

    private static bool IsDecodable(byte[] data)
    {
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

internal interface IArtworkDownloader
{
    byte[]? Download(string url);
}

internal sealed class CoverArtArchiveDownloader : IArtworkDownloader
{
    private const int MaxDownloadBytes = 32 * 1024 * 1024;
    private readonly HttpClient _client;
    private readonly Logger _logger;

    public CoverArtArchiveDownloader(Logger logger)
        : this(logger, new HttpClientHandler { AllowAutoRedirect = true })
    {
    }

    internal CoverArtArchiveDownloader(Logger logger, HttpMessageHandler handler)
    {
        _logger = logger;
        _client = CreateClient(handler);
    }

    public byte[]? Download(string url)
    {
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var response = _client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellation.Token)
                .GetAwaiter()
                .GetResult();
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.Warn(
                    "AlbumArt Cover Art Archive request failed: status={0} url='{1}'",
                    (int)response.StatusCode,
                    url);
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            {
                _logger.Warn("AlbumArt Cover Art Archive image exceeded size limit: url='{0}'", url);
                return null;
            }

            using var input = response.Content.ReadAsStream(cancellation.Token);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = input.ReadAsync(buffer.AsMemory(), cancellation.Token)
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaxDownloadBytes)
                {
                    throw new IOException("Downloaded artwork exceeded the size limit.");
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            OperationCanceledException or
            IOException)
        {
            _logger.Warn(exception, "AlbumArt Cover Art Archive lookup failed: url='{0}'", url);
            return null;
        }
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LidarrAlbumArtOptimizer", "1.0"));
        return client;
    }
}
