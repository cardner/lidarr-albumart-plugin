using SkiaSharp;

namespace NzbDrone.Core.Plugins.AlbumArt.Services;

public interface ICoverArtProcessor
{
    CoverArtResult Process(string albumDirectory, CoverArtOptions options);
}

public sealed class CoverArtProcessor : ICoverArtProcessor
{
    private const long SoftOptimizedSizeLimit = 2 * 1024 * 1024;
    private readonly ICoverArtFinder _finder;

    public CoverArtProcessor()
        : this(new CoverArtFinder())
    {
    }

    internal CoverArtProcessor(ICoverArtFinder finder)
    {
        _finder = finder ?? throw new ArgumentNullException(nameof(finder));
    }

    public CoverArtResult Process(string albumDirectory, CoverArtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var sourcePath = _finder.Find(albumDirectory);
        if (sourcePath is null)
        {
            return new CoverArtResult(CoverArtOutcome.NoArtwork);
        }

        try
        {
            return ProcessFile(sourcePath, albumDirectory, options);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new CoverArtResult(
                CoverArtOutcome.Failed,
                SourcePath: sourcePath,
                Error: exception.Message);
        }
    }

    private static CoverArtResult ProcessFile(string sourcePath, string albumDirectory, CoverArtOptions options)
    {
        Validate(options);

        using var image = SKBitmap.Decode(sourcePath)
                          ?? throw new ArgumentException($"Unsupported or invalid image '{sourcePath}'.", nameof(sourcePath));

        var originalWidth = image.Width;
        var originalHeight = image.Height;
        var originalBytes = new FileInfo(sourcePath).Length;
        var destinationPath = GetDestinationPath(sourcePath, albumDirectory, options);
        var requiresResize = Math.Max(image.Width, image.Height) > options.MaxDimension;
        var requiresConversion = options.ConvertToJpeg &&
                                 !string.Equals(Path.GetExtension(sourcePath), ".jpg", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(Path.GetExtension(sourcePath), ".jpeg", StringComparison.OrdinalIgnoreCase);
        var requiresNormalization = !PathsEqual(sourcePath, destinationPath);
        var requiresRecompression = originalBytes > SoftOptimizedSizeLimit &&
                                    GetOutputFormat(destinationPath) == SKEncodedImageFormat.Jpeg;

        if (!requiresResize && !requiresConversion && !requiresNormalization && !requiresRecompression)
        {
            return new CoverArtResult(
                CoverArtOutcome.Skipped,
                sourcePath,
                destinationPath,
                originalWidth,
                originalHeight,
                originalWidth,
                originalHeight,
                originalBytes,
                originalBytes);
        }

        var outputSize = CalculateOutputSize(image.Width, image.Height, options);
        if (options.DryRun)
        {
            return new CoverArtResult(
                CoverArtOutcome.WouldOptimize,
                sourcePath,
                destinationPath,
                originalWidth,
                originalHeight,
                outputSize.Width,
                outputSize.Height,
                originalBytes);
        }

        WriteAtomically(image, outputSize, destinationPath, options.JpegQuality);

        if (options.RemoveRedundantSidecars && !PathsEqual(sourcePath, destinationPath))
        {
            File.Delete(sourcePath);
        }

        return new CoverArtResult(
            CoverArtOutcome.Optimized,
            sourcePath,
            destinationPath,
            originalWidth,
            originalHeight,
            outputSize.Width,
            outputSize.Height,
            originalBytes,
            new FileInfo(destinationPath).Length);
    }

    private static string GetDestinationPath(string sourcePath, string albumDirectory, CoverArtOptions options)
    {
        if (options.NormalizeToCoverJpeg)
        {
            return Path.Combine(albumDirectory, "cover.jpg");
        }

        if (options.ConvertToJpeg &&
            !string.Equals(Path.GetExtension(sourcePath), ".jpg", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Path.GetExtension(sourcePath), ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            return Path.ChangeExtension(sourcePath, ".jpg");
        }

        return sourcePath;
    }

    private static SKSizeI CalculateOutputSize(int width, int height, CoverArtOptions options)
    {
        var longestEdge = Math.Max(width, height);
        if (options.OnlyShrink && longestEdge <= options.MaxDimension)
        {
            return new SKSizeI(width, height);
        }

        if (longestEdge == options.MaxDimension)
        {
            return new SKSizeI(width, height);
        }

        var scale = (double)options.MaxDimension / longestEdge;
        return new SKSizeI(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static void WriteAtomically(SKBitmap bitmap, SKSizeI outputSize, string destinationPath, int quality)
    {
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new IOException($"Cannot determine destination directory for '{destinationPath}'.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(outputSize.Width, outputSize.Height))
                                ?? throw new IOException("Could not create the image resize surface.");
            using var paint = new SKPaint { IsAntialias = true };
            surface.Canvas.DrawBitmap(
                bitmap,
                new SKRect(0, 0, outputSize.Width, outputSize.Height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                paint);
            surface.Canvas.Flush();

            using var outputImage = surface.Snapshot();
            using var encoded = outputImage.Encode(GetOutputFormat(destinationPath), quality)
                                ?? throw new IOException("Could not encode the optimized image.");
            using (var stream = File.Create(temporaryPath))
            {
                encoded.SaveTo(stream);
            }

            File.Move(temporaryPath, destinationPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static SKEncodedImageFormat GetOutputFormat(string destinationPath)
    {
        return Path.GetExtension(destinationPath).ToLowerInvariant() switch
        {
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Jpeg
        };
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static void Validate(CoverArtOptions options)
    {
        if (options.MaxDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Maximum dimension must be greater than zero.");
        }

        if (options.JpegQuality is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "JPEG quality must be between 1 and 100.");
        }
    }
}
