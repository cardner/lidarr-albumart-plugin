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

        var result = new CoverArtFinder().Find(_directory);

        Assert.Equal("cover.webp", Path.GetFileName(result));
    }

    [Fact]
    public void Processor_resizes_png_writes_cover_jpeg_and_removes_source()
    {
        var source = CreateImage("folder.png", 2800, 2100);

        var result = new CoverArtProcessor().Process(_directory, new CoverArtOptions());

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
            new CoverArtOptions(DryRun: true));

        Assert.Equal(CoverArtOutcome.WouldOptimize, result.Outcome);
        Assert.True(File.Exists(source));
        Assert.False(File.Exists(Path.Combine(_directory, "cover.jpg")));
    }

    [Fact]
    public void Processor_skips_small_existing_cover_jpeg()
    {
        var source = CreateImage("cover.jpg", 800, 800);
        var before = File.GetLastWriteTimeUtc(source);

        var result = new CoverArtProcessor().Process(_directory, new CoverArtOptions());

        Assert.Equal(CoverArtOutcome.Skipped, result.Outcome);
        Assert.Equal(before, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public void Processor_preserves_png_name_and_format_when_conversion_is_disabled()
    {
        var source = CreateImage("folder.png", 2000, 1000);
        var options = new CoverArtOptions(
            NormalizeToCoverJpeg: false,
            ConvertToJpeg: false,
            RemoveRedundantSidecars: false);

        var result = new CoverArtProcessor().Process(_directory, options);

        Assert.Equal(CoverArtOutcome.Optimized, result.Outcome);
        Assert.Equal(source, result.DestinationPath);
        using var codec = SKCodec.Create(source);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
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
