using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Plugins.AlbumArt.Scheduling;

public interface IProvideScheduledTask
{
    Type CommandType { get; }

    int IntervalMinutes { get; }

    CommandPriority Priority { get; }
}
