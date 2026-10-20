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
        var options = ResolveOptions(command.DryRun);
        var processed = 0;
        var optimized = 0;
        var failed = 0;

        foreach (var artist in artists.Where(artist => artist is not null))
        {
            try
            {
                var albums = _albumService.GetAlbumsByArtistMetadataId(artist.ArtistMetadataId);
                foreach (var album in albums)
                {
                    var trackFiles = _mediaFileService.GetFilesByAlbum(album.Id);
                    foreach (var directory in AlbumArtOptimize.FindAlbumDirectories(trackFiles.Select(file => file.Path)))
                    {
                        var result = _processor.Process(directory, options);
                        processed++;
                        optimized += result.Outcome is CoverArtOutcome.Optimized or CoverArtOutcome.WouldOptimize ? 1 : 0;
                        failed += result.Outcome == CoverArtOutcome.Failed ? 1 : 0;
                    }
                }
            }
            catch (Exception exception)
            {
                failed++;
                _logger.Warn(exception, "AlbumArt scan failed for artistId={0} artist='{1}'; continuing.", artist.Id, artist.Name);
            }
        }

        command.ResultMessage = $"{processed} album folders scanned, {optimized} optimized, {failed} failed";
        _logger.Info("AlbumArt library scan complete: {0}", command.ResultMessage);
    }

    private CoverArtOptions ResolveOptions(bool commandDryRun)
    {
        try
        {
            var notificationSettings = _notificationFactory.Value.All()
                .Select(definition => definition.Settings)
                .OfType<AlbumArtSettings>()
                .FirstOrDefault();

            if (notificationSettings is not null)
            {
                return notificationSettings.ToOptions() with { DryRun = commandDryRun || notificationSettings.DryRun };
            }
        }
        catch (Exception exception)
        {
            _logger.Warn(exception, "AlbumArt scan could not resolve Connect settings; using Metadata settings.");
        }

        var settings = Settings ?? new AlbumArtSettings();
        return settings.ToOptions() with { DryRun = commandDryRun || settings.DryRun };
    }
}
