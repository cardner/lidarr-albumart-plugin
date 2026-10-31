using System.Collections.Concurrent;
using FluentValidation.Results;
using NLog;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins.AlbumArt.Services;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.Plugins.AlbumArt.Connect;

public sealed class AlbumArtOptimize : NotificationBase<AlbumArtSettings>
{
    private static readonly ConcurrentDictionary<string, int> FlatArtistDirectoryOwners =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly ICoverArtProcessor _processor;
    private readonly IAlbumArtworkResolver _resolver;
    private readonly IArtistArtworkResolver? _artistResolver;
    private readonly IMissingArtworkTagService _missingArtworkTags;
    private readonly Lazy<IAlbumService>? _albumService;
    private readonly Lazy<IMediaFileService>? _mediaFileService;
    private readonly Lazy<IRootFolderService>? _rootFolderService;
    private readonly Logger _logger;

    public AlbumArtOptimize(
        IMapCoversToLocal mediaCoverService,
        IArtistService artistService,
        Lazy<ITagService> tagService,
        Lazy<IAlbumService> albumService,
        Lazy<IMediaFileService> mediaFileService,
        Lazy<IRootFolderService> rootFolderService,
        Logger logger)
        : this(
            new CoverArtProcessor(),
            new AlbumArtworkResolver(mediaCoverService, logger),
            new ArtistArtworkResolver(mediaCoverService, logger),
            new MissingArtworkTagService(artistService, tagService),
            logger,
            albumService,
            mediaFileService,
            rootFolderService)
    {
    }

    internal AlbumArtOptimize(
        ICoverArtProcessor processor,
        IAlbumArtworkResolver resolver,
        IMissingArtworkTagService missingArtworkTags,
        Logger logger)
        : this(processor, resolver, null, missingArtworkTags, logger)
    {
    }

    internal AlbumArtOptimize(
        ICoverArtProcessor processor,
        IAlbumArtworkResolver resolver,
        IArtistArtworkResolver? artistResolver,
        IMissingArtworkTagService missingArtworkTags,
        Logger logger,
        Lazy<IAlbumService>? albumService = null,
        Lazy<IMediaFileService>? mediaFileService = null,
        Lazy<IRootFolderService>? rootFolderService = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _artistResolver = artistResolver;
        _missingArtworkTags = missingArtworkTags ?? throw new ArgumentNullException(nameof(missingArtworkTags));
        _albumService = albumService;
        _mediaFileService = mediaFileService;
        _rootFolderService = rootFolderService;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override string Name => AlbumArtPlugin.DisplayName;

    public override string Link => AlbumArtPlugin.RepositoryUrl;

    public override ValidationResult Test()
    {
        return new ValidationResult();
    }

    public override void OnReleaseImport(AlbumDownloadMessage message)
    {
        try
        {
            if (message?.TrackFiles is null || message.TrackFiles.Count == 0)
            {
                _logger.Info("AlbumArt import no-op: no imported track files were supplied.");
                return;
            }

            var settings = Definition?.Settings as AlbumArtSettings ?? new AlbumArtSettings();
            var options = settings.ToOptions();
            var rootFolderPath = ResolveRootFolderPath(message.Artist);
            var artistDirectoryExclusive = rootFolderPath is null
                ? null
                : IsArtistDirectoryExclusive(message);
            var directoryResolution = string.IsNullOrWhiteSpace(message.Artist?.Path)
                ? new AlbumDirectoryResolution([], "artist path unavailable on import")
                : AlbumDirectoryResolver.Resolve(
                    message.TrackFiles.Select(track => track.Path),
                    message.Artist.Path,
                    rootFolderPath,
                    artistDirectoryExclusive);
            var albumDirectories = directoryResolution.Directories;
            if (!directoryResolution.HasSafeTarget)
            {
                _logger.Warn(
                    "AlbumArt import skipped albumId={0}: {1}",
                    message.Album?.Id,
                    directoryResolution.RejectionReason);
            }

            if (directoryResolution.HasSafeTarget &&
                ProcessAlbumDirectories(message, albumDirectories, options) &&
                message.Artist is not null &&
                !options.DryRun)
            {
                _missingArtworkTags.MarkMissing(message.Artist);
            }

            if (settings.OptimizeArtistImages)
            {
                foreach (var directory in FindArtistDirectories(message.Artist?.Path, albumDirectories))
                {
                    if (message.Artist is not null &&
                        AlbumDirectoryResolver.AreEquivalent(directory, message.Artist.Path) &&
                        _artistResolver is not null)
                    {
                        ProcessArtistDirectory(directory, message.Artist, options);
                    }
                    else
                    {
                        ProcessDirectory("artist", directory, options, ArtworkProfile.Artist);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Warn(exception, "AlbumArt import handler failed; the album import itself is unaffected.");
        }
    }

    private string? ResolveRootFolderPath(Artist? artist)
    {
        if (artist is null || !string.IsNullOrWhiteSpace(artist.RootFolderPath))
        {
            return artist?.RootFolderPath;
        }

        if (_rootFolderService is null)
        {
            return null;
        }

        try
        {
            var resolved = _rootFolderService.Value.GetBestRootFolderPath(artist.Path);
            return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
        }
        catch (Exception exception)
        {
            _logger.Warn(
                exception,
                "AlbumArt could not resolve the library root for import artistId={0} path='{1}'.",
                artist.Id,
                artist.Path);
            return null;
        }
    }

    private bool? IsArtistDirectoryExclusive(AlbumDownloadMessage message)
    {
        if (message.Artist is null ||
            _albumService is null ||
            _mediaFileService is null)
        {
            return null;
        }

        try
        {
            var currentDirectories = message.TrackFiles
                .Select(file => Path.GetDirectoryName(file.Path))
                .OfType<string>()
                .Distinct()
                .ToList();
            if (currentDirectories.Count == 0)
            {
                return false;
            }

            var occupiedByAnotherAlbum = _albumService.Value
                .GetAlbumsByArtistMetadataId(message.Artist.ArtistMetadataId)
                .Where(album => album.Monitored && album.Id != message.Album.Id)
                .SelectMany(album => _mediaFileService.Value.GetFilesByAlbum(album.Id))
                .Where(file => File.Exists(file.Path))
                .Any(file => currentDirectories.Any(directory =>
                    AlbumDirectoryResolver.AreEquivalent(
                        Path.GetDirectoryName(file.Path),
                        directory)));
            if (occupiedByAnotherAlbum)
            {
                return false;
            }

            foreach (var directory in currentDirectories)
            {
                var key = AlbumDirectoryResolver.GetIdentityKey(directory);
                if (key is null)
                {
                    return false;
                }

                if (!FlatArtistDirectoryOwners.TryAdd(key, message.Album.Id) &&
                    !(FlatArtistDirectoryOwners.TryGetValue(key, out var owner) &&
                      owner == message.Album.Id))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception)
        {
            _logger.Warn(
                exception,
                "AlbumArt could not verify artist-directory ownership for albumId={0}; retaining the safe rejection.",
                message.Album.Id);
            return null;
        }
    }

    private bool ProcessAlbumDirectories(
        AlbumDownloadMessage message,
        IReadOnlyList<string> directories,
        CoverArtOptions options)
    {
        var context = new AlbumArtworkLookupContext(
            message.Album,
            message.Release,
            message.TrackFiles.Select(track => track.Path).ToList(),
            message.Artist?.Name);
        var foundSidecar = false;
        var failed = false;

        foreach (var directory in directories)
        {
            using var artwork = _resolver.Resolve(
                directory,
                context,
                options with { CreateMissingCover = false });
            if (artwork is null)
            {
                continue;
            }

            foundSidecar = true;
            var result = _processor.ProcessSidecar(
                artwork.Path!,
                directory,
                options,
                ArtworkProfile.Album);
            LogResult(
                "album",
                directory,
                result);
            failed |= result.Outcome == CoverArtOutcome.Failed;
        }

        if (foundSidecar)
        {
            return failed;
        }

        if (directories.Count == 0)
        {
            return true;
        }

        var targetDirectory = directories[0];
        using var fallback = _resolver.Resolve(targetDirectory, context, options);
        if (fallback is null)
        {
            LogResult("album", targetDirectory, new CoverArtResult(CoverArtOutcome.NoArtwork));
            return true;
        }

        var fallbackResult = ProcessResolvedArtwork(fallback, targetDirectory, options);
        LogResult(
            "album",
            targetDirectory,
            fallbackResult);
        return fallbackResult.Outcome == CoverArtOutcome.Failed;
    }

    private CoverArtResult ProcessResolvedArtwork(
        ResolvedArtwork artwork,
        string directory,
        CoverArtOptions options,
        ArtworkProfile? profile = null)
    {
        profile ??= ArtworkProfile.Album;
        if (options.DryRun && artwork.Path is null && artwork.Data is { Length: 0 })
        {
            return new CoverArtResult(
                CoverArtOutcome.WouldOptimize,
                SourcePath: artwork.Description,
                DestinationPath: Path.Combine(directory, profile.NormalizedFileName),
                SourceKind: artwork.SourceKind);
        }

        return artwork.Path is not null
            ? _processor.ProcessSource(
                artwork.Path,
                directory,
                options,
                profile,
                artwork.SourceKind)
            : _processor.ProcessData(
                artwork.Data ?? [],
                artwork.Description,
                directory,
                options,
                profile,
                artwork.SourceKind);
    }

    private void ProcessArtistDirectory(
        string directory,
        Artist artist,
        CoverArtOptions options)
    {
        try
        {
            using var artwork = _artistResolver!.Resolve(directory, artist, options);
            var result = artwork is null
                ? new CoverArtResult(CoverArtOutcome.NoArtwork)
                : ProcessResolvedArtwork(artwork, directory, options, ArtworkProfile.Artist);
            LogResult("artist", directory, result);
        }
        catch (Exception exception)
        {
            _logger.Warn(
                exception,
                "AlbumArt artist processing failed: artistId={0} directory='{1}'; continuing.",
                artist.Id,
                directory);
        }
    }

    internal static IReadOnlyList<string> FindArtistDirectories(
        string? artistPath,
        IEnumerable<string> albumDirectories)
    {
        return new[] { artistPath }
            .Concat(albumDirectories)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .OfType<string>()
            .DistinctBy(
                path => AlbumDirectoryResolver.GetIdentityKey(path) ?? path,
                PathComparer)
            .ToList();
    }

    private void ProcessDirectory(
        string artworkKind,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile)
    {
        try
        {
            LogResult(artworkKind, directory, _processor.Process(directory, options, profile));
        }
        catch (Exception exception)
        {
            _logger.Warn(
                exception,
                "AlbumArt {0} processing failed: directory='{1}'; continuing.",
                artworkKind,
                directory);
        }
    }

    private void LogResult(string artworkKind, string directory, CoverArtResult result)
    {
        if (result.Outcome == CoverArtOutcome.Failed)
        {
            _logger.Warn(
                "AlbumArt failed: kind={0} directory='{1}' source='{2}' error='{3}'",
                artworkKind,
                directory,
                result.SourcePath,
                result.Error);
            return;
        }

        _logger.Info(
            "AlbumArt result: kind={0} outcome={1} sourceKind={2} directory='{3}' source='{4}' destination='{5}' dimensions={6}x{7}->{8}x{9} bytes={10}->{11}",
            artworkKind,
            result.Outcome,
            result.SourceKind,
            directory,
            result.SourcePath,
            result.DestinationPath,
            result.OriginalWidth,
            result.OriginalHeight,
            result.OutputWidth,
            result.OutputHeight,
            result.OriginalBytes,
            result.OutputBytes);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
