using Microsoft.Extensions.Options;
using OpenNet.Server.Application;
using OpenNet.Server.Contracts;
using OpenNet.Server.Options;

namespace OpenNet.Server.Tests;

[TestClass]
public sealed class UpdateCatalogServiceTests
{
    [TestMethod]
    public void GetLatest_FiltersTargetAndReturnsHighestVersion()
    {
        UpdateCatalogService service = CreateService(
            Package("1.2.0", "stable", "x64", "installer"),
            Package("1.10.0", "stable", "x64", "installer"),
            Package("2.0.0", "preview", "x64", "installer"),
            Package("3.0.0", "stable", "arm64", "installer"));

        UpdatePackageResponse? result =
            service.GetLatest("STABLE", "X64", "INSTALLER");

        Assert.IsNotNull(result);
        Assert.AreEqual("1.10.0", result.Version);
        Assert.HasCount(1, result.Mirrors);
    }

    [TestMethod]
    public void GetLatest_AcceptsVPrefixAndPrereleaseSuffix()
    {
        UpdateCatalogService service = CreateService(
            Package("v1.9.0", "preview", "x64", "msix"),
            Package("v2.0.0-preview.1", "preview", "x64", "msix"));

        UpdatePackageResponse? result =
            service.GetLatest("preview", "x64", "msix");

        Assert.IsNotNull(result);
        Assert.AreEqual("v2.0.0-preview.1", result.Version);
    }

    [TestMethod]
    public void GetLatest_ReturnsNullWhenNoTargetMatches()
    {
        UpdateCatalogService service = CreateService(
            Package("1.0.0", "stable", "x64", "installer"));

        Assert.IsNull(service.GetLatest("stable", "arm64", "installer"));
    }

    private static UpdateCatalogService CreateService(
        params UpdatePackageOptions[] packages)
    {
        return new UpdateCatalogService(
            Options.Create(new UpdateCatalogOptions
            {
                Packages = packages
            }));
    }

    private static UpdatePackageOptions Package(
        string version,
        string channel,
        string architecture,
        string packageType)
    {
        return new UpdatePackageOptions
        {
            Version = version,
            Channel = channel,
            Architecture = architecture,
            PackageType = packageType,
            Validation = new string('a', 64),
            Mirrors =
            [
                new UpdateMirrorOptions
                {
                    Url = "https://downloads.example.test/OpenNet.exe",
                    MirrorName = "Primary",
                    MirrorType = "Direct"
                }
            ]
        };
    }
}
