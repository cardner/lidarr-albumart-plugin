using NLog;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins.AlbumArt.Connect;
using NzbDrone.Core.Plugins.AlbumArt.Services;

namespace NzbDrone.Core.Plugins.AlbumArt.Scheduling;

public sealed class AlbumArtLibraryScan : ScheduledTaskBase<AlbumArtSettings>, IExecute<AlbumArtScanCommand>
{
    private readonly IArtistService _artistService;
    private readonly IAlbumService _albumService;
    private readonly IMediaFileService _mediaFileService;
    private readonly ICoverArtProcessor _processor;
    private readonly Lazy<INotificationFactory> _notificationFactory;
    private readonly Logger _logger;

    public AlbumArtLibraryScan(
        IArtistService artistService,
        IAlbumService albumService,
        IMediaFileService mediaFileService,
        Lazy<INotificationFactory> notificationFactory,
        Logger logger)
    {
        _artistService = artistService;
        _albumService = albumService;
        _mediaFileService = mediaFileService;
        _notificationFactory = notificationFactory;
        _processor = new CoverArtProcessor();
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

        foreach (var artist in artists.Where(artist => artist is not null))
        {
            try
            {
                if (optimizeArtistImages)
                {
                    artistCounters.Record(ProcessDirectory(artist.Path, options, ArtworkProfile.Artist));
                }

                var albums = _albumService.GetAlbumsByArtistMetadataId(artist.ArtistMetadataId);
                foreach (var album in albums)
                {
                    var trackFiles = _mediaFileService.GetFilesByAlbum(album.Id);
                    foreach (var directory in AlbumArtOptimize.FindAlbumDirectories(trackFiles.Select(file => file.Path)))
                    {
                        albumCounters.Record(ProcessDirectory(directory, options, ArtworkProfile.Album));
                        if (optimizeArtistImages)
                        {
                            artistCounters.Record(ProcessDirectory(directory, options, ArtworkProfile.Artist));
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                albumCounters.RecordFailure();
                _logger.Warn(exception, "AlbumArt scan failed for artistId={0} artist='{1}'; continuing.", artist.Id, artist.Name);
            }
        }

        command.ResultMessage =
            $"album folders: {albumCounters.Processed} scanned, {albumCounters.Optimized} optimized, {albumCounters.Failed} failed; " +
            $"artist locations: {artistCounters.Processed} scanned, {artistCounters.Optimized} optimized, {artistCounters.Failed} failed";
        _logger.Info("AlbumArt library scan complete: {0}", command.ResultMessage);
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

        public void Record(CoverArtResult result)
        {
            Processed++;
            Optimized += result.Outcome is CoverArtOutcome.Optimized or CoverArtOutcome.WouldOptimize ? 1 : 0;
            Failed += result.Outcome == CoverArtOutcome.Failed ? 1 : 0;
        }

        public void RecordFailure()
        {
            Failed++;
        }
    }
}
