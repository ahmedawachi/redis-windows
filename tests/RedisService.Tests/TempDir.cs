namespace RedisService.Tests;

internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "redissvc-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name, string content)
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public void Dispose()
    {
        try
        {
            foreach (var d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories))
                new DirectoryInfo(d).Attributes = FileAttributes.Normal;
            if (!OperatingSystem.IsWindows())
                foreach (var d in Directory.EnumerateDirectories(Path, "*", SearchOption.AllDirectories).Prepend(Path))
                    System.IO.File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
