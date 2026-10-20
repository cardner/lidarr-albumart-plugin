using NLog;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Plugins.AlbumArt.Scheduling;

public sealed class PluginScheduledTaskStarter : IHandle<ApplicationStartedEvent>
{
    private readonly PluginScheduledTaskService _service;
    private readonly Logger _logger;

    public PluginScheduledTaskStarter(PluginScheduledTaskService service, Logger logger)
    {
        _service = service;
        _logger = logger;
    }

    public void Handle(ApplicationStartedEvent message)
    {
        try
        {
            _service.Synchronize();
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Could not initialize AlbumArt scheduled task.");
        }
    }
}
