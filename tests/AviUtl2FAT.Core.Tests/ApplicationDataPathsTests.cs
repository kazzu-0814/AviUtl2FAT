using AviUtl2FAT.Core;
using Xunit;

namespace AviUtl2FAT.Core.Tests;

public sealed class ApplicationDataPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "AltFactorPathTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void CleanInstallUsesAltFactorWithoutCreatingLegacyDirectory()
    {
        var paths = ApplicationDataPaths.Detect(_root);

        Assert.Equal(ApplicationDataEnvironment.NewInstall, paths.Environment);
        Assert.Equal(Path.Combine(_root, ApplicationDataPaths.AltFactorDirectoryName), paths.RootDirectory);
        Assert.False(Directory.Exists(Path.Combine(_root, ApplicationDataPaths.LegacyDirectoryName)));
    }

    [Fact]
    public void ExistingFatDirectoryIsPreserved()
    {
        var legacy = Path.Combine(_root, ApplicationDataPaths.LegacyDirectoryName);
        Directory.CreateDirectory(legacy);

        var paths = ApplicationDataPaths.Detect(_root);

        Assert.True(paths.IsLegacy);
        Assert.Equal(legacy, paths.RootDirectory);
    }

    [Fact]
    public void AltFactorTakesPriorityWhenBothDirectoriesExist()
    {
        var altFactor = Path.Combine(_root, ApplicationDataPaths.AltFactorDirectoryName);
        Directory.CreateDirectory(altFactor);
        Directory.CreateDirectory(Path.Combine(_root, ApplicationDataPaths.LegacyDirectoryName));

        var paths = ApplicationDataPaths.Detect(_root);

        Assert.Equal(ApplicationDataEnvironment.AltFactor, paths.Environment);
        Assert.Equal(altFactor, paths.RootDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
