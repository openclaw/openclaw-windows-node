namespace OpenClaw.Shared;

internal interface IDeviceIdentityFileSystem
{
    bool IdentityFileExists(string path);
    bool DirectoryExists(string path);
    void CreateDirectory(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string content);
    void MoveFileNoOverwrite(string source, string destination);
    bool FileExists(string path);
    void DeleteFile(string path);
}

internal sealed class DeviceIdentityFileSystem : IDeviceIdentityFileSystem
{
    public static DeviceIdentityFileSystem Instance { get; } = new();

    private DeviceIdentityFileSystem()
    {
    }

    public bool IdentityFileExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);

    public void MoveFileNoOverwrite(string source, string destination) =>
        File.Move(source, destination);

    public bool FileExists(string path) => File.Exists(path);

    public void DeleteFile(string path) => File.Delete(path);
}
