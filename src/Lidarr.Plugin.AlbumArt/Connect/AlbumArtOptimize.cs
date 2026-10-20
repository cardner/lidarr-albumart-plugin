using FluentValidation.Results;
using NLog;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins.AlbumArt.Services;

namespace NzbDrone.Core.Plugins.AlbumArt.Connect;

public sealed class AlbumArtOptimize : NotificationBase<AlbumArtSettings>
{
    private readonly ICoverArtProcessor _processor;
    private readonly Logger _logger;

    public AlbumArtOptimize(Logger logger)
        : this(new CoverArtProcessor(), logger)
    {
    }

    internal AlbumArtOptimize(ICoverArtProcessor processor, Logger logger)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
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

            var options = (Definition?.Settings as AlbumArtSettings ?? new AlbumArtSettings()).ToOptions();
            foreach (var directory in FindAlbumDirectories(message.TrackFiles.Select(track => track.Path)))
            {
                LogResult(directory, _processor.Process(directory, options));
            }
        }
        catch (Exception exception)
        {
            _logger.Warn(exception, "AlbumArt import handler failed; the album import itself is unaffected.");
        }
    }

    internal static IReadOnlyList<string> FindAlbumDirectories(IEnumerable<string> trackPaths)
    {
        var trackDirectories = trackPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .Distinct(PathComparer)
            .ToList();

        if (trackDirectories.Count == 0)
        {
            return [];
        }

        var candidates = new List<string>();
        var commonDirectory = FindCommonDirectory(trackDirectories);
        if (commonDirectory is not null)
        {
            candidates.Add(commonDirectory);
        }

        candidates.AddRange(trackDirectories);

        // A single imported disc commonly stores tracks in CD1/Disc 1 while cover.jpg
        // lives one level up in the album directory.
        if (trackDirectories.Count == 1 && LooksLikeDiscDirectory(trackDirectories[0]))
        {
            var parent = Directory.GetParent(trackDirectories[0])?.FullName;
            if (parent is not null)
            {
                candidates.Insert(0, parent);
            }
        }

        return candidates.Distinct(PathComparer).ToList();
    }

    private static string? FindCommonDirectory(IReadOnlyList<string> directories)
    {
        var candidate = new DirectoryInfo(directories[0]);
        while (candidate is not null)
        {
            if (directories.All(directory => IsSameOrDescendant(directory, candidate.FullName)))
            {
                return candidate.FullName;
            }

            candidate = candidate.Parent;
        }

        return null;
    }

    private static bool IsSameOrDescendant(string path, string candidate)
    {
        var relative = Path.GetRelativePath(candidate, path);
        return relative == "." ||
               (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                relative != ".." &&
                !Path.IsPathRooted(relative));
    }

    private static bool LooksLikeDiscDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return name.StartsWith("cd", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("disc", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("disk", StringComparison.OrdinalIgnoreCase);
    }

    private void LogResult(string directory, CoverArtResult result)
    {
        if (result.Outcome == CoverArtOutcome.Failed)
        {
            _logger.Warn("AlbumArt failed: directory='{0}' source='{1}' error='{2}'", directory, result.SourcePath, result.Error);
            return;
        }

        _logger.Info(
            "AlbumArt result: outcome={0} directory='{1}' source='{2}' destination='{3}' dimensions={4}x{5}->{6}x{7} bytes={8}->{9}",
            result.Outcome,
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
