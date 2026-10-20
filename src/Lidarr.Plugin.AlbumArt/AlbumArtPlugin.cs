using System.Runtime.CompilerServices;
using NzbDrone.Core.Plugins;

[assembly: InternalsVisibleTo("Lidarr.Plugin.AlbumArt.Tests")]

namespace NzbDrone.Core.Plugins.AlbumArt;

public sealed class AlbumArtPlugin : Plugin
{
    public const string DisplayName = "Album Art Optimizer";
    public const string RepositoryOwner = "cardner";
    public const string RepositoryUrl = "https://github.com/cardner/lidarr-albumart-plugin";

    public override string Name => DisplayName;

    public override string Owner => RepositoryOwner;

    public override string GithubUrl => RepositoryUrl;
}
