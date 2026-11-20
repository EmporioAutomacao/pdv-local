using SyncAgent.Installer;

namespace SyncAgent.Tests;

public class InstallVersionComparerTests
{
    private const string CommitA = "a3b499473b801963ba973f33da7da59523e9b5ef";
    private const string CommitB = "9a838006c768897c2a47dad17fb218c5bfec6654";

    [Fact]
    public void SameCommitIsSameBuild()
    {
        var installed = new InstallVersionSnapshot("1.6.46", $"1.6.46+{CommitA}");
        var package = new InstallVersionSnapshot("1.6.46", $"1.6.46+{CommitA}");

        Assert.Equal(InstallComparison.SameBuild, InstallVersionComparer.Compare(installed, package));
    }

    [Fact]
    public void SameVersionFileButOtherCommitIsDifferent()
    {
        // Mesmo rotulo de release, binarios de outro build: antes o instalador dizia "ja esta na versao".
        var installed = new InstallVersionSnapshot("1.6.46", $"1.6.46+{CommitB}");
        var package = new InstallVersionSnapshot("1.6.46", $"1.6.46+{CommitA}");

        Assert.Equal(InstallComparison.Different, InstallVersionComparer.Compare(installed, package));
    }

    [Fact]
    public void StaleVersionFileWithSameCommitIsSameBuild()
    {
        // VERSION da instalacao ficou para tras, mas os binarios ja sao os do pacote.
        var installed = new InstallVersionSnapshot("1.6.40", $"1.6.46+{CommitA}");
        var package = new InstallVersionSnapshot("1.6.46", $"1.6.46+{CommitA}");

        Assert.Equal(InstallComparison.SameBuild, InstallVersionComparer.Compare(installed, package));
    }

    [Fact]
    public void LaggingProductVersionNumberDoesNotHideNewBuild()
    {
        // csproj nao bumpado junto da tag: o numero do exe e igual, o commit nao.
        var installed = new InstallVersionSnapshot("1.6.37", $"1.6.35+{CommitB}");
        var package = new InstallVersionSnapshot("1.6.38", $"1.6.35+{CommitA}");

        Assert.Equal(InstallComparison.Different, InstallVersionComparer.Compare(installed, package));
    }

    [Theory]
    [InlineData("1.6.46", "1.6.46", true)]
    [InlineData("1.6.45", "1.6.46", false)]
    [InlineData(null, "1.6.46", false)]
    public void WithoutCommitFallsBackToVersionFile(string? installedLabel, string packageLabel, bool expectSameBuild)
    {
        var installed = new InstallVersionSnapshot(installedLabel, "1.6.46");
        var package = new InstallVersionSnapshot(packageLabel, $"1.6.46+{CommitA}");

        var expected = expectSameBuild ? InstallComparison.SameBuild : InstallComparison.Different;
        Assert.Equal(expected, InstallVersionComparer.Compare(installed, package));
    }

    [Theory]
    [InlineData("1.6.46", "1.6.45")]
    [InlineData("1.6.100", "1.6.99")]
    [InlineData("1.6.40", "1.6.45")] // VERSION atrasado: os binarios instalados sao 1.6.46
    public void OlderPackageIsRefusedEvenWithAnotherCommit(string installedLabel, string packageLabel)
    {
        var installed = new InstallVersionSnapshot(installedLabel, $"1.6.46+{CommitB}");
        var package = new InstallVersionSnapshot(packageLabel, $"{packageLabel}+{CommitA}");

        var expected = InstallVersionComparer.Compare(installed, package);

        Assert.Equal(InstallComparison.Older, expected);
    }

    [Fact]
    public void OlderPackageWithoutCommitIsRefused()
    {
        var installed = new InstallVersionSnapshot("1.6.46", "1.6.46");
        var package = new InstallVersionSnapshot("1.6.45", "1.6.45");

        Assert.Equal(InstallComparison.Older, InstallVersionComparer.Compare(installed, package));
    }

    [Theory]
    [InlineData(null, null, "1.6.46", "1.6.46")]
    [InlineData("1.6.46", "1.6.46", "unknown", "0.1.0-dev")]
    [InlineData("1.6.46", "1.6.46", null, null)]
    public void UnknownVersionIsRefused(string? installedLabel, string? installedProduct, string? packageLabel, string? packageProduct)
    {
        var installed = new InstallVersionSnapshot(installedLabel, installedProduct);
        var package = new InstallVersionSnapshot(packageLabel, packageProduct);

        Assert.Equal(InstallComparison.Older, InstallVersionComparer.Compare(installed, package));
    }

    [Fact]
    public void SameCommitRepairIsAllowedEvenIfNumbersDisagree()
    {
        var installed = new InstallVersionSnapshot("1.6.47", $"1.6.47+{CommitA}");
        var package = new InstallVersionSnapshot("1.6.46", $"1.6.46+{CommitA}");

        Assert.Equal(InstallComparison.SameBuild, InstallVersionComparer.Compare(installed, package));
    }

    [Fact]
    public void DescribeShowsShortCommit()
    {
        var snapshot = new InstallVersionSnapshot("1.6.46", $"1.6.46+{CommitA}");

        Assert.Equal("1.6.46 (build a3b4994)", snapshot.Describe());
        Assert.Equal("desconhecida", new InstallVersionSnapshot(null, null).Describe());
    }
}
