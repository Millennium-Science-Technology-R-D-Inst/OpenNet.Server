using System.ComponentModel.DataAnnotations;

namespace OpenNet.Server.Options;

public sealed class UpdateCatalogOptions
{
    public const string SectionName = "UpdateCatalog";

    [Range(1, 3600)]
    public int CacheDurationSeconds { get; init; } = 300;

    public IReadOnlyList<UpdatePackageOptions> Packages { get; init; } = [];

    public bool HasValidPackages()
    {
        return Packages.All(package =>
            IsValidObject(package)
            && package.Mirrors.Count > 0
            && package.Mirrors.All(IsValidObject));
    }

    private static bool IsValidObject(object value)
    {
        return Validator.TryValidateObject(
            value,
            new ValidationContext(value),
            validationResults: null,
            validateAllProperties: true);
    }
}

public sealed class UpdatePackageOptions
{
    [Required, RegularExpression("^v?\\d+(\\.\\d+){1,3}([+-].+)?$")]
    public required string Version { get; init; }

    [Required]
    public string Channel { get; init; } = "stable";

    [Required, RegularExpression("^(x86|x64|arm64)$")]
    public required string Architecture { get; init; }

    [Required, RegularExpression("^(installer|msix)$")]
    public required string PackageType { get; init; }

    [Required, RegularExpression("^[0-9a-fA-F]{64}$")]
    public required string Validation { get; init; }

    public string ReleaseNotes { get; init; } = string.Empty;

    public DateTimeOffset PublishedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public bool IsActive { get; init; } = true;

    [MinLength(1)]
    public IReadOnlyList<UpdateMirrorOptions> Mirrors { get; init; } = [];
}

public sealed class UpdateMirrorOptions
{
    [Required, Url]
    public required string Url { get; init; }

    [Required]
    public required string MirrorName { get; init; }

    [Required, RegularExpression("^(Direct|Archive|Browser)$")]
    public string MirrorType { get; init; } = "Browser";
}
