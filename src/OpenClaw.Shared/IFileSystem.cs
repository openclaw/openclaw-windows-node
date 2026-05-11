namespace OpenClaw.Shared;

/// <summary>
/// Filesystem abstraction for testability. Production uses <see cref="RealFileSystem"/>.
/// </summary>
public interface IFileSystem
{
    bool FileExists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string content);
    void CreateDirectory(string path);
    bool DirectoryExists(string path);
    void CopyFile(string source, string destination, bool overwrite);
}

/// <summary>
/// Production filesystem implementation delegating to <see cref="System.IO.File"/>
/// and <see cref="System.IO.Directory"/>.
/// </summary>
public sealed class RealFileSystem : IFileSystem
{
    public static readonly RealFileSystem Instance = new();

    public bool FileExists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string content) => File.WriteAllText(path, content);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public bool DirectoryExists(string path) => Directory.Exists(path);
    public void CopyFile(string source, string destination, bool overwrite) =>
        File.Copy(source, destination, overwrite);
}
