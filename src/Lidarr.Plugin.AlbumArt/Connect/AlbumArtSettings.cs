using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using NzbDrone.Core.Plugins.AlbumArt.Services;

namespace NzbDrone.Core.Plugins.AlbumArt.Connect;

public sealed class AlbumArtSettings : IProviderConfig
{
    private static readonly AlbumArtSettingsValidator Validator = new();

    public AlbumArtSettings()
    {
        MaxDimension = 1400;
        JpegQuality = 85;
        NormalizeToCoverJpeg = true;
        RemoveRedundantSidecars = true;
        ConvertToJpeg = true;
        OnlyShrink = true;
        ScanIntervalMinutes = 1440;
        OptimizeArtistImages = true;
        CreateMissingCover = true;
        UseLidarrArtwork = true;
        ExtractEmbeddedArtwork = true;
        UseCoverArtArchive = true;
        DiscogsToken = string.Empty;
        DiscogsReleaseOverrides = string.Empty;
        ArtistPathMappings = string.Empty;
    }

    [FieldDefinition(0, Label = "Max dimension (px)", Type = FieldType.Number, HelpText = "Maximum width or height. Defaults to 1400.")]
    public int MaxDimension { get; set; }

    [FieldDefinition(1, Label = "JPEG quality", Type = FieldType.Number, HelpText = "Source JPEG quality from 1 to 100. Defaults to 85 to reduce artifacts when Navidrome resizes it again.")]
    public int JpegQuality { get; set; }

    [FieldDefinition(2, Label = "Normalize artwork filenames", Type = FieldType.Checkbox, HelpText = "Write cover.jpg or artist.jpg so artwork wins Navidrome's default priorities.")]
    public bool NormalizeToCoverJpeg { get; set; }

    [FieldDefinition(3, Label = "Remove redundant sidecars", Type = FieldType.Checkbox, HelpText = "Delete the selected source image only after its normalized JPEG is written successfully.")]
    public bool RemoveRedundantSidecars { get; set; }

    [FieldDefinition(4, Label = "Convert PNG/WebP to JPEG", Type = FieldType.Checkbox, HelpText = "Encode supported sidecars as compact JPEG artwork.")]
    public bool ConvertToJpeg { get; set; }

    [FieldDefinition(5, Label = "Only shrink (never upscale)", Type = FieldType.Checkbox, HelpText = "Preserve smaller artwork at its original dimensions.")]
    public bool OnlyShrink { get; set; }

    [FieldDefinition(6, Label = "Scan interval (minutes)", Type = FieldType.Number, HelpText = "How often to scan the existing library. Minimum 60; defaults to 1440 (24 hours).")]
    public int ScanIntervalMinutes { get; set; }

    [FieldDefinition(7, Label = "Optimize artist images", Type = FieldType.Checkbox, HelpText = "Optimize artist.* sidecars and backfill artist.jpg in artist roots from Lidarr metadata.")]
    public bool OptimizeArtistImages { get; set; }

    [FieldDefinition(8, Label = "Create missing album covers", Type = FieldType.Checkbox, HelpText = "Create cover.jpg when no supported sidecar is present.")]
    public bool CreateMissingCover { get; set; }

    [FieldDefinition(9, Label = "Use Lidarr artwork", Type = FieldType.Checkbox, HelpText = "Use Lidarr's matching cached or remote album cover before other fallback sources.")]
    public bool UseLidarrArtwork { get; set; }

    [FieldDefinition(10, Label = "Extract embedded artwork", Type = FieldType.Checkbox, HelpText = "Use front-cover artwork embedded in imported audio files when Lidarr has no cover.")]
    public bool ExtractEmbeddedArtwork { get; set; }

    [FieldDefinition(11, Label = "Use Cover Art Archive", Type = FieldType.Checkbox, HelpText = "Try imported, monitored, and other known MusicBrainz releases before release-group artwork.")]
    public bool UseCoverArtArchive { get; set; }

    [FieldDefinition(12, Label = "Dry run", Type = FieldType.Checkbox, HelpText = "Log intended changes without writing or deleting files.")]
    public bool DryRun { get; set; }

    [FieldDefinition(13, Label = "Use Discogs fallback", Type = FieldType.Checkbox, HelpText = "After local, Lidarr, embedded, and MusicBrainz sources miss, search Discogs using conservative matching.")]
    public bool UseDiscogs { get; set; }

    [FieldDefinition(14, Label = "Discogs personal access token", Type = FieldType.Password, Privacy = PrivacyLevel.ApiKey, HelpText = "Required for Discogs image access. The token is sent only to Discogs and is never logged.")]
    public string DiscogsToken { get; set; }

    [FieldDefinition(15, Label = "Discogs release overrides", Type = FieldType.Textbox, Advanced = true, HelpText = "Optional exact mappings in LidarrAlbumId=DiscogsReleaseId form, separated by lines or commas.")]
    public string DiscogsReleaseOverrides { get; set; }

    [FieldDefinition(16, Label = "Artist path mappings", Type = FieldType.Textbox, Advanced = true, HelpText = "Optional one-per-line mappings in LidarrArtistName=FolderName or LidarrArtistName=/absolute/path form.")]
    public string ArtistPathMappings { get; set; }

    public CoverArtOptions ToOptions()
    {
        return new CoverArtOptions(
            MaxDimension,
            JpegQuality,
            NormalizeToCoverJpeg,
            RemoveRedundantSidecars,
            ConvertToJpeg,
            OnlyShrink,
            DryRun,
            CreateMissingCover,
            UseLidarrArtwork,
            ExtractEmbeddedArtwork,
            UseCoverArtArchive,
            UseDiscogs,
            DiscogsToken,
            DiscogsReleaseOverrides,
            ArtistPathMappings);
    }

    public NzbDroneValidationResult Validate()
    {
        return new NzbDroneValidationResult(Validator.Validate(this));
    }
}

public sealed class AlbumArtSettingsValidator : AbstractValidator<AlbumArtSettings>
{
    public AlbumArtSettingsValidator()
    {
        RuleFor(settings => settings.MaxDimension)
            .InclusiveBetween(100, 10000)
            .WithMessage("Maximum dimension must be between 100 and 10000 pixels.");
        RuleFor(settings => settings.JpegQuality)
            .InclusiveBetween(1, 100)
            .WithMessage("JPEG quality must be between 1 and 100.");
        RuleFor(settings => settings.ScanIntervalMinutes)
            .GreaterThanOrEqualTo(60)
            .WithMessage("Scan interval must be at least 60 minutes.");
        RuleFor(settings => settings.DiscogsToken)
            .NotEmpty()
            .When(settings => settings.UseDiscogs)
            .WithMessage("A Discogs personal access token is required when Discogs fallback is enabled.");
    }
}
