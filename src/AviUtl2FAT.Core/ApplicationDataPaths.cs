namespace AviUtl2FAT.Core;

public enum ApplicationDataEnvironment
{
    AltFactor,
    LegacyFat,
    NewInstall
}

/// <summary>
/// Selects one persistent-data root for the whole process. Existing FAT users
/// remain on their original directory; a clean installation uses AltFactor.
/// Detection itself is read-only and never creates or migrates a directory.
/// </summary>
public sealed record ApplicationDataPaths(string RootDirectory, ApplicationDataEnvironment Environment)
{
    public const string AltFactorDirectoryName = "AviUtl2AltFactor";
    public const string LegacyDirectoryName = "AviUtl2FAT";

    private static readonly Lazy<ApplicationDataPaths> CurrentValue = new(() =>
        Detect(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData)));

    public static ApplicationDataPaths Current => CurrentValue.Value;
    public bool IsLegacy => Environment == ApplicationDataEnvironment.LegacyFat;

    public string File(string fileName) => Path.Combine(RootDirectory, fileName);
    public string Directory(string directoryName) => Path.Combine(RootDirectory, directoryName);

    public static ApplicationDataPaths Detect(string localApplicationData)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localApplicationData);
        var altFactor = Path.Combine(localApplicationData, AltFactorDirectoryName);
        var legacy = Path.Combine(localApplicationData, LegacyDirectoryName);

        if (System.IO.Directory.Exists(altFactor))
            return new(altFactor, ApplicationDataEnvironment.AltFactor);
        if (System.IO.Directory.Exists(legacy))
            return new(legacy, ApplicationDataEnvironment.LegacyFat);
        return new(altFactor, ApplicationDataEnvironment.NewInstall);
    }
}
