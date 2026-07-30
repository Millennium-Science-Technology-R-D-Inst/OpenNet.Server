using Microsoft.Extensions.Options;
using OpenNet.Server.Contracts;
using OpenNet.Server.Options;

namespace OpenNet.Server.Application;

public sealed class UpdateCatalogService(IOptions<UpdateCatalogOptions> options)
{
    private readonly UpdateCatalogOptions catalog = options.Value;

    public int CacheDurationSeconds => catalog.CacheDurationSeconds;

    public UpdatePackageResponse? GetLatest(
        string channel,
        string architecture,
        string packageType)
    {
        UpdatePackageOptions? package = catalog.Packages
            .Where(candidate =>
                candidate.IsActive
                && candidate.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase)
                && candidate.Architecture.Equals(architecture, StringComparison.OrdinalIgnoreCase)
                && candidate.PackageType.Equals(packageType, StringComparison.OrdinalIgnoreCase)
                && TryParseVersion(candidate.Version, out _))
            .OrderByDescending(
                candidate =>
                {
                    TryParseVersion(candidate.Version, out Version? version);
                    return version;
                })
            .ThenByDescending(candidate => candidate.PublishedAtUtc)
            .FirstOrDefault();

        return package is null
            ? null
            : new UpdatePackageResponse(
                package.Version,
                package.Validation,
                package.Channel,
                package.Architecture,
                package.PackageType,
                package.ReleaseNotes,
                package.PublishedAtUtc,
                package.Mirrors
                    .Select(mirror => new UpdateMirrorResponse(
                        mirror.Url,
                        mirror.MirrorName,
                        mirror.MirrorType))
                    .ToArray());
    }

    private static bool TryParseVersion(string value, out Version? version)
    {
        string normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        int suffixIndex = normalized.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            normalized = normalized[..suffixIndex];
        }

        return Version.TryParse(normalized, out version);
    }
}
