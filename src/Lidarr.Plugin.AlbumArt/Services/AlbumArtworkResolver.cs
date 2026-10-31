using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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
    IReadOnlyList<string> TrackPaths,
    string? ArtistName = null)
{
    public ArtworkLookupDiagnostics Diagnostics { get; } = new();
}

public sealed record ArtworkLookupAttempt(
    string Source,
    string Outcome,
    string Detail);

public sealed class ArtworkLookupDiagnostics
{
    private readonly List<ArtworkLookupAttempt> _attempts = [];

    public IReadOnlyList<ArtworkLookupAttempt> Attempts => _attempts;

    public string Summary => string.Join(
        "; ",
        _attempts.Select(value => $"{value.Source}:{value.Outcome}({value.Detail})"));

    public void Record(string source, string outcome, string detail)
    {
        _attempts.Add(new ArtworkLookupAttempt(source, outcome, detail));
    }
}

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
    private readonly IDiscogsArtworkProvider _discogsArtwork;
    private readonly Logger _logger;

    public AlbumArtworkResolver(IMapCoversToLocal mediaCoverService, Logger logger)
        : this(
            new CoverArtFinder(),
            new LidarrArtworkProvider(mediaCoverService),
            new EmbeddedArtworkProvider(),
            new CoverArtArchiveDownloader(logger),
            new DiscogsArtworkProvider(logger),
            logger)
    {
    }

    internal AlbumArtworkResolver(
        ICoverArtFinder finder,
        ILidarrArtworkProvider lidarrArtwork,
        IEmbeddedArtworkProvider embeddedArtwork,
        IArtworkDownloader downloader,
        Logger logger)
        : this(
            finder,
            lidarrArtwork,
            embeddedArtwork,
            downloader,
            new NullDiscogsArtworkProvider(),
            logger)
    {
    }

    internal AlbumArtworkResolver(
        ICoverArtFinder finder,
        ILidarrArtworkProvider lidarrArtwork,
        IEmbeddedArtworkProvider embeddedArtwork,
        IArtworkDownloader downloader,
        IDiscogsArtworkProvider discogsArtwork,
        Logger logger)
    {
        _finder = finder;
        _lidarrArtwork = lidarrArtwork;
        _embeddedArtwork = embeddedArtwork;
        _downloader = downloader;
        _discogsArtwork = discogsArtwork;
        _logger = logger;
    }

    public ResolvedArtwork? Resolve(
        string directory,
        AlbumArtworkLookupContext context,
        CoverArtOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var sidecars = _finder.FindAll(directory, ArtworkProfile.Album);
        context.Diagnostics.Record("sidecar", "checked", $"{sidecars.Count} candidate(s)");
        foreach (var sidecar in sidecars)
        {
            if (IsDecodable(sidecar))
            {
                context.Diagnostics.Record("sidecar", "matched", Path.GetFileName(sidecar));
                return new ResolvedArtwork(sidecar, ArtworkSourceKind.Sidecar);
            }

            context.Diagnostics.Record("sidecar", "invalid", Path.GetFileName(sidecar));
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
                    context.Diagnostics.Record("lidarr", "matched", "cached cover");
                    return new ResolvedArtwork(lidarrPath!, ArtworkSourceKind.Lidarr);
                }

                if (options.DryRun &&
                    context.Album.Images.Any(image => image.CoverType == MediaCoverTypes.Cover))
                {
                    context.Diagnostics.Record("lidarr", "matched", "album cover metadata (dry run)");
                    return new ResolvedArtwork(
                        [],
                        "Lidarr album artwork (dry run)",
                        ArtworkSourceKind.Lidarr);
                }

                var remoteUrl = GetLidarrRemoteCoverUrl(context.Album);
                if (!options.DryRun && remoteUrl is not null)
                {
                    var remoteData = _downloader.Download(remoteUrl);
                    if (IsDecodable(remoteData))
                    {
                        context.Diagnostics.Record("lidarr", "matched", "remote cover");
                        return new ResolvedArtwork(
                            remoteData!,
                            "Lidarr remote album artwork",
                            ArtworkSourceKind.Lidarr);
                    }
                }

                context.Diagnostics.Record(
                    "lidarr",
                    "miss",
                    remoteUrl is null ? "no usable cached or remote cover" : "remote cover invalid");
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
                context.Diagnostics.Record("embedded", "matched", $"{context.TrackPaths.Count} track(s) checked");
                return new ResolvedArtwork(
                    embeddedData!,
                    "embedded front cover",
                    ArtworkSourceKind.Embedded);
            }

            context.Diagnostics.Record("embedded", "miss", $"{context.TrackPaths.Count} track(s) checked");
        }

        if (options.UseCoverArtArchive)
        {
            foreach (var identifier in BuildCoverArtArchiveIdentifiers(context))
            {
                context.Diagnostics.Record(
                    "coverartarchive",
                    "checked",
                    $"{identifier.Kind} {identifier.Id:D}");
                var indexUrl = $"https://coverartarchive.org/{identifier.Kind}/{identifier.Id:D}";
                var indexJson = _downloader.DownloadText(indexUrl);
                foreach (var imageUrl in ParseCoverArtArchiveImageUrls(indexJson))
                {
                    var indexedData = _downloader.Download(imageUrl);
                    if (IsDecodable(indexedData))
                    {
                        context.Diagnostics.Record(
                            "coverartarchive",
                            "matched",
                            $"{identifier.Kind} {identifier.Id:D} indexed front");
                        return new ResolvedArtwork(
                            indexedData!,
                            $"Cover Art Archive {identifier.Kind} {identifier.Id:D}",
                            ArtworkSourceKind.CoverArtArchive);
                    }
                }

                foreach (var url in BuildDirectCoverArtArchiveUrls(identifier))
                {
                    var downloadedData = _downloader.Download(url);
                    if (IsDecodable(downloadedData))
                    {
                        context.Diagnostics.Record(
                            "coverartarchive",
                            "matched",
                            $"{identifier.Kind} {identifier.Id:D} direct front");
                        return new ResolvedArtwork(
                            downloadedData!,
                            $"Cover Art Archive {identifier.Kind} {identifier.Id:D}",
                            ArtworkSourceKind.CoverArtArchive);
                    }
                }
            }

            context.Diagnostics.Record("coverartarchive", "miss", "all known MusicBrainz IDs");
        }

        if (options.UseDiscogs)
        {
            if (string.IsNullOrWhiteSpace(options.DiscogsToken))
            {
                context.Diagnostics.Record("discogs", "disabled", "personal access token missing");
            }

            var discogs = _discogsArtwork.Find(context, options);
            if (discogs is not null && IsDecodable(discogs.Data))
            {
                context.Diagnostics.Record(
                    "discogs",
                    "matched",
                    $"release {discogs.ReleaseId}, score {discogs.Score}");
                return new ResolvedArtwork(
                    discogs.Data,
                    $"Discogs release {discogs.ReleaseId}",
                    ArtworkSourceKind.Discogs);
            }

            if (!string.IsNullOrWhiteSpace(options.DiscogsToken))
            {
                context.Diagnostics.Record("discogs", "miss", "no accepted release image");
            }
        }

        _logger.Info(
            "AlbumArt lookup unresolved: albumId={0} album='{1}' directory='{2}'",
            context.Album.Id,
            context.Album.Title,
            directory);
        _logger.Debug(
            "AlbumArt lookup details: albumId={0} attempts='{1}'",
            context.Album.Id,
            context.Diagnostics.Summary);
        return null;
    }

    internal static IReadOnlyList<string> BuildCoverArtArchiveUrls(AlbumArtworkLookupContext context)
    {
        return BuildCoverArtArchiveIdentifiers(context)
            .SelectMany(BuildDirectCoverArtArchiveUrls)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static IReadOnlyList<(string Kind, Guid Id)> BuildCoverArtArchiveIdentifiers(
        AlbumArtworkLookupContext context)
    {
        var releases = GetOrderedReleases(context);
        var identifiers = new List<(string Kind, Guid Id)>();
        foreach (var release in releases)
        {
            if (Guid.TryParse(release.ForeignReleaseId, out var releaseId))
            {
                identifiers.Add(("release", releaseId));
            }
        }

        if (Guid.TryParse(context.Album.ForeignAlbumId, out var albumId))
        {
            identifiers.Add(("release-group", albumId));
        }

        return identifiers
            .Distinct()
            .ToList();
    }

    internal static IReadOnlyList<AlbumRelease> GetOrderedReleases(
        AlbumArtworkLookupContext context)
    {
        var releases = new List<AlbumRelease>();
        if (context.Release is not null)
        {
            releases.Add(context.Release);
        }

        try
        {
            var knownReleases = context.Album.AlbumReleases?.Value ?? [];
            releases.AddRange(knownReleases.Where(value => value.Monitored));
            releases.AddRange(knownReleases.Where(value => !value.Monitored));
        }
        catch (InvalidOperationException)
        {
            // Lazy release metadata is not always populated during import.
        }

        return releases
            .GroupBy(release => release.ForeignReleaseId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    internal static IReadOnlyList<string> ParseCoverArtArchiveImageUrls(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("images", out var images) ||
                images.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return images.EnumerateArray()
                .Where(IsFrontImage)
                .OrderByDescending(image =>
                    image.TryGetProperty("approved", out var approved) &&
                    approved.ValueKind == JsonValueKind.True)
                .ThenByDescending(image =>
                    image.TryGetProperty("front", out var front) &&
                    front.ValueKind == JsonValueKind.True)
                .SelectMany(GetCoverArtArchiveImageUrls)
                .Where(url => IsSafeRemoteUrl(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> BuildDirectCoverArtArchiveUrls(
        (string Kind, Guid Id) identifier)
    {
        var baseUrl = $"https://coverartarchive.org/{identifier.Kind}/{identifier.Id:D}/front";
        return [$"{baseUrl}-1200", baseUrl];
    }

    private static bool IsFrontImage(JsonElement image)
    {
        if (image.TryGetProperty("front", out var front) && front.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        return image.TryGetProperty("types", out var types) &&
               types.ValueKind == JsonValueKind.Array &&
               types.EnumerateArray().Any(value =>
                   string.Equals(value.GetString(), "Front", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> GetCoverArtArchiveImageUrls(JsonElement image)
    {
        if (image.TryGetProperty("thumbnails", out var thumbnails) &&
            thumbnails.ValueKind == JsonValueKind.Object &&
            thumbnails.TryGetProperty("1200", out var large) &&
            large.ValueKind == JsonValueKind.String)
        {
            yield return large.GetString()!;
        }

        if (image.TryGetProperty("image", out var original) &&
            original.ValueKind == JsonValueKind.String)
        {
            yield return original.GetString()!;
        }
    }

    private static string? GetLidarrRemoteCoverUrl(Album album)
    {
        var cover = album.Images
            .FirstOrDefault(image => image.CoverType == MediaCoverTypes.Cover);
        return new[] { cover?.RemoteUrl, cover?.Url }
            .FirstOrDefault(IsSafeRemoteUrl);
    }

    private static bool IsSafeRemoteUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               !uri.IsLoopback;
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

    string? DownloadText(string url)
    {
        return null;
    }
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
        return DownloadBytes(url, "image", MaxDownloadBytes);
    }

    public string? DownloadText(string url)
    {
        var data = DownloadBytes(url, "metadata", 2 * 1024 * 1024);
        return data is null ? null : System.Text.Encoding.UTF8.GetString(data);
    }

    private byte[]? DownloadBytes(string url, string resourceKind, int maxBytes)
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
                    "AlbumArt remote request failed: status={0} host='{1}' kind={2}",
                    (int)response.StatusCode,
                    new Uri(url).Host,
                    resourceKind);
                return null;
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                _logger.Warn(
                    "AlbumArt remote {0} exceeded size limit: host='{1}'",
                    resourceKind,
                    new Uri(url).Host);
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

                if (output.Length + read > maxBytes)
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
            _logger.Warn(
                exception,
                "AlbumArt remote lookup failed: host='{0}' kind={1}",
                Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid)",
                resourceKind);
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
