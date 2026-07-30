namespace OpenNet.Server.Contracts;

public sealed record UpdatePackageResponse(
    string Version,
    string Validation,
    string Channel,
    string Architecture,
    string PackageType,
    string ReleaseNotes,
    DateTimeOffset PublishedAtUtc,
    IReadOnlyList<UpdateMirrorResponse> Mirrors);

public sealed record UpdateMirrorResponse(
    string Url,
    string MirrorName,
    string MirrorType);
