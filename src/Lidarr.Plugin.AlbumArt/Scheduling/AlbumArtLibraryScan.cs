using NLog;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins.AlbumArt.Connect;
using NzbDrone.Core.Plugins.AlbumArt.Services;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.Plugins.AlbumArt.Scheduling;

public sealed class AlbumArtLibraryScan : ScheduledTaskBase<AlbumArtSettings>, IExecute<AlbumArtScanCommand>
{
    private readonly IArtistService _artistService;
    private readonly IAlbumService _albumService;
    private readonly IMediaFileService _mediaFileService;
    private readonly ICoverArtProcessor _processor;
    private readonly IAlbumArtworkResolver _resolver;
    private readonly IArtistArtworkResolver _artistResolver;
    private readonly IMissingArtworkTagService _missingArtworkTags;
    private readonly Lazy<INotificationFactory> _notificationFactory;
    private readonly Lazy<IRootFolderService> _rootFolderService;
    private readonly Logger _logger;

    public AlbumArtLibraryScan(
        IArtistService artistService,
        IAlbumService albumService,
        IMediaFileService mediaFileService,
        IMapCoversToLocal mediaCoverService,
        Lazy<ITagService> tagService,
        Lazy<INotificationFactory> notificationFactory,
        Lazy<IRootFolderService> rootFolderService,
        Logger logger)
    {
        _artistService = artistService;
        _albumService = albumService;
        _mediaFileService = mediaFileService;
        _notificationFactory = notificationFactory;
        _rootFolderService = rootFolderService;
        _processor = new CoverArtProcessor();
        _resolver = new AlbumArtworkResolver(mediaCoverService, logger);
        _artistResolver = new ArtistArtworkResolver(mediaCoverService, logger);
        _missingArtworkTags = new MissingArtworkTagService(artistService, tagService);
        _logger = logger;
    }

    public override string Name => "Album Art Optimizer Scan";

    public override Type CommandType => typeof(AlbumArtScanCommand);

    public override int IntervalMinutes => Settings?.ScanIntervalMinutes ?? 1440;

    public override CommandPriority Priority => CommandPriority.Low;

    public void Execute(AlbumArtScanCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var artists = command.ArtistId is > 0
            ? [_artistService.GetArtist(command.ArtistId.Value)]
            : _artistService.GetAllArtists().Where(artist => artist.Monitored).ToList();
        var (options, optimizeArtistImages) = ResolveConfiguration(command.DryRun);
        var albumCounters = new ScanCounters();
        var artistCounters = new ScanCounters();
        var unresolvedAlbums = new List<string>();

        foreach (var artist in artists.Where(artist => artist is not null))
        {
            try
            {
                if (optimizeArtistImages)
                {
                    artistCounters.Record(ProcessArtist(artist, options));
                }

                var albums = _albumService.GetAlbumsByArtistMetadataId(artist.ArtistMetadataId)
                    .Where(value => value.Monitored)
                    .ToList();
                var trackFilesByAlbum = albums.ToDictionary(
                    album => album.Id,
                    album => _mediaFileService.GetFilesByAlbum(album.Id)
                        .Where(file => File.Exists(file.Path))
                        .ToList());
                var rootFolderPath = ResolveRootFolderPath(artist);
                var artistHasUnresolvedAlbums = false;
                foreach (var album in albums)
                {
                    var trackFiles = trackFilesByAlbum[album.Id];
                    if (trackFiles.Count == 0)
                    {
                        continue;
                    }

                    bool? artistDirectoryExclusive = rootFolderPath is null
                        ? null
                        : IsDirectoryExclusive(
                            album.Id,
                            artist.Path,
                            trackFilesByAlbum);
                    var directoryResolution = AlbumDirectoryResolver.Resolve(
                        trackFiles.Select(file => file.Path),
                        artist.Path,
                        rootFolderPath,
                        artistDirectoryExclusive);
                    var directories = directoryResolution.Directories;
                    if (!directoryResolution.HasSafeTarget)
                    {
                        albumCounters.RecordUnsafePath();
                        albumCounters.Record(new CoverArtResult(CoverArtOutcome.NoArtwork));
                        unresolvedAlbums.Add($"{artist.Name} — {album.Title} (unsafe path)");
                        _logger.Warn(
                            "AlbumArt scan rejected albumId={0} album='{1}': {2}",
                            album.Id,
                            album.Title,
                            directoryResolution.RejectionReason);
                        continue;
                    }

                    var unresolved = ProcessAlbum(
                        album,
                        artist.Name,
                        trackFiles.Select(file => file.Path).ToList(),
                        directories,
                        options,
                        albumCounters);
                    artistHasUnresolvedAlbums |= unresolved;
                    if (unresolved)
                    {
                        unresolvedAlbums.Add($"{artist.Name} — {album.Title}");
                    }

                    foreach (var directory in directories)
                    {
                        if (optimizeArtistImages)
                        {
                            artistCounters.Record(ProcessDirectory(directory, options, ArtworkProfile.Artist));
                        }
                    }
                }

                if (!options.DryRun)
                {
                    _missingArtworkTags.Reconcile(artist, artistHasUnresolvedAlbums);
                }
            }
            catch (Exception exception)
            {
                albumCounters.RecordFailure();
                _logger.Warn(exception, "AlbumArt scan failed for artistId={0} artist='{1}'; continuing.", artist.Id, artist.Name);
            }
        }

        var unresolvedSummary = string.Join(", ", unresolvedAlbums.Take(25));
        if (unresolvedAlbums.Count > 25)
        {
            unresolvedSummary += $", and {unresolvedAlbums.Count - 25} more";
        }

        command.ResultMessage =
            $"album folders: {albumCounters.Processed} scanned, {albumCounters.Optimized} optimized, {albumCounters.Failed} failed, " +
            $"{unresolvedAlbums.Count} unresolved ({albumCounters.SourceSummary}); " +
            $"unsafe paths: {albumCounters.UnsafePaths}; " +
            $"artist locations: {artistCounters.Processed} scanned, {artistCounters.Optimized} optimized, {artistCounters.Unresolved} unresolved, {artistCounters.Failed} failed ({artistCounters.SourceSummary})" +
            (unresolvedAlbums.Count > 0 ? $"; unresolved: {unresolvedSummary}" : string.Empty);
        _logger.Info("AlbumArt library scan complete: {0}", command.ResultMessage);
    }

    private string? ResolveRootFolderPath(Artist artist)
    {
        if (!string.IsNullOrWhiteSpace(artist.RootFolderPath))
        {
            return artist.RootFolderPath;
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
                "AlbumArt could not resolve the library root for artistId={0} path='{1}'.",
                artist.Id,
                artist.Path);
            return null;
        }
    }

    private static bool IsDirectoryExclusive(
        int albumId,
        string artistPath,
        IReadOnlyDictionary<int, List<TrackFile>> trackFilesByAlbum)
    {
        var currentDirectories = GetTrackDirectories(trackFilesByAlbum.GetValueOrDefault(albumId));
        if (currentDirectories.Count == 0)
        {
            return false;
        }

        var shared = trackFilesByAlbum
            .Where(entry => entry.Key != albumId)
            .SelectMany(entry => GetTrackDirectories(entry.Value))
            .Any(directory => currentDirectories.Any(current =>
                AlbumDirectoryResolver.AreEquivalent(directory, current)));
        return !shared;
    }

    private static IReadOnlyList<string> GetTrackDirectories(IEnumerable<TrackFile>? files)
    {
        return (files ?? [])
            .Select(file => Path.GetDirectoryName(file.Path))
            .OfType<string>()
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToList();
    }

    private bool ProcessAlbum(
        Album album,
        string artistName,
        IReadOnlyList<string> trackPaths,
        IReadOnlyList<string> directories,
        CoverArtOptions options,
        ScanCounters counters)
    {
        if (directories.Count == 0)
        {
            counters.Record(new CoverArtResult(CoverArtOutcome.NoArtwork));
            return true;
        }

        var release = album.AlbumReleases.Value.FirstOrDefault(value => value.Monitored) ??
                      album.AlbumReleases.Value.FirstOrDefault();
        var context = new AlbumArtworkLookupContext(album, release, trackPaths, artistName);
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
            counters.Record(result);
            failed |= result.Outcome == CoverArtOutcome.Failed;
        }

        if (foundSidecar)
        {
            return failed;
        }

        var targetDirectory = directories[0];
        using var fallback = _resolver.Resolve(targetDirectory, context, options);
        if (fallback is null)
        {
            counters.Record(new CoverArtResult(CoverArtOutcome.NoArtwork));
            return true;
        }

        CoverArtResult fallbackResult;
        if (options.DryRun && fallback.Path is null && fallback.Data is { Length: 0 })
        {
            fallbackResult = new CoverArtResult(
                CoverArtOutcome.WouldOptimize,
                SourcePath: fallback.Description,
                DestinationPath: Path.Combine(targetDirectory, ArtworkProfile.Album.NormalizedFileName),
                SourceKind: fallback.SourceKind);
        }
        else
        {
            fallbackResult = fallback.Path is not null
                ? _processor.ProcessSource(
                    fallback.Path,
                    targetDirectory,
                    options,
                    ArtworkProfile.Album,
                    fallback.SourceKind)
                : _processor.ProcessData(
                    fallback.Data ?? [],
                    fallback.Description,
                    targetDirectory,
                    options,
                    ArtworkProfile.Album,
                    fallback.SourceKind);
        }
        counters.Record(fallbackResult);
        return fallbackResult.Outcome == CoverArtOutcome.Failed;
    }

    private CoverArtResult ProcessDirectory(
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile)
    {
        try
        {
            return _processor.Process(directory, options, profile);
        }
        catch (Exception exception)
        {
            _logger.Warn(
                exception,
                "AlbumArt scan failed for profile='{0}' directory='{1}'; continuing.",
                profile.NormalizedFileName,
                directory);
            return new CoverArtResult(CoverArtOutcome.Failed, Error: exception.Message);
        }
    }

    private CoverArtResult ProcessArtist(
        Artist artist,
        CoverArtOptions options)
    {
        try
        {
            using var artwork = _artistResolver.Resolve(artist.Path, artist, options);
            if (artwork is null)
            {
                return new CoverArtResult(CoverArtOutcome.NoArtwork);
            }

            if (options.DryRun && artwork.Path is null && artwork.Data is { Length: 0 })
            {
                return new CoverArtResult(
                    CoverArtOutcome.WouldOptimize,
                    SourcePath: artwork.Description,
                    DestinationPath: Path.Combine(artist.Path, ArtworkProfile.Artist.NormalizedFileName),
                    SourceKind: artwork.SourceKind);
            }

            return artwork.Path is not null
                ? _processor.ProcessSource(
                    artwork.Path,
                    artist.Path,
                    options,
                    ArtworkProfile.Artist,
                    artwork.SourceKind)
                : _processor.ProcessData(
                    artwork.Data ?? [],
                    artwork.Description,
                    artist.Path,
                    options,
                    ArtworkProfile.Artist,
                    artwork.SourceKind);
        }
        catch (Exception exception)
        {
            _logger.Warn(
                exception,
                "AlbumArt artist scan failed: artistId={0} directory='{1}'; continuing.",
                artist.Id,
                artist.Path);
            return new CoverArtResult(CoverArtOutcome.Failed, Error: exception.Message);
        }
    }

    private (CoverArtOptions Options, bool OptimizeArtistImages) ResolveConfiguration(bool commandDryRun)
    {
        try
        {
            var notificationSettings = _notificationFactory.Value.All()
                .Select(definition => definition.Settings)
                .OfType<AlbumArtSettings>()
                .FirstOrDefault();

            if (notificationSettings is not null)
            {
                return (
                    notificationSettings.ToOptions() with { DryRun = commandDryRun || notificationSettings.DryRun },
                    notificationSettings.OptimizeArtistImages);
            }
        }
        catch (Exception exception)
        {
            _logger.Warn(exception, "AlbumArt scan could not resolve Connect settings; using Metadata settings.");
        }

        var settings = Settings ?? new AlbumArtSettings();
        return (
            settings.ToOptions() with { DryRun = commandDryRun || settings.DryRun },
            settings.OptimizeArtistImages);
    }

    private sealed class ScanCounters
    {
        public int Processed { get; private set; }

        public int Optimized { get; private set; }

        public int Failed { get; private set; }

        public int Unresolved { get; private set; }

        public int UnsafePaths { get; private set; }

        private Dictionary<ArtworkSourceKind, int> Sources { get; } = [];

        public string SourceSummary =>
            string.Join(
                ", ",
                Enum.GetValues<ArtworkSourceKind>()
                    .Select(source => $"{source.ToString().ToLowerInvariant()}: {Sources.GetValueOrDefault(source)}"));

        public void Record(CoverArtResult result)
        {
            Processed++;
            Optimized += result.Outcome is CoverArtOutcome.Optimized or CoverArtOutcome.WouldOptimize ? 1 : 0;
            Unresolved += result.Outcome == CoverArtOutcome.NoArtwork ? 1 : 0;
            Failed += result.Outcome == CoverArtOutcome.Failed ? 1 : 0;
            if (result.SourceKind is { } source)
            {
                Sources[source] = Sources.GetValueOrDefault(source) + 1;
            }
        }

        public void RecordFailure()
        {
            Failed++;
        }

        public void RecordUnsafePath()
        {
            UnsafePaths++;
        }
    }
}
