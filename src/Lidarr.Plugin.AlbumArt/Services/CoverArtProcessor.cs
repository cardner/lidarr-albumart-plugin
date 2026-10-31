using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

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
            using var image = Image.Load<Rgba32>(stream);
            image.Mutate(context => context.AutoOrient());
            return ProcessDecodedImage(
                image,
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

        using var image = Image.Load<Rgba32>(sourcePath);
        image.Mutate(context => context.AutoOrient());
        return ProcessDecodedImage(
            image,
            sourcePath,
            new FileInfo(sourcePath).Length,
            directory,
            options,
            profile,
            sourceKind,
            deleteSourceAfterSuccess);
    }

    private static CoverArtResult ProcessDecodedImage(
        Image<Rgba32> image,
        string sourcePath,
        long originalBytes,
        string directory,
        CoverArtOptions options,
        ArtworkProfile profile,
        ArtworkSourceKind sourceKind,
        bool deleteSourceAfterSuccess)
    {
        Validate(options);

        var originalWidth = image.Width;
        var originalHeight = image.Height;
        var destinationPath = GetDestinationPath(sourcePath, directory, options, profile);
        var requiresResize = Math.Max(originalWidth, originalHeight) > options.MaxDimension;
        var requiresConversion = options.ConvertToJpeg &&
                                 !string.Equals(Path.GetExtension(sourcePath), ".jpg", StringComparison.OrdinalIgnoreCase) &&
                                 !string.Equals(Path.GetExtension(sourcePath), ".jpeg", StringComparison.OrdinalIgnoreCase);
        var requiresNormalization = !PathsEqual(sourcePath, destinationPath);
        var requiresRecompression = originalBytes > SoftOptimizedSizeLimit &&
                                    IsJpeg(destinationPath);

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

        WriteAtomically(image, outputSize, destinationPath, options.JpegQuality);

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

    private static Size CalculateOutputSize(int width, int height, CoverArtOptions options)
    {
        var longestEdge = Math.Max(width, height);
        if (options.OnlyShrink && longestEdge <= options.MaxDimension)
        {
            return new Size(width, height);
        }

        if (longestEdge == options.MaxDimension)
        {
            return new Size(width, height);
        }

        var scale = (double)options.MaxDimension / longestEdge;
        return new Size(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static void WriteAtomically(
        Image<Rgba32> image,
        Size outputSize,
        string destinationPath,
        int quality)
    {
        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new IOException($"Cannot determine destination directory for '{destinationPath}'.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using var outputImage = image.Clone();
            if (outputImage.Width != outputSize.Width || outputImage.Height != outputSize.Height)
            {
                outputImage.Mutate(context => context.Resize(new ResizeOptions
                {
                    Size = outputSize,
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Lanczos3
                }));
            }

            if (IsJpeg(destinationPath))
            {
                FlattenTransparency(outputImage);
            }

            using (var stream = File.Create(temporaryPath))
            {
                SaveImage(outputImage, stream, destinationPath, quality);
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

    private static void FlattenTransparency(Image<Rgba32> image)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                foreach (ref var pixel in row)
                {
                    var alpha = pixel.A;
                    if (alpha == byte.MaxValue)
                    {
                        continue;
                    }

                    pixel.R = BlendWithWhite(pixel.R, alpha);
                    pixel.G = BlendWithWhite(pixel.G, alpha);
                    pixel.B = BlendWithWhite(pixel.B, alpha);
                    pixel.A = byte.MaxValue;
                }
            }
        });
    }

    private static byte BlendWithWhite(byte value, byte alpha)
    {
        return (byte)((value * alpha + byte.MaxValue * (byte.MaxValue - alpha) + 127) / byte.MaxValue);
    }

    private static void SaveImage(
        Image<Rgba32> image,
        Stream stream,
        string destinationPath,
        int quality)
    {
        switch (Path.GetExtension(destinationPath).ToLowerInvariant())
        {
            case ".png":
                image.Save(stream, new PngEncoder());
                break;
            case ".webp":
                image.Save(stream, new WebpEncoder { Quality = quality });
                break;
            default:
                image.Save(stream, new JpegEncoder { Quality = quality });
                break;
        }
    }

    private static bool IsJpeg(string path) =>
        Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

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
