using SkiaSharp;

namespace NzbDrone.Core.Plugins.AlbumArt.Services;

public interface ICoverArtProcessor
{
    CoverArtResult Process(string directory, CoverArtOptions options, ArtworkProfile profile);

    CoverArtResult ProcessSource(
        string sourcePath,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile,
        ArtworkSourceKind sourceKind);

    CoverArtResult ProcessSidecar(
        string sourcePath,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile);

    CoverArtResult ProcessData(
        byte[] sourceData,
        string sourceDescription,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile,
        ArtworkSourceKind sourceKind);
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

    public CoverArtResult Process(string directory, CoverArtOptions options, ArtworkProfile profile)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);

        var sourcePath = _finder.Find(directory, profile);
        if (sourcePath is null)
        {
            return new CoverArtResult(CoverArtOutcome.NoArtwork);
        }

        try
        {
            return ProcessFile(
                sourcePath,
                directory,
                options,
                profile,
                ArtworkSourceKind.Sidecar,
                options.RemoveRedundantSidecars);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new CoverArtResult(
                CoverArtOutcome.Failed,
                SourcePath: sourcePath,
                Error: exception.Message);
        }
    }

    public CoverArtResult ProcessSource(
        string sourcePath,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile,
        ArtworkSourceKind sourceKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            return ProcessFile(
                sourcePath,
                directory,
                options with { NormalizeToJpeg = true, ConvertToJpeg = true },
                profile,
                sourceKind,
                false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new CoverArtResult(
                CoverArtOutcome.Failed,
                SourcePath: sourcePath,
                Error: exception.Message,
                SourceKind: sourceKind);
        }
    }

    public CoverArtResult ProcessSidecar(
        string sourcePath,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            return ProcessFile(
                sourcePath,
                directory,
                options,
                profile,
                ArtworkSourceKind.Sidecar,
                options.RemoveRedundantSidecars);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new CoverArtResult(
                CoverArtOutcome.Failed,
                SourcePath: sourcePath,
                Error: exception.Message,
                SourceKind: ArtworkSourceKind.Sidecar);
        }
    }

    public CoverArtResult ProcessData(
        byte[] sourceData,
        string sourceDescription,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile,
        ArtworkSourceKind sourceKind)
    {
        ArgumentNullException.ThrowIfNull(sourceData);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDescription);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);

        try
        {
            using var stream = new MemoryStream(sourceData, false);
            using var codec = SKCodec.Create(stream)
                              ?? throw new ArgumentException($"Unsupported or invalid image '{sourceDescription}'.");
            using var image = SKBitmap.Decode(codec)
                              ?? throw new ArgumentException($"Could not decode image '{sourceDescription}'.");
            return ProcessDecodedImage(
                image,
                codec.EncodedOrigin,
                sourceDescription,
                sourceData.LongLength,
                directory,
                options with { NormalizeToJpeg = true, ConvertToJpeg = true },
                profile,
                sourceKind,
                false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new CoverArtResult(
                CoverArtOutcome.Failed,
                SourcePath: sourceDescription,
                Error: exception.Message,
                SourceKind: sourceKind);
        }
    }

    private static CoverArtResult ProcessFile(
        string sourcePath,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile,
        ArtworkSourceKind sourceKind,
        bool deleteSourceAfterSuccess)
    {
        Validate(options);

        using var image = DecodeImage(sourcePath, out var encodedOrigin);
        return ProcessDecodedImage(
            image,
            encodedOrigin,
            sourcePath,
            new FileInfo(sourcePath).Length,
            directory,
            options,
            profile,
            sourceKind,
            deleteSourceAfterSuccess);
    }

    private static CoverArtResult ProcessDecodedImage(
        SKBitmap image,
        SKEncodedOrigin encodedOrigin,
        string sourcePath,
        long originalBytes,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile,
        ArtworkSourceKind sourceKind,
        bool deleteSourceAfterSuccess)
    {
        Validate(options);
        var orientedSize = GetOrientedSize(image.Width, image.Height, encodedOrigin);

        var originalWidth = orientedSize.Width;
        var originalHeight = orientedSize.Height;
        var destinationPath = GetDestinationPath(sourcePath, directory, options, profile);
        var requiresResize = Math.Max(originalWidth, originalHeight) > options.MaxDimension;
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
                originalBytes,
                SourceKind: sourceKind);
        }

        var outputSize = CalculateOutputSize(originalWidth, originalHeight, options);
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
                originalBytes,
                SourceKind: sourceKind);
        }

        WriteAtomically(image, encodedOrigin, outputSize, destinationPath, options.JpegQuality);

        if (deleteSourceAfterSuccess && !PathsEqual(sourcePath, destinationPath))
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
            new FileInfo(destinationPath).Length,
            SourceKind: sourceKind);
    }

    private static string GetDestinationPath(
        string sourcePath,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile)
    {
        if (options.NormalizeToJpeg)
        {
            return Path.Combine(directory, profile.NormalizedFileName);
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

    private static SKSizeI GetOrientedSize(int width, int height, SKEncodedOrigin origin)
    {
        return origin is SKEncodedOrigin.LeftTop or
            SKEncodedOrigin.RightTop or
            SKEncodedOrigin.RightBottom or
            SKEncodedOrigin.LeftBottom
            ? new SKSizeI(height, width)
            : new SKSizeI(width, height);
    }

    private static SKBitmap DecodeImage(string sourcePath, out SKEncodedOrigin origin)
    {
        using var sourceStream = File.OpenRead(sourcePath);
        using var codec = SKCodec.Create(sourceStream)
                          ?? throw new ArgumentException($"Unsupported or invalid image '{sourcePath}'.", nameof(sourcePath));
        origin = codec.EncodedOrigin;
        return SKBitmap.Decode(codec)
               ?? throw new ArgumentException($"Could not decode image '{sourcePath}'.", nameof(sourcePath));
    }

    private static void WriteAtomically(
        SKBitmap bitmap,
        SKEncodedOrigin origin,
        SKSizeI outputSize,
        string destinationPath,
        int quality)
    {
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new IOException($"Cannot determine destination directory for '{destinationPath}'.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var orientedSize = GetOrientedSize(bitmap.Width, bitmap.Height, origin);
            using var orientedSurface = SKSurface.Create(new SKImageInfo(orientedSize.Width, orientedSize.Height))
                                        ?? throw new IOException("Could not create the orientation surface.");
            orientedSurface.Canvas.Clear(SKColors.Transparent);
            ApplyOrientation(orientedSurface.Canvas, origin, bitmap.Width, bitmap.Height);
            orientedSurface.Canvas.DrawBitmap(bitmap, 0, 0, SKSamplingOptions.Default);
            orientedSurface.Canvas.Flush();
            using var orientedImage = orientedSurface.Snapshot();

            using var surface = SKSurface.Create(new SKImageInfo(outputSize.Width, outputSize.Height))
                                ?? throw new IOException("Could not create the image resize surface.");
            var outputFormat = GetOutputFormat(destinationPath);
            surface.Canvas.Clear(outputFormat == SKEncodedImageFormat.Jpeg ? SKColors.White : SKColors.Transparent);
            surface.Canvas.DrawImage(
                orientedImage,
                new SKRect(0, 0, outputSize.Width, outputSize.Height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            surface.Canvas.Flush();

            using var outputImage = surface.Snapshot();
            using var encoded = outputImage.Encode(outputFormat, quality)
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

    private static void ApplyOrientation(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(height, width);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, width);
                canvas.RotateDegrees(-90);
                break;
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
