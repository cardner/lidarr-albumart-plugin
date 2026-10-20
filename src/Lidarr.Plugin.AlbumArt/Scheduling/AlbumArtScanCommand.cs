using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Plugins.AlbumArt.Scheduling;

public sealed class AlbumArtScanCommand : Command
{
    private string _resultMessage = string.Empty;

    public AlbumArtScanCommand()
    {
        SendUpdatesToClient = true;
    }

    public int? ArtistId { get; set; }

    public bool DryRun { get; set; }

    public string ResultMessage
    {
        get => _resultMessage;
        set => _resultMessage = value;
    }

    public override string CompletionMessage => _resultMessage;
}
