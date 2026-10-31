using NzbDrone.Core.Music;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.Plugins.AlbumArt.Services;

public interface IMissingArtworkTagService
{
    void MarkMissing(Artist artist);

    void Reconcile(Artist artist, bool hasUnresolvedAlbums);
}

public sealed class MissingArtworkTagService : IMissingArtworkTagService
{
    public const string TagLabel = "missing-album-artwork";

    private readonly IArtistService _artistService;
    private readonly ITagService _tagService;

    public MissingArtworkTagService(IArtistService artistService, ITagService tagService)
    {
        _artistService = artistService;
        _tagService = tagService;
    }

    public void MarkMissing(Artist artist)
    {
        ArgumentNullException.ThrowIfNull(artist);

        var tag = GetOrCreateTag();
        if (artist.Tags.Add(tag.Id))
        {
            _artistService.UpdateArtist(artist);
        }
    }

    public void Reconcile(Artist artist, bool hasUnresolvedAlbums)
    {
        ArgumentNullException.ThrowIfNull(artist);

        if (hasUnresolvedAlbums)
        {
            MarkMissing(artist);
            return;
        }

        var tag = _tagService.All()
            .FirstOrDefault(value => string.Equals(value.Label, TagLabel, StringComparison.OrdinalIgnoreCase));
        if (tag is not null && artist.Tags.Remove(tag.Id))
        {
            _artistService.UpdateArtist(artist);
        }
    }

    private Tag GetOrCreateTag()
    {
        return _tagService.All()
                   .FirstOrDefault(value => string.Equals(value.Label, TagLabel, StringComparison.OrdinalIgnoreCase)) ??
               _tagService.Add(new Tag { Label = TagLabel });
    }
}
