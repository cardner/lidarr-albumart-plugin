using System.Net;
using System.Text;
using Moq;
using NLog;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Music;
using NzbDrone.Core.Plugins.AlbumArt.Services;
using SkiaSharp;
using Xunit;

namespace Lidarr.Plugin.AlbumArt.Tests;

public sealed class ArtworkLookupExpansionTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"album-art-expansion-{Guid.NewGuid():N}");
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public ArtworkLookupExpansionTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Directory_resolver_rejects_library_and_artist_roots()
    {
        var root = Path.Combine(_directory, "Music");
        var artist = Path.Combine(root, "Artist");

        var libraryRoot = AlbumDirectoryResolver.Resolve(
            [Path.Combine(root, "song.flac")],
            artist,
            root);
        var artistRoot = AlbumDirectoryResolver.Resolve(
            [Path.Combine(artist, "song.flac")],
            artist,
            root);

        Assert.False(libraryRoot.HasSafeTarget);
        Assert.False(artistRoot.HasSafeTarget);
    }

    [Fact]
    public void Directory_resolver_uses_validated_multi_disc_parent()
    {
        var root = Path.Combine(_directory, "Music");
        var artist = Path.Combine(root, "Artist");
        var album = Path.Combine(artist, "Album");

        var result = AlbumDirectoryResolver.Resolve(
            [
                Path.Combine(album, "CD1", "01.flac"),
                Path.Combine(album, "CD2", "02.flac")
            ],
            artist,
            root);

        Assert.True(result.HasSafeTarget);
        Assert.Equal(album, Assert.Single(result.Directories));
    }

    [Theory]
    [InlineData("Arm's Length", "Arm’s Length")]
    [InlineData("Anti-Flag", "Anti‐Flag")]
    [InlineData("Andrew W.K.", "Andrew W.K")]
    public void Directory_resolver_matches_equivalent_artist_path_spelling(
        string lidarrArtistName,
        string diskArtistName)
    {
        var root = Path.Combine(_directory, "Music");
        var artistPath = Path.Combine(root, lidarrArtistName);
        var albumPath = Path.Combine(root, diskArtistName, "Album");

        var result = AlbumDirectoryResolver.Resolve(
            [Path.Combine(albumPath, "01.flac")],
            artistPath,
            root);

        Assert.True(result.HasSafeTarget, result.RejectionReason);
        Assert.Equal(albumPath, Assert.Single(result.Directories));
    }

    [Fact]
    public void Directory_resolver_compares_symlinked_and_real_paths()
    {
        var realRoot = Path.Combine(_directory, "real-music");
        var albumPath = Path.Combine(realRoot, "Artist", "Album");
        var linkedRoot = Path.Combine(_directory, "linked-music");
        Directory.CreateDirectory(albumPath);
        Directory.CreateSymbolicLink(linkedRoot, realRoot);

        var result = AlbumDirectoryResolver.Resolve(
            [Path.Combine(albumPath, "01.flac")],
            Path.Combine(linkedRoot, "Artist"),
            linkedRoot);

        Assert.True(result.HasSafeTarget, result.RejectionReason);
        Assert.Equal(albumPath, Assert.Single(result.Directories));
    }

    [Fact]
    public void Directory_resolver_allows_unshared_artist_directory()
    {
        var root = Path.Combine(_directory, "Music");
        var artist = Path.Combine(root, "Single Album Artist");

        var result = AlbumDirectoryResolver.Resolve(
            [Path.Combine(artist, "01.flac")],
            artist,
            root,
            artistDirectoryExclusive: true);

        Assert.True(result.HasSafeTarget);
        Assert.Equal(artist, Assert.Single(result.Directories));
    }

    [Fact]
    public void Directory_resolver_rejects_shared_artist_directory_with_detail()
    {
        var root = Path.Combine(_directory, "Music");
        var artist = Path.Combine(root, "Cannonball Adderley");
        var trackPath = Path.Combine(artist, "01.flac");

        var result = AlbumDirectoryResolver.Resolve(
            [trackPath],
            artist,
            root,
            artistDirectoryExclusive: false);

        Assert.False(result.HasSafeTarget);
        Assert.Contains(artist, result.RejectionReason, StringComparison.Ordinal);
        Assert.Contains("shared by multiple albums", result.RejectionReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CAKE", "Cake", "Fashion Nugget")]
    [InlineData("Cage the Elephant", "Cage The Elephant", "Social Cues")]
    [InlineData("Built to Spill", "Built To Spill", "There Is No Enemy")]
    [InlineData("The Bouncing Souls", "Bouncing Souls", "Simplicity (2016)")]
    [InlineData("blink‐182", "Blink-182", "Dude Ranch")]
    [InlineData("Andrew W.K", "Andrew W.K_", "I Get Wet")]
    [InlineData("Bad Cop+Bad Cop", "Bad Cop-Bad Cop", "The Ride")]
    [InlineData("Bobby “Blue” Bland", "Bobby _Blue_ Bland", "First Class Blues")]
    public void Directory_resolver_matches_equivalent_artist_folders(
        string lidarrArtistName,
        string diskArtistName,
        string albumName)
    {
        var root = Path.Combine(_directory, "Music");
        var albumPath = Path.Combine(root, diskArtistName, albumName);

        var result = AlbumDirectoryResolver.Resolve(
            [Path.Combine(albumPath, "01.flac")],
            Path.Combine(root, lidarrArtistName),
            root);

        Assert.True(result.HasSafeTarget, result.RejectionReason);
        Assert.Equal(albumPath, Assert.Single(result.Directories));
    }

    [Theory]
    [InlineData("Cécile McLorin Salvant", "Cecile McLorin Salvant", "With Every Breath I Take")]
    [InlineData("Anberlin", "Anberlin & Matty Mullins", "Nevertake")]
    [InlineData("Ahmad Jamal", "Ahmad Jamal featuring Yusef Lateef", "Live at the Olympia - June 27, 2012")]
    [InlineData("Billy Bragg", "Billy Bragg & Wilco", "Mermaid Avenue")]
    [InlineData("Chris Thile", "Chris Thile & Brad Mehldau", "Chris Thile & Brad Mehldau [Disc 1]")]
    [InlineData("Born Without Bones", "Bone Without Bones", "Born Without Bones - Baby")]
    public void Directory_resolver_allows_exclusive_collaboration_folders(
        string lidarrArtistName,
        string diskArtistName,
        string albumName)
    {
        var root = Path.Combine(_directory, "Music");
        var albumPath = Path.Combine(root, diskArtistName, albumName);

        var result = AlbumDirectoryResolver.Resolve(
            [Path.Combine(albumPath, "01.flac")],
            Path.Combine(root, lidarrArtistName),
            root,
            artistDirectoryExclusive: true);

        Assert.True(result.HasSafeTarget, result.RejectionReason);
        Assert.Equal(albumPath, Assert.Single(result.Directories));
    }

    [Fact]
    public void Directory_resolver_allows_nested_collaboration_folder()
    {
        var root = Path.Combine(_directory, "Music");
        var albumPath = Path.Combine(
            root,
            "Cannonball Adderley",
            "The Cannonball Adderley Quintet",
            "Plus");

        var result = AlbumDirectoryResolver.Resolve(
            [Path.Combine(albumPath, "01.flac")],
            Path.Combine(root, "The Cannonball Adderley Quintet"),
            root,
            artistDirectoryExclusive: true);

        Assert.True(result.HasSafeTarget, result.RejectionReason);
        Assert.Equal(albumPath, Assert.Single(result.Directories));
    }

    [Fact]
    public void Directory_resolver_allows_collaboration_folder_when_other_albums_exist()
    {
        var root = Path.Combine(_directory, "Music");
        var albumPath = Path.Combine(root, "Cecile McLorin Salvant", "With Every Breath I Take");

        var result = AlbumDirectoryResolver.Resolve(
            [Path.Combine(albumPath, "01.flac")],
            Path.Combine(root, "Cécile McLorin Salvant"),
            root,
            artistDirectoryExclusive: false);

        Assert.True(result.HasSafeTarget, result.RejectionReason);
        Assert.Equal(albumPath, Assert.Single(result.Directories));
    }

    [Fact]
    public void Directory_resolver_rejects_unrelated_exclusive_folder()
    {
        var root = Path.Combine(_directory, "Music");
        var albumPath = Path.Combine(root, "Big D and the Kids Table", "Beijing to Boston");

        var result = AlbumDirectoryResolver.Resolve(
            [Path.Combine(albumPath, "01.flac")],
            Path.Combine(root, "Brain Failure"),
            root,
            artistDirectoryExclusive: true);

        Assert.False(result.HasSafeTarget);
        Assert.Contains("outside artist path", result.RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Directory_resolver_matches_loaded_artist_alias()
    {
        var root = Path.Combine(_directory, "Music");
        var albumPath = Path.Combine(root, "Hiromi", "Spark");

        var result = AlbumDirectoryResolver.Resolve(
            [Path.Combine(albumPath, "01.flac")],
            Path.Combine(root, "上原ひろみ"),
            root,
            artistDirectoryExclusive: false,
            artistNames: ["上原ひろみ", "Hiromi"]);

        Assert.True(result.HasSafeTarget, result.RejectionReason);
        Assert.Equal(albumPath, Assert.Single(result.Directories));
    }

    [Fact]
    public void Directory_resolver_reports_track_folder_before_rejected_common_root()
    {
        var root = Path.Combine(_directory, "Music");
        var firstAlbumPath = Path.Combine(root, "Cecile McLorin Salvant", "Album");
        var secondAlbumPath = Path.Combine(root, "Unrelated Artist", "Album");

        var result = AlbumDirectoryResolver.Resolve(
            [
                Path.Combine(firstAlbumPath, "01.flac"),
                Path.Combine(secondAlbumPath, "02.flac")
            ],
            Path.Combine(root, "Cécile McLorin Salvant"),
            root);

        Assert.False(result.HasSafeTarget);
        Assert.Contains($"candidate='{firstAlbumPath}'", result.RejectionReason, StringComparison.Ordinal);
        Assert.DoesNotContain($"candidate='{root}'", result.RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Artist_path_mapping_accepts_explicit_folder_but_not_unmapped_artist()
    {
        var root = Path.Combine(_directory, "Music");
        var albumPath = Path.Combine(root, "Chewing on Tinfoil", "Inarticulate");
        var names = new[] { "CHEWIE" };

        var withoutMapping = AlbumDirectoryResolver.Resolve(
            [Path.Combine(albumPath, "01.flac")],
            Path.Combine(root, "CHEWIE"),
            root,
            artistDirectoryExclusive: true,
            artistNames: names);
        var mappedPath = ArtistPathMappingResolver.Resolve(
            names,
            root,
            $"CHEWIE={Path.Combine(root, "Chewing on Tinfoil")}");
        var withMapping = AlbumDirectoryResolver.Resolve(
            [Path.Combine(albumPath, "01.flac")],
            mappedPath,
            root,
            artistDirectoryExclusive: true,
            artistNames: names);

        Assert.False(withoutMapping.HasSafeTarget);
        Assert.Equal(Path.Combine(root, "Chewing on Tinfoil"), mappedPath);
        Assert.True(withMapping.HasSafeTarget, withMapping.RejectionReason);
        Assert.Equal(albumPath, Assert.Single(withMapping.Directories));
    }

    [Fact]
    public void Artist_path_mapping_preserves_commas_and_rejects_unsafe_destinations()
    {
        var root = Path.Combine(_directory, "Music");
        const string longFolder =
            "Waylon Jennings, Willie Nelson, Kris Kristofferson & Johnny Cash";
        var mappings = string.Join(
            Environment.NewLine,
            $"The Highwaymen={longFolder}",
            $"Root={root}",
            $"Escape={Path.Combine(_directory, "Outside")}");

        Assert.Equal(
            Path.Combine(root, longFolder),
            ArtistPathMappingResolver.Resolve(["The Highwaymen"], root, mappings));
        Assert.Null(ArtistPathMappingResolver.Resolve(["Root"], root, mappings));
        Assert.Null(ArtistPathMappingResolver.Resolve(["Escape"], root, mappings));
    }

    [Fact]
    public void Artist_identity_includes_name_sort_name_and_loaded_aliases()
    {
        var artist = new Artist
        {
            Name = "上原ひろみ",
            SortName = "Uehara, Hiromi",
            Metadata = new ArtistMetadata
            {
                Name = "上原ひろみ",
                Aliases = ["Hiromi"]
            }
        };

        var names = ArtistIdentityResolver.GetNames(artist);

        Assert.Contains("上原ひろみ", names);
        Assert.Contains("Uehara, Hiromi", names);
        Assert.Contains("Hiromi", names);
    }

    [Fact]
    public void Directory_resolver_reports_library_root_rejection()
    {
        var root = Path.Combine(_directory, "Music");
        var artist = Path.Combine(root, "Artist");

        var result = AlbumDirectoryResolver.Resolve(
            [Path.Combine(root, "01.flac")],
            artist,
            root);

        Assert.False(result.HasSafeTarget);
        Assert.Contains($"track directory='{root}'", result.RejectionReason, StringComparison.Ordinal);
        Assert.Contains("library root", result.RejectionReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Cover_art_archive_orders_imported_monitored_then_other_releases()
    {
        var imported = Guid.NewGuid();
        var monitored = Guid.NewGuid();
        var other = Guid.NewGuid();
        var group = Guid.NewGuid();
        var album = new Album
        {
            ForeignAlbumId = group.ToString(),
            AlbumReleases = new List<AlbumRelease>
            {
                new AlbumRelease { ForeignReleaseId = other.ToString() },
                new AlbumRelease { ForeignReleaseId = monitored.ToString(), Monitored = true }
            }
        };
        var context = new AlbumArtworkLookupContext(
            album,
            new AlbumRelease { ForeignReleaseId = imported.ToString() },
            []);

        var identifiers = AlbumArtworkResolver.BuildCoverArtArchiveIdentifiers(context);

        Assert.Equal(
            [
                ("release", imported),
                ("release", monitored),
                ("release", other),
                ("release-group", group)
            ],
            identifiers);
    }

    [Fact]
    public void Cover_art_archive_json_prefers_approved_1200px_front()
    {
        const string json =
            """
            {
              "images": [
                {
                  "front": true,
                  "approved": false,
                  "image": "https://archive.org/unapproved.jpg"
                },
                {
                  "front": true,
                  "approved": true,
                  "image": "https://archive.org/original.jpg",
                  "thumbnails": { "1200": "https://archive.org/1200.jpg" }
                }
              ]
            }
            """;

        var urls = AlbumArtworkResolver.ParseCoverArtArchiveImageUrls(json);

        Assert.Equal("https://archive.org/1200.jpg", urls[0]);
        Assert.Equal("https://archive.org/original.jpg", urls[1]);
        Assert.Equal("https://archive.org/unapproved.jpg", urls[2]);
    }

    [Fact]
    public void Resolver_uses_cover_art_archive_json_before_direct_fallback()
    {
        var context = CreateContext();
        var identifier = AlbumArtworkResolver.BuildCoverArtArchiveIdentifiers(context)[0];
        var indexUrl = $"https://coverartarchive.org/{identifier.Kind}/{identifier.Id:D}";
        const string imageUrl = "https://archive.org/indexed-front.jpg";
        var downloader = new IndexArtworkDownloader(
            indexUrl,
            $$"""{"images":[{"front":true,"approved":true,"image":"{{imageUrl}}"}]}""",
            imageUrl,
            CreateImageBytes());
        var resolver = new AlbumArtworkResolver(
            new CoverArtFinder(),
            new NullLidarrArtworkProvider(),
            new NullEmbeddedArtworkProvider(),
            downloader,
            _logger);

        using var result = resolver.Resolve(
            _directory,
            context,
            new CoverArtOptions(
                UseLidarrArtwork: false,
                ExtractEmbeddedArtwork: false));

        Assert.NotNull(result);
        Assert.Equal(ArtworkSourceKind.CoverArtArchive, result.SourceKind);
        Assert.Equal([indexUrl, imageUrl], downloader.Urls);
    }

    [Fact]
    public void Resolver_uses_remote_lidarr_cover_when_cache_is_missing()
    {
        var context = CreateContext();
        context.Album.Images =
        [
            new NzbDrone.Core.MediaCover.MediaCover(
                MediaCoverTypes.Cover,
                "https://example.com/cover.jpg")
        ];
        var downloader = new QueueArtworkDownloader(CreateImageBytes());
        var resolver = new AlbumArtworkResolver(
            new CoverArtFinder(),
            new NullLidarrArtworkProvider(),
            new NullEmbeddedArtworkProvider(),
            downloader,
            _logger);

        using var result = resolver.Resolve(
            _directory,
            context,
            new CoverArtOptions(UseCoverArtArchive: false));

        Assert.NotNull(result);
        Assert.Equal(ArtworkSourceKind.Lidarr, result.SourceKind);
        Assert.Equal("https://example.com/cover.jpg", Assert.Single(downloader.Urls));
    }

    [Fact]
    public void Discogs_provider_authenticates_and_accepts_strong_match()
    {
        var handler = new QueueHttpHandler(
            JsonResponse("""{"results":[{"id":123}]}"""),
            JsonResponse(
                """
                {
                  "id": 123,
                  "title": "Test Album",
                  "artists_sort": "Test Artist",
                  "year": 2020,
                  "images": [
                    { "type": "primary", "uri": "https://api-img.discogs.com/cover.jpg" }
                  ],
                  "tracklist": []
                }
                """),
            BytesResponse(CreateImageBytes()));
        var provider = new DiscogsArtworkProvider(_logger, handler);

        var result = provider.Find(
            CreateContext(),
            new CoverArtOptions(
                UseDiscogs: true,
                DiscogsToken: "secret-token"));

        Assert.NotNull(result);
        Assert.Equal(123, result.ReleaseId);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Equal("Discogs token=secret-token", request.Authorization));
    }

    [Fact]
    public void Discogs_provider_rejects_ambiguous_artist_match()
    {
        var handler = new QueueHttpHandler(
            JsonResponse("""{"results":[{"id":124}]}"""),
            JsonResponse(
                """
                {
                  "id": 124,
                  "title": "Test Album",
                  "artists_sort": "Different Artist",
                  "year": 2020,
                  "images": [
                    { "type": "primary", "uri": "https://api-img.discogs.com/cover.jpg" }
                  ]
                }
                """));
        var provider = new DiscogsArtworkProvider(_logger, handler);

        var result = provider.Find(
            CreateContext(),
            new CoverArtOptions(
                UseDiscogs: true,
                DiscogsToken: "secret-token"));

        Assert.Null(result);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void Discogs_override_bypasses_search()
    {
        var handler = new QueueHttpHandler(
            JsonResponse(
                """
                {
                  "id": 999,
                  "title": "Anything",
                  "images": [
                    { "type": "primary", "uri": "https://api-img.discogs.com/override.jpg" }
                  ]
                }
                """),
            BytesResponse(CreateImageBytes()));
        var provider = new DiscogsArtworkProvider(_logger, handler);

        var result = provider.Find(
            CreateContext(),
            new CoverArtOptions(
                UseDiscogs: true,
                DiscogsToken: "secret-token",
                DiscogsReleaseOverrides: "42=999"));

        Assert.NotNull(result);
        Assert.Equal(999, result.ReleaseId);
        Assert.Contains("/releases/999", handler.Requests[0].Uri);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void Discogs_provider_handles_rate_limit_without_retrying()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.TryAddWithoutValidation("Retry-After", "60");
        var handler = new QueueHttpHandler(response);
        var provider = new DiscogsArtworkProvider(_logger, handler);

        var result = provider.Find(
            CreateContext(),
            new CoverArtOptions(
                UseDiscogs: true,
                DiscogsToken: "secret-token"));

        Assert.Null(result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Artist_resolver_uses_lidarr_remote_image_and_processor_writes_artist_jpeg()
    {
        var artist = new Artist
        {
            Id = 7,
            Metadata = new ArtistMetadata
            {
                Images =
                [
                    new NzbDrone.Core.MediaCover.MediaCover(
                        MediaCoverTypes.Poster,
                        "https://example.com/artist.png")
                ]
            }
        };
        var covers = new Mock<IMapCoversToLocal>();
        covers.Setup(value => value.GetCoverPath(
                artist.Id,
                MediaCoverEntity.Artist,
                MediaCoverTypes.Poster,
                ".png",
                null))
            .Returns(Path.Combine(_directory, "missing-cache.png"));
        var resolver = new ArtistArtworkResolver(
            new CoverArtFinder(),
            covers.Object,
            new QueueArtworkDownloader(CreateImageBytes(SKEncodedImageFormat.Png)),
            _logger);

        using var artwork = resolver.Resolve(
            _directory,
            artist,
            new CoverArtOptions());
        Assert.NotNull(artwork);
        Assert.Equal(ArtworkSourceKind.ArtistLidarr, artwork.SourceKind);

        var result = new CoverArtProcessor().ProcessData(
            artwork.Data!,
            artwork.Description,
            _directory,
            new CoverArtOptions(),
            ArtworkProfile.Artist,
            artwork.SourceKind);

        Assert.Equal(CoverArtOutcome.Optimized, result.Outcome);
        Assert.True(File.Exists(Path.Combine(_directory, "artist.jpg")));
    }

    [Fact]
    public void Expanded_album_aliases_are_deterministic()
    {
        File.WriteAllBytes(Path.Combine(_directory, "artwork.jpg"), CreateImageBytes());
        File.WriteAllBytes(Path.Combine(_directory, "albumart.jpg"), CreateImageBytes());

        var result = new CoverArtFinder().Find(_directory, ArtworkProfile.Album);

        Assert.EndsWith("albumart.jpg", result, StringComparison.Ordinal);
    }

    private static AlbumArtworkLookupContext CreateContext()
    {
        return new AlbumArtworkLookupContext(
            new Album
            {
                Id = 42,
                Title = "Test Album",
                ReleaseDate = new DateTime(2020, 1, 1),
                ForeignAlbumId = Guid.NewGuid().ToString()
            },
            new AlbumRelease
            {
                ForeignReleaseId = Guid.NewGuid().ToString(),
                ReleaseDate = new DateTime(2020, 1, 1)
            },
            [],
            "Test Artist");
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage BytesResponse(byte[] data)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(data)
        };
    }

    private static byte[] CreateImageBytes(
        SKEncodedImageFormat format = SKEncodedImageFormat.Jpeg)
    {
        using var bitmap = new SKBitmap(600, 600);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private sealed class NullLidarrArtworkProvider : ILidarrArtworkProvider
    {
        public string? Find(Album album, bool allowDownload) => null;
    }

    private sealed class NullEmbeddedArtworkProvider : IEmbeddedArtworkProvider
    {
        public byte[]? Extract(IReadOnlyList<string> trackPaths) => null;
    }

    private sealed class QueueArtworkDownloader : IArtworkDownloader
    {
        private readonly Queue<byte[]?> _responses;

        public QueueArtworkDownloader(params byte[]?[] responses)
        {
            _responses = new Queue<byte[]?>(responses);
        }

        public List<string> Urls { get; } = [];

        public byte[]? Download(string url)
        {
            Urls.Add(url);
            return _responses.Count > 0 ? _responses.Dequeue() : null;
        }
    }

    private sealed class IndexArtworkDownloader : IArtworkDownloader
    {
        private readonly string _indexUrl;
        private readonly string _json;
        private readonly string _imageUrl;
        private readonly byte[] _image;

        public IndexArtworkDownloader(
            string indexUrl,
            string json,
            string imageUrl,
            byte[] image)
        {
            _indexUrl = indexUrl;
            _json = json;
            _imageUrl = imageUrl;
            _image = image;
        }

        public List<string> Urls { get; } = [];

        public byte[]? Download(string url)
        {
            Urls.Add(url);
            return url == _imageUrl ? _image : null;
        }

        public string? DownloadText(string url)
        {
            Urls.Add(url);
            return url == _indexUrl ? _json : null;
        }
    }

    private sealed class QueueHttpHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueueHttpHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestRecord(
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString()));
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed record RequestRecord(
        string Uri,
        string? Authorization);
}
