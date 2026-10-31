using Moq;
using NLog;
using System.Net;
using NzbDrone.Core.Extras.Metadata;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Music;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Plugins.AlbumArt.Connect;
using NzbDrone.Core.Plugins.AlbumArt.Scheduling;
using NzbDrone.Core.Plugins.AlbumArt.Services;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using SkiaSharp;
using TagLib;
using Xunit;
using IOFile = System.IO.File;
using LidarrTag = NzbDrone.Core.Tags.Tag;

namespace Lidarr.Plugin.AlbumArt.Tests;

public sealed class AlbumArtworkResolverTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"album-art-resolver-{Guid.NewGuid():N}");
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public AlbumArtworkResolverTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Resolver_prefers_sidecar_over_all_fallback_sources()
    {
        var sidecar = CreateImage("folder.png", 300, 300);
        var lidarr = new FakeLidarrArtworkProvider(CreateImage("lidarr.jpg", 300, 300));
        var embedded = new FakeEmbeddedArtworkProvider(ReadImage("embedded.jpg", 300, 300));
        var downloader = new FakeArtworkDownloader(ReadImage("downloaded.jpg", 300, 300));
        var resolver = CreateResolver(lidarr, embedded, downloader);

        using var result = resolver.Resolve(_directory, CreateContext(), new CoverArtOptions());

        Assert.NotNull(result);
        Assert.Equal(sidecar, result.Path);
        Assert.Equal(ArtworkSourceKind.Sidecar, result.SourceKind);
        Assert.Equal(0, lidarr.Calls);
        Assert.Equal(0, embedded.Calls);
        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public void Resolver_uses_lidarr_then_embedded_then_cover_art_archive()
    {
        var lidarrPath = CreateImage("lidarr-source.png", 500, 250);
        var resolver = CreateResolver(
            new FakeLidarrArtworkProvider(lidarrPath),
            new FakeEmbeddedArtworkProvider(ReadImage("embedded-source.png", 500, 250)),
            new FakeArtworkDownloader(ReadImage("archive-source.png", 500, 250)));

        using var result = resolver.Resolve(_directory, CreateContext(), new CoverArtOptions());

        Assert.NotNull(result);
        Assert.Equal(ArtworkSourceKind.Lidarr, result.SourceKind);
        Assert.Equal(lidarrPath, result.Path);
    }

    [Fact]
    public void Lidarr_provider_ensures_and_returns_the_matching_cached_cover()
    {
        var album = CreateContext().Album;
        album.Images =
        [
            new NzbDrone.Core.MediaCover.MediaCover(
                MediaCoverTypes.Cover,
                "https://example.invalid/cover.jpg")
        ];
        var cachedPath = Path.Combine(_directory, "lidarr-cache.jpg");
        var mediaCovers = new Mock<IMapCoversToLocal>();
        mediaCovers.Setup(value => value.GetCoverPath(
                album.Id,
                MediaCoverEntity.Album,
                MediaCoverTypes.Cover,
                ".jpg",
                null))
            .Returns(cachedPath);
        mediaCovers.Setup(value => value.EnsureAlbumCovers(album))
            .Callback(() => CreateImage("lidarr-cache.jpg", 500, 500))
            .Returns(true);

        var result = new LidarrArtworkProvider(mediaCovers.Object).Find(album, true);

        Assert.Equal(cachedPath, result);
        mediaCovers.Verify(value => value.EnsureAlbumCovers(album), Times.Once);
    }

    [Fact]
    public void Resolver_skips_invalid_download_and_uses_release_fallback_url()
    {
        var invalid = "not an image"u8.ToArray();
        var valid = ReadImage("archive-valid.png", 1200, 1200);
        var downloader = new FakeArtworkDownloader(invalid, valid);
        var resolver = CreateResolver(
            new FakeLidarrArtworkProvider(null),
            new FakeEmbeddedArtworkProvider(null),
            downloader);

        using var result = resolver.Resolve(_directory, CreateContext(), new CoverArtOptions());

        Assert.NotNull(result);
        Assert.Equal(ArtworkSourceKind.CoverArtArchive, result.SourceKind);
        Assert.Equal(2, downloader.Calls);
        Assert.EndsWith("/front", downloader.Urls[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_falls_back_from_release_to_release_group()
    {
        var downloader = new FakeArtworkDownloader(
            null,
            null,
            ReadImage("release-group.png", 1200, 1200));
        var resolver = CreateResolver(
            new FakeLidarrArtworkProvider(null),
            new FakeEmbeddedArtworkProvider(null),
            downloader);

        using var result = resolver.Resolve(_directory, CreateContext(), new CoverArtOptions());

        Assert.NotNull(result);
        Assert.Equal(ArtworkSourceKind.CoverArtArchive, result.SourceKind);
        Assert.Equal(3, downloader.Calls);
        Assert.Contains("/release-group/", downloader.Urls[2], StringComparison.Ordinal);
        Assert.EndsWith("/front-1200", downloader.Urls[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_returns_null_when_every_source_is_unavailable()
    {
        var resolver = CreateResolver(
            new FakeLidarrArtworkProvider(null),
            new FakeEmbeddedArtworkProvider(null),
            new FakeArtworkDownloader());

        using var result = resolver.Resolve(_directory, CreateContext(), new CoverArtOptions());

        Assert.Null(result);
    }

    [Fact]
    public void Resolver_ignores_corrupt_sidecar_and_continues_to_fallback()
    {
        IOFile.WriteAllText(Path.Combine(_directory, "cover.jpg"), "corrupt");
        var embeddedData = ReadImage("embedded-valid.png", 600, 600);
        var resolver = CreateResolver(
            new FakeLidarrArtworkProvider(null),
            new FakeEmbeddedArtworkProvider(embeddedData),
            new FakeArtworkDownloader());

        using var result = resolver.Resolve(_directory, CreateContext(), new CoverArtOptions());

        Assert.NotNull(result);
        Assert.Equal(ArtworkSourceKind.Embedded, result.SourceKind);
    }

    [Fact]
    public void Resolver_and_processor_use_valid_lower_priority_sidecar()
    {
        IOFile.WriteAllText(Path.Combine(_directory, "cover.jpg"), "corrupt");
        var folder = CreateImage("folder.png", 1600, 800);
        var resolver = CreateResolver(
            new FakeLidarrArtworkProvider(null),
            new FakeEmbeddedArtworkProvider(null),
            new FakeArtworkDownloader());

        using var artwork = resolver.Resolve(_directory, CreateContext(), new CoverArtOptions());
        Assert.NotNull(artwork);
        Assert.Equal(folder, artwork.Path);

        var result = new CoverArtProcessor().ProcessSidecar(
            artwork.Path!,
            _directory,
            new CoverArtOptions(),
            ArtworkProfile.Album);

        Assert.Equal(CoverArtOutcome.Optimized, result.Outcome);
        using var output = SKBitmap.Decode(Path.Combine(_directory, "cover.jpg"));
        Assert.NotNull(output);
        Assert.Equal(1400, output.Width);
        Assert.Equal(700, output.Height);
    }

    [Fact]
    public void Dry_run_preserves_lidarr_priority_without_downloading_or_writing()
    {
        var context = CreateContext();
        context.Album.Images =
        [
            new NzbDrone.Core.MediaCover.MediaCover(
                MediaCoverTypes.Cover,
                "https://example.invalid/cover.jpg")
        ];
        var downloader = new FakeArtworkDownloader(ReadImage("unused-download.png", 300, 300));
        var resolver = CreateResolver(
            new FakeLidarrArtworkProvider(null),
            new FakeEmbeddedArtworkProvider(ReadImage("unused-embedded.png", 300, 300)),
            downloader);

        using var result = resolver.Resolve(
            _directory,
            context,
            new CoverArtOptions(DryRun: true));

        Assert.NotNull(result);
        Assert.Equal(ArtworkSourceKind.Lidarr, result.SourceKind);
        Assert.Empty(Assert.IsType<byte[]>(result.Data));
        Assert.Equal(0, downloader.Calls);
    }

    [Fact]
    public void Cover_art_archive_downloader_treats_404_as_a_normal_miss()
    {
        var downloader = new CoverArtArchiveDownloader(
            _logger,
            new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = downloader.Download("https://coverartarchive.org/release/test/front");

        Assert.Null(result);
    }

    [Fact]
    public void Cover_art_archive_downloader_rejects_oversized_responses()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        };
        response.Content.Headers.ContentLength = 33L * 1024 * 1024;
        var downloader = new CoverArtArchiveDownloader(
            _logger,
            new StubHttpHandler(_ => response));

        var result = downloader.Download("https://coverartarchive.org/release/test/front");

        Assert.Null(result);
    }

    [Fact]
    public void Cover_art_archive_downloader_handles_timeouts()
    {
        var downloader = new CoverArtArchiveDownloader(
            _logger,
            new StubHttpHandler(_ => throw new TaskCanceledException("timed out")));

        var result = downloader.Download("https://coverartarchive.org/release/test/front");

        Assert.Null(result);
    }

    [Fact]
    public void Explicit_fallback_source_is_normalized_to_compatible_cover_jpeg()
    {
        var source = CreateTransparentImage("fallback.png", 2800, 1400);

        var result = new CoverArtProcessor().ProcessSource(
            source,
            _directory,
            new CoverArtOptions(),
            ArtworkProfile.Album,
            ArtworkSourceKind.Embedded);

        var destination = Path.Combine(_directory, "cover.jpg");
        Assert.Equal(CoverArtOutcome.Optimized, result.Outcome);
        Assert.Equal(ArtworkSourceKind.Embedded, result.SourceKind);
        using var codec = SKCodec.Create(destination);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        using var image = SKBitmap.Decode(destination);
        Assert.NotNull(image);
        Assert.Equal(1400, image.Width);
        Assert.Equal(700, image.Height);
        var background = image.GetPixel(5, 5);
        Assert.True(background.Red > 240 && background.Green > 240 && background.Blue > 240);
        Assert.True(IOFile.Exists(source));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".cover.jpg.*.tmp"));
    }

    [Fact]
    public void Embedded_provider_extracts_front_cover_from_audio_file()
    {
        var audioPath = Path.Combine(_directory, "track.wav");
        CreateWaveFile(audioPath);
        var artworkBytes = IOFile.ReadAllBytes(CreateImage("tag-cover.png", 640, 320));
        using (var audio = TagLib.File.Create(audioPath))
        {
            audio.Tag.Pictures =
            [
                new Picture(new ByteVector("corrupt"u8.ToArray()))
                {
                    Type = PictureType.FrontCover,
                    MimeType = "image/jpeg"
                },
                new Picture(new ByteVector(artworkBytes))
                {
                    Type = PictureType.Other,
                    MimeType = "image/png"
                }
            ];
            audio.Save();
        }

        var extracted = new EmbeddedArtworkProvider().Extract([audioPath]);

        Assert.NotNull(extracted);
        using var image = SKBitmap.Decode(extracted);
        Assert.NotNull(image);
        Assert.Equal(640, image.Width);
        Assert.Equal(320, image.Height);
    }

    [Fact]
    public void Missing_tag_is_added_and_removed_when_artist_is_reconciled()
    {
        var artist = new Artist { Id = 42, Name = "Test Artist" };
        var storedTags = new List<LidarrTag>();
        var tagService = new Mock<ITagService>();
        tagService.Setup(service => service.All()).Returns(storedTags);
        tagService.Setup(service => service.Add(It.IsAny<LidarrTag>()))
            .Returns((LidarrTag tag) =>
            {
                tag.Id = 7;
                storedTags.Add(tag);
                return tag;
            });
        var artistService = new Mock<IArtistService>();
        var service = new MissingArtworkTagService(artistService.Object, tagService.Object);

        service.MarkMissing(artist);
        service.Reconcile(artist, false);

        Assert.DoesNotContain(7, artist.Tags);
        artistService.Verify(value => value.UpdateArtist(artist, true), Times.Exactly(2));
    }

    [Fact]
    public void Missing_tag_service_defers_tag_dependency_until_it_is_used()
    {
        var resolved = false;
        var tagService = new Mock<ITagService>();
        tagService.Setup(service => service.All()).Returns([]);
        tagService.Setup(service => service.Add(It.IsAny<LidarrTag>()))
            .Returns((LidarrTag tag) =>
            {
                tag.Id = 11;
                return tag;
            });
        var lazyTags = new Lazy<ITagService>(() =>
        {
            resolved = true;
            return tagService.Object;
        });
        var service = new MissingArtworkTagService(
            new Mock<IArtistService>().Object,
            lazyTags);

        Assert.False(resolved);

        service.MarkMissing(new Artist { Id = 1, Name = "Deferred" });

        Assert.True(resolved);
    }

    [Fact]
    public void Import_handler_creates_cover_from_resolved_fallback()
    {
        var artistDirectory = Path.Combine(_directory, "import-artist");
        var albumDirectory = Path.Combine(artistDirectory, "imported");
        Directory.CreateDirectory(albumDirectory);
        var trackPath = Path.Combine(albumDirectory, "track.flac");
        IOFile.WriteAllBytes(trackPath, []);
        var fallbackPath = CreateImage("import-fallback.png", 800, 800);
        var resolver = new Mock<IAlbumArtworkResolver>();
        resolver.Setup(value => value.Resolve(
                albumDirectory,
                It.IsAny<AlbumArtworkLookupContext>(),
                It.Is<CoverArtOptions>(options => !options.CreateMissingCover)))
            .Returns((ResolvedArtwork?)null);
        resolver.Setup(value => value.Resolve(
                albumDirectory,
                It.IsAny<AlbumArtworkLookupContext>(),
                It.Is<CoverArtOptions>(options => options.CreateMissingCover)))
            .Returns(new ResolvedArtwork(fallbackPath, ArtworkSourceKind.Lidarr));
        var processor = new Mock<ICoverArtProcessor>();
        processor.Setup(value => value.ProcessSource(
                fallbackPath,
                albumDirectory,
                It.IsAny<CoverArtOptions>(),
                ArtworkProfile.Album,
                ArtworkSourceKind.Lidarr))
            .Returns(new CoverArtResult(CoverArtOutcome.Optimized, SourceKind: ArtworkSourceKind.Lidarr));
        var tags = new Mock<IMissingArtworkTagService>();
        var handler = new AlbumArtOptimize(processor.Object, resolver.Object, tags.Object, _logger);
        var artist = new Artist
        {
            Id = 9,
            Name = "Import Artist",
            Path = artistDirectory,
            RootFolderPath = _directory
        };

        handler.OnReleaseImport(new AlbumDownloadMessage
        {
            Artist = artist,
            Album = new Album { Id = 10, Title = "Import Album" },
            Release = new AlbumRelease { ForeignReleaseId = Guid.NewGuid().ToString() },
            TrackFiles = [new TrackFile { Path = trackPath }]
        });

        processor.VerifyAll();
        tags.Verify(value => value.MarkMissing(It.IsAny<Artist>()), Times.Never);
    }

    [Fact]
    public void Import_handler_does_not_mark_missing_when_album_target_is_unsafe()
    {
        var artistDirectory = Path.Combine(_directory, "flat-artist");
        Directory.CreateDirectory(artistDirectory);
        var trackPath = Path.Combine(artistDirectory, "track.flac");
        IOFile.WriteAllBytes(trackPath, []);
        var artist = new Artist
        {
            Id = 12,
            Name = "Flat Artist",
            Path = artistDirectory,
            RootFolderPath = _directory
        };
        var processor = new Mock<ICoverArtProcessor>();
        processor.Setup(value => value.Process(
                artistDirectory,
                It.IsAny<CoverArtOptions>(),
                ArtworkProfile.Artist))
            .Returns(new CoverArtResult(CoverArtOutcome.NoArtwork));
        var resolver = new Mock<IAlbumArtworkResolver>();
        var tags = new Mock<IMissingArtworkTagService>();
        var handler = new AlbumArtOptimize(
            processor.Object,
            resolver.Object,
            tags.Object,
            _logger);

        handler.OnReleaseImport(new AlbumDownloadMessage
        {
            Artist = artist,
            Album = new Album { Id = 13, Title = "Flat Album" },
            Release = new AlbumRelease(),
            TrackFiles = [new TrackFile { Path = trackPath }]
        });

        resolver.Verify(value => value.Resolve(
            It.IsAny<string>(),
            It.IsAny<AlbumArtworkLookupContext>(),
            It.IsAny<CoverArtOptions>()), Times.Never);
        tags.Verify(value => value.MarkMissing(It.IsAny<Artist>()), Times.Never);
    }

    [Fact]
    public void Import_handler_allows_artist_directory_owned_by_one_album()
    {
        var artistDirectory = Path.Combine(_directory, "single-album-artist");
        Directory.CreateDirectory(artistDirectory);
        var trackPath = Path.Combine(artistDirectory, "track.flac");
        IOFile.WriteAllBytes(trackPath, []);
        var fallbackPath = CreateImage("single-album-fallback.png", 800, 800);
        var artist = new Artist
        {
            Id = 14,
            ArtistMetadataId = 15,
            Name = "Single Album Artist",
            Path = artistDirectory,
            RootFolderPath = _directory
        };
        var album = new Album
        {
            Id = 16,
            ArtistMetadataId = artist.ArtistMetadataId,
            Title = "Only Album",
            Monitored = true
        };
        var resolver = new Mock<IAlbumArtworkResolver>();
        resolver.Setup(value => value.Resolve(
                artistDirectory,
                It.IsAny<AlbumArtworkLookupContext>(),
                It.Is<CoverArtOptions>(options => !options.CreateMissingCover)))
            .Returns((ResolvedArtwork?)null);
        resolver.Setup(value => value.Resolve(
                artistDirectory,
                It.IsAny<AlbumArtworkLookupContext>(),
                It.Is<CoverArtOptions>(options => options.CreateMissingCover)))
            .Returns(new ResolvedArtwork(fallbackPath, ArtworkSourceKind.Lidarr));
        var processor = new Mock<ICoverArtProcessor>();
        processor.Setup(value => value.ProcessSource(
                fallbackPath,
                artistDirectory,
                It.IsAny<CoverArtOptions>(),
                ArtworkProfile.Album,
                ArtworkSourceKind.Lidarr))
            .Returns(new CoverArtResult(CoverArtOutcome.Optimized, SourceKind: ArtworkSourceKind.Lidarr));
        processor.Setup(value => value.Process(
                artistDirectory,
                It.IsAny<CoverArtOptions>(),
                ArtworkProfile.Artist))
            .Returns(new CoverArtResult(CoverArtOutcome.NoArtwork));
        var albumService = new Mock<IAlbumService>();
        albumService.Setup(value => value.GetAlbumsByArtistMetadataId(artist.ArtistMetadataId))
            .Returns([album]);
        var mediaFiles = new Mock<IMediaFileService>();
        var tags = new Mock<IMissingArtworkTagService>();
        var handler = new AlbumArtOptimize(
            processor.Object,
            resolver.Object,
            null,
            tags.Object,
            _logger,
            new Lazy<IAlbumService>(() => albumService.Object),
            new Lazy<IMediaFileService>(() => mediaFiles.Object));

        handler.OnReleaseImport(new AlbumDownloadMessage
        {
            Artist = artist,
            Album = album,
            Release = new AlbumRelease(),
            TrackFiles = [new TrackFile { Path = trackPath }]
        });

        processor.VerifyAll();
        resolver.VerifyAll();
        tags.Verify(value => value.MarkMissing(It.IsAny<Artist>()), Times.Never);
    }

    [Fact]
    public void Import_handler_rejects_second_concurrent_claim_for_artist_directory()
    {
        var artistDirectory = Path.Combine(_directory, "concurrent-flat-artist");
        Directory.CreateDirectory(artistDirectory);
        var firstTrack = Path.Combine(artistDirectory, "first.flac");
        var secondTrack = Path.Combine(artistDirectory, "second.flac");
        IOFile.WriteAllBytes(firstTrack, []);
        IOFile.WriteAllBytes(secondTrack, []);
        var artist = new Artist
        {
            Id = 17,
            ArtistMetadataId = 18,
            Name = "Concurrent Artist",
            Path = artistDirectory,
            RootFolderPath = _directory
        };
        var firstAlbum = new Album
        {
            Id = 19,
            ArtistMetadataId = artist.ArtistMetadataId,
            Title = "First",
            Monitored = true
        };
        var secondAlbum = new Album
        {
            Id = 20,
            ArtistMetadataId = artist.ArtistMetadataId,
            Title = "Second",
            Monitored = true
        };
        var resolver = new Mock<IAlbumArtworkResolver>();
        resolver.Setup(value => value.Resolve(
                artistDirectory,
                It.IsAny<AlbumArtworkLookupContext>(),
                It.IsAny<CoverArtOptions>()))
            .Returns((ResolvedArtwork?)null);
        var processor = new Mock<ICoverArtProcessor>();
        processor.Setup(value => value.Process(
                artistDirectory,
                It.IsAny<CoverArtOptions>(),
                ArtworkProfile.Artist))
            .Returns(new CoverArtResult(CoverArtOutcome.NoArtwork));
        var albumService = new Mock<IAlbumService>();
        albumService.Setup(value => value.GetAlbumsByArtistMetadataId(artist.ArtistMetadataId))
            .Returns([firstAlbum, secondAlbum]);
        var mediaFiles = new Mock<IMediaFileService>();
        mediaFiles.Setup(value => value.GetFilesByAlbum(It.IsAny<int>()))
            .Returns([]);
        var handler = new AlbumArtOptimize(
            processor.Object,
            resolver.Object,
            null,
            new Mock<IMissingArtworkTagService>().Object,
            _logger,
            new Lazy<IAlbumService>(() => albumService.Object),
            new Lazy<IMediaFileService>(() => mediaFiles.Object));

        handler.OnReleaseImport(new AlbumDownloadMessage
        {
            Artist = artist,
            Album = firstAlbum,
            Release = new AlbumRelease(),
            TrackFiles = [new TrackFile { Path = firstTrack }]
        });
        handler.OnReleaseImport(new AlbumDownloadMessage
        {
            Artist = artist,
            Album = secondAlbum,
            Release = new AlbumRelease(),
            TrackFiles = [new TrackFile { Path = secondTrack }]
        });

        resolver.Verify(value => value.Resolve(
            artistDirectory,
            It.IsAny<AlbumArtworkLookupContext>(),
            It.IsAny<CoverArtOptions>()), Times.Exactly(2));
    }

    [Fact]
    public void Manual_scan_processes_existing_artwork_and_reports_source_counts()
    {
        var artistDirectory = Path.Combine(_directory, "manual-scan");
        var albumDirectory = Path.Combine(artistDirectory, "album");
        Directory.CreateDirectory(albumDirectory);
        var trackPath = Path.Combine(albumDirectory, "track.flac");
        IOFile.WriteAllBytes(trackPath, []);
        CreateImage(Path.Combine("manual-scan", "album", "folder.png"), 1800, 900);
        var artist = new Artist
        {
            Id = 20,
            ArtistMetadataId = 21,
            Name = "Scan Artist",
            Monitored = true,
            Path = artistDirectory
        };
        var album = new Album
        {
            Id = 22,
            ArtistMetadataId = artist.ArtistMetadataId,
            Title = "Scan Album",
            Monitored = true,
            AlbumReleases = new List<AlbumRelease>
            {
                new() { ForeignReleaseId = Guid.NewGuid().ToString(), Monitored = true }
            }
        };
        var unmonitoredAlbum = new Album
        {
            Id = 23,
            ArtistMetadataId = artist.ArtistMetadataId,
            Title = "Unmonitored",
            Monitored = false
        };
        var emptyAlbum = new Album
        {
            Id = 24,
            ArtistMetadataId = artist.ArtistMetadataId,
            Title = "No Imported Tracks",
            Monitored = true
        };
        var artistService = new Mock<IArtistService>();
        artistService.Setup(value => value.GetAllArtists()).Returns([artist]);
        var albumService = new Mock<IAlbumService>();
        albumService.Setup(value => value.GetAlbumsByArtistMetadataId(artist.ArtistMetadataId))
            .Returns([album, unmonitoredAlbum, emptyAlbum]);
        var mediaFiles = new Mock<IMediaFileService>();
        mediaFiles.Setup(value => value.GetFilesByAlbum(album.Id)).Returns([new TrackFile { Path = trackPath }]);
        mediaFiles.Setup(value => value.GetFilesByAlbum(emptyAlbum.Id))
            .Returns([new TrackFile { Path = Path.Combine(albumDirectory, "missing.flac") }]);
        var mediaCovers = new Mock<IMapCoversToLocal>();
        var tags = new Mock<ITagService>();
        tags.Setup(value => value.All()).Returns([]);
        var notifications = new Mock<INotificationFactory>();
        notifications.Setup(value => value.All()).Returns([]);
        var rootFolders = new Mock<IRootFolderService>();
        rootFolders.Setup(value => value.GetBestRootFolderPath(artist.Path))
            .Returns(_directory);
        var tagsResolved = false;
        var scan = new AlbumArtLibraryScan(
            artistService.Object,
            albumService.Object,
            mediaFiles.Object,
            mediaCovers.Object,
            new Lazy<ITagService>(() =>
            {
                tagsResolved = true;
                return tags.Object;
            }),
            new Lazy<INotificationFactory>(() => notifications.Object),
            new Lazy<IRootFolderService>(() => rootFolders.Object),
            _logger);
        scan.Definition = new MetadataDefinition { Settings = new AlbumArtSettings() };
        var command = new AlbumArtScanCommand();

        Assert.False(tagsResolved);
        scan.Execute(command);

        Assert.True(tagsResolved);
        Assert.True(IOFile.Exists(Path.Combine(albumDirectory, "cover.jpg")));
        Assert.Contains("sidecar: 1", command.ResultMessage, StringComparison.Ordinal);
        Assert.Contains("0 unresolved", command.ResultMessage, StringComparison.Ordinal);
        rootFolders.Verify(value => value.GetBestRootFolderPath(artist.Path), Times.Once);
        mediaFiles.Verify(value => value.GetFilesByAlbum(unmonitoredAlbum.Id), Times.Never);
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    private AlbumArtworkResolver CreateResolver(
        ILidarrArtworkProvider lidarr,
        IEmbeddedArtworkProvider embedded,
        IArtworkDownloader downloader)
    {
        return new AlbumArtworkResolver(new CoverArtFinder(), lidarr, embedded, downloader, _logger);
    }

    private static AlbumArtworkLookupContext CreateContext()
    {
        return new AlbumArtworkLookupContext(
            new Album
            {
                Id = 5,
                Title = "Test Album",
                ForeignAlbumId = "48140466-cff6-3222-bd55-63c27e43190d"
            },
            new AlbumRelease { ForeignReleaseId = "76df3287-6cda-33eb-8e9a-044b5e15ffdd" },
            []);
    }

    private string CreateImage(string fileName, int width, int height)
    {
        var path = Path.Combine(_directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(45, 100, 180));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(
            Path.GetExtension(fileName).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? SKEncodedImageFormat.Png
                : SKEncodedImageFormat.Jpeg,
            95);
        using var stream = IOFile.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private byte[] ReadImage(string fileName, int width, int height)
    {
        return IOFile.ReadAllBytes(CreateImage(fileName, width, height));
    }

    private string CreateTransparentImage(string fileName, int width, int height)
    {
        var path = Path.Combine(_directory, fileName);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Transparent);
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint { Color = SKColors.Blue };
        canvas.DrawRect(new SKRect(width / 4f, height / 4f, width * 3f / 4f, height * 3f / 4f), paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = IOFile.Create(path);
        data.SaveTo(stream);
        return path;
    }

    private static void CreateWaveFile(string path)
    {
        const int sampleRate = 8000;
        const int sampleCount = 800;
        using var stream = IOFile.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + sampleCount * 2);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(sampleCount * 2);
        writer.Write(new byte[sampleCount * 2]);
    }

    private sealed class FakeLidarrArtworkProvider : ILidarrArtworkProvider
    {
        private readonly string? _path;

        public FakeLidarrArtworkProvider(string? path)
        {
            _path = path;
        }

        public int Calls { get; private set; }

        public string? Find(Album album, bool allowDownload)
        {
            Calls++;
            return _path;
        }
    }

    private sealed class FakeEmbeddedArtworkProvider : IEmbeddedArtworkProvider
    {
        private readonly byte[]? _data;

        public FakeEmbeddedArtworkProvider(byte[]? data)
        {
            _data = data;
        }

        public int Calls { get; private set; }

        public byte[]? Extract(IReadOnlyList<string> trackPaths)
        {
            Calls++;
            return _data;
        }
    }

    private sealed class FakeArtworkDownloader : IArtworkDownloader
    {
        private readonly Queue<byte[]?> _data;

        public FakeArtworkDownloader(params byte[]?[] data)
        {
            _data = new Queue<byte[]?>(data);
        }

        public int Calls { get; private set; }

        public List<string> Urls { get; } = [];

        public byte[]? Download(string url)
        {
            Calls++;
            Urls.Add(url);
            return _data.Count > 0 ? _data.Dequeue() : null;
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_response(request));
        }
    }
}
