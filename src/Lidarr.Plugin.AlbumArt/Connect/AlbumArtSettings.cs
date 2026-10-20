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

    [FieldDefinition(7, Label = "Optimize artist images", Type = FieldType.Checkbox, HelpText = "Optimize artist.* sidecars in artist roots and album folders.")]
    public bool OptimizeArtistImages { get; set; }

    [FieldDefinition(8, Label = "Dry run", Type = FieldType.Checkbox, HelpText = "Log intended changes without writing or deleting files.")]
    public bool DryRun { get; set; }

    public CoverArtOptions ToOptions()
    {
        return new CoverArtOptions(
            MaxDimension,
            JpegQuality,
            NormalizeToCoverJpeg,
            RemoveRedundantSidecars,
            ConvertToJpeg,
            OnlyShrink,
            DryRun);
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
    }
}
