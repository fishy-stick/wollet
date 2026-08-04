namespace Wollet.Client;

internal sealed class WindowsPaths
{
    public WindowsPaths()
    {
        ConfigDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Wollet");
        ConfigFile = Path.Combine(ConfigDirectory, "client.json");
        InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Wollet");
        InstalledExecutable = Path.Combine(InstallDirectory, "wollet-client.exe");
    }

    public string ConfigDirectory { get; }

    public string ConfigFile { get; }

    public string InstallDirectory { get; }

    public string InstalledExecutable { get; }
}
