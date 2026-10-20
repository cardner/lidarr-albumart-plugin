using NzbDrone.Core.Plugins.AlbumArt.Services;
using SkiaSharp;
using Xunit;

namespace Lidarr.Plugin.AlbumArt.Tests;

public sealed class CoverArtProcessorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"album-art-tests-{Guid.NewGuid():N}");

    public CoverArtProcessorTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Finder_prefers_cover_over_folder_and_front()
    {
        CreateImage("front.png", 10, 10);
        CreateImage("folder.jpg", 10, 10);
        CreateImage("cover.webp", 10, 10);

        var result = new CoverArtFinder().Find(_directory, ArtworkProfile.Album);

        Assert.Equal("cover.webp", Path.GetFileName(result));
    }

    [Fact]
    public void Processor_resizes_png_writes_cover_jpeg_and_removes_source()
    {
        var source = CreateImage("folder.png", 2800, 2100);

        var result = new CoverArtProcessor().Process(_directory, new CoverArtOptions(), ArtworkProfile.Album);

        var destination = Path.Combine(_directory, "cover.jpg");
        Assert.Equal(CoverArtOutcome.Optimized, result.Outcome);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        using var image = SKBitmap.Decode(destination);
        Assert.NotNull(image);
        Assert.Equal(1400, image.Width);
        Assert.Equal(1050, image.Height);
        Assert.True(new FileInfo(destination).Length < result.OriginalBytes);
    }

    [Fact]
    public void Processor_dry_run_does_not_write_or_delete()
    {
        var source = CreateImage("front.png", 2000, 2000);

        var result = new CoverArtProcessor().Process(
            _directory,
            new CoverArtOptions(DryRun: true),
            ArtworkProfile.Album);

        Assert.Equal(CoverArtOutcome.WouldOptimize, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(_directory, "cover.jpg")));
    }

    [Fact]
    public void Processor_skips_small_existing_cover_jpeg()
    {
        var source = CreateImage("cover.jpg", 800, 800);
        var before = File.GetLastWriteTimeUtc(source);

        var result = new CoverArtProcessor().Process(_directory, new CoverArtOptions(), ArtworkProfile.Album);

        Assert.Equal(CoverArtOutcome.Skipped, result.Outcome);
        Assert.Equal(before, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public void Processor_preserves_png_name_and_format_when_conversion_is_disabled()
    {
        var source = CreateImage("folder.png", 2000, 1000);
        var options = new CoverArtOptions(
            NormalizeToJpeg: false,
            ConvertToJpeg: false,
            RemoveRedundantSidecars: false);

        var result = new CoverArtProcessor().Process(_directory, options, ArtworkProfile.Album);

        Assert.Equal(CoverArtOutcome.Optimized, result.Outcome);
        Assert.Equal(source, result.DestinationPath);
        using var codec = SKCodec.Create(source);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
    }

    [Fact]
    public void Artist_finder_prefers_jpeg_and_ignores_album_artwork()
    {
        CreateImage("cover.jpg", 100, 100);
        CreateImage("artist.png", 100, 100);
        CreateImage("artist.jpg", 100, 100);

        var result = new CoverArtFinder().Find(_directory, ArtworkProfile.Artist);

        Assert.Equal("artist.jpg", Path.GetFileName(result));
    }

    [Fact]
    public void Album_and_artist_profiles_do_not_select_each_others_artwork()
    {
        CreateImage("artist.jpg", 100, 100);
        Assert.Null(new CoverArtFinder().Find(_directory, ArtworkProfile.Album));

        File.Delete(Path.Combine(_directory, "artist.jpg"));
        CreateImage("cover.jpg", 100, 100);
        Assert.Null(new CoverArtFinder().Find(_directory, ArtworkProfile.Artist));
    }

    [Fact]
    public void Processor_resizes_artist_png_to_artist_jpeg_preserving_aspect_ratio()
    {
        var source = CreateImage("artist.png", 2400, 1200);

        var result = new CoverArtProcessor().Process(
            _directory,
            new CoverArtOptions(),
            ArtworkProfile.Artist);

        var destination = Path.Combine(_directory, "artist.jpg");
        Assert.Equal(CoverArtOutcome.Optimized, result.Outcome);
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
        using var image = SKBitmap.Decode(destination);
        Assert.NotNull(image);
        Assert.Equal(1400, image.Width);
        Assert.Equal(700, image.Height);
    }

    [Fact]
    public void Processor_artist_dry_run_does_not_write_or_delete()
    {
        var source = CreateImage("artist.png", 2000, 1000);

        var result = new CoverArtProcessor().Process(
            _directory,
            new CoverArtOptions(DryRun: true),
            ArtworkProfile.Artist);

        Assert.Equal(CoverArtOutcome.WouldOptimize, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(_directory, "artist.jpg")));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, true);
    }

    private string CreateImage(string fileName, int width, int height)
    {
        var path = Path.Combine(_directory, fileName);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(45, 100, 180));
        using var image = SKImage.FromBitmap(bitmap);
        var format = Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };
        using var data = image.Encode(format, 95);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }
}
