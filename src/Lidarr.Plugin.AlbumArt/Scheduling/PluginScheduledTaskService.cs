using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.ThingiProvider.Events;

namespace NzbDrone.Core.Plugins.AlbumArt.Scheduling;

public sealed class PluginScheduledTaskService :
    IHandle<ProviderAddedEvent<IMetadata>>,
    IHandle<ProviderUpdatedEvent<IMetadata>>,
    IHandle<ProviderDeletedEvent<IMetadata>>
{
    private readonly IMetadataFactory _metadataFactory;
    private readonly IScheduledTaskRepository _repository;
    private readonly ICached<ScheduledTask> _cache;
    private readonly Logger _logger;
    private readonly HashSet<string> _registeredTypes = [];

    public PluginScheduledTaskService(
        IMetadataFactory metadataFactory,
        IScheduledTaskRepository repository,
        ICacheManager cacheManager,
        Logger logger)
    {
        _metadataFactory = metadataFactory;
        _repository = repository;
        _cache = cacheManager.GetCache<ScheduledTask>(typeof(TaskManager));
        _logger = logger;
    }

    public void Handle(ProviderAddedEvent<IMetadata> message)
    {
        RegisterDefinition(message.Definition);
    }

    public void Handle(ProviderUpdatedEvent<IMetadata> message)
    {
        RegisterDefinition(message.Definition);
    }

    public void Handle(ProviderDeletedEvent<IMetadata> message)
    {
        Synchronize();
    }

    public void Synchronize()
    {
        var providers = _metadataFactory.GetAvailableProviders()
            .OfType<IProvideScheduledTask>()
            .ToList();

        foreach (var provider in providers)
        {
            Register(provider);
        }

        var activeTypes = providers
            .Select(provider => provider.CommandType.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var typeName in _registeredTypes.Where(type => !activeTypes.Contains(type)).ToList())
        {
            var task = Find(typeName);
            if (task is not null)
            {
                _repository.Delete(task.Id);
                _cache.Remove(typeName);
            }

            _registeredTypes.Remove(typeName);
        }
    }

    private void RegisterDefinition(ProviderDefinition definition)
    {
        if (definition is not MetadataDefinition metadataDefinition)
        {
            return;
        }

        var provider = _metadataFactory.GetInstance(metadataDefinition);
        if (provider is IProvideScheduledTask scheduledTask)
        {
            Register(scheduledTask);
        }
    }

    private void Register(IProvideScheduledTask provider)
    {
        var typeName = provider.CommandType.FullName!;
        var task = Find(typeName);
        if (task is null)
        {
            var due = DateTime.UtcNow.AddMinutes(-provider.IntervalMinutes - 1);
            task = new ScheduledTask
            {
                TypeName = typeName,
                Interval = provider.IntervalMinutes,
                Priority = provider.Priority,
                LastExecution = due,
                LastStartTime = due
            };
            _repository.Insert(task);
            _logger.Info("Registered AlbumArt scheduled task: {0}", typeName);
        }
        else
        {
            task.Interval = provider.IntervalMinutes;
            task.Priority = provider.Priority;
            _repository.Update(task);
        }

        _cache.Set(typeName, task);
        _registeredTypes.Add(typeName);
    }

    private ScheduledTask? Find(string typeName)
    {
        return _repository.All().FirstOrDefault(task => task.TypeName == typeName);
    }
}
