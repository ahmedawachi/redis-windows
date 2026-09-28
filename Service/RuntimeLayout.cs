namespace RedisService.Service;

/// <summary>Which POSIX runtime redis-server was built against; decides the root and mount layout.</summary>
public enum RuntimeFlavor
{
    /// <summary>No msys-2.0.dll / cygwin1.dll next to redis-server (a native Unix build, e.g. in tests).</summary>
    Native,
    Msys2,
    Cygwin,
}

/// <summary>
/// Path rules for the MSYS2 / Cygwin runtimes. Windows-style paths are always passed as "C:/dir/file"
/// (forward slashes, drive kept): both runtimes accept them without consulting the mount table,
/// unlike "/cygdrive/c/..." which breaks when an fstab changes the cygdrive prefix (upstream issue #77).
/// All methods are pure string manipulation so they can be tested on any OS.
/// </summary>
public static class RuntimeLayout
{
    public static RuntimeFlavor DetectFlavor(string redisServerDirectory)
    {
        if (File.Exists(Path.Combine(redisServerDirectory, "msys-2.0.dll"))) return RuntimeFlavor.Msys2;
        if (File.Exists(Path.Combine(redisServerDirectory, "cygwin1.dll"))) return RuntimeFlavor.Cygwin;
        return RuntimeFlavor.Native;
    }

    /// <summary>Converts an absolute Windows path to the form passed to redis-server: C:/a/b, //server/share/a.</summary>
    public static string ToRedisPath(string windowsPath)
    {
        var p = windowsPath.Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal)) p = p[4..];
        var trimmed = p.Length > 3 ? p.TrimEnd('\\') : p;
        if (trimmed.Length == 0) trimmed = p;
        return trimmed.Replace('\\', '/');
    }

    /// <summary>True for "C:\x", "C:/x" and UNC paths. Independent of the OS the code runs on.</summary>
    public static bool IsWindowsAbsolute(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// Combines a Windows base directory with a relative path and normalises "." and "..", without touching the file system.
    /// </summary>
    public static string CombineWindows(string baseDirectory, string relative)
    {
        if (IsWindowsAbsolute(relative)) return NormalizeWindows(relative);
        return NormalizeWindows(baseDirectory.TrimEnd('\\', '/') + "\\" + relative);
    }

    public static string NormalizeWindows(string path)
    {
        var p = path.Replace('/', '\\');
        string prefix;
        string rest;
        if (p.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var parts = p[2..].Split('\\', 3);
            prefix = parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : p;
            rest = parts.Length == 3 ? parts[2] : "";
        }
        else if (p.Length >= 2 && p[1] == ':')
        {
            prefix = char.ToUpperInvariant(p[0]) + ":";
            rest = p[2..];
        }
        else
        {
            prefix = "";
            rest = p;
        }

        var stack = new List<string>();
        foreach (var seg in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(seg);
        }
        return prefix + "\\" + string.Join('\\', stack);
    }

    /// <summary>
    /// The runtime's POSIX root ("/") for a redis-server installed in <paramref name="installDirectory"/>.
    /// MSYS2 strips the DLL name plus two folders (mm/cygheap.cc "Back two folders to get root"), Cygwin one.
    /// It never goes above the drive or share root.
    /// </summary>
    public static string PosixRoot(string installDirectory, RuntimeFlavor flavor)
    {
        var dir = NormalizeWindows(installDirectory);
        var levels = flavor switch { RuntimeFlavor.Msys2 => 2, RuntimeFlavor.Cygwin => 1, _ => 0 };
        for (var i = 0; i < levels; i++)
        {
            var parent = ParentOf(dir);
            if (parent is null) break;
            dir = parent;
        }
        return dir;
    }

    private static string? ParentOf(string normalized)
    {
        var t = normalized.TrimEnd('\\');
        var rootLen = RootLength(t);
        if (t.Length <= rootLen) return null;
        var idx = t.LastIndexOf('\\');
        return idx <= rootLen ? t[..rootLen] + "\\" : t[..idx];
    }

    private static int RootLength(string normalized)
    {
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            var afterServer = normalized.IndexOf('\\', 2);
            var afterShare = afterServer < 0 ? -1 : normalized.IndexOf('\\', afterServer + 1);
            return afterShare < 0 ? normalized.Length : afterShare;
        }
        return normalized.Length >= 2 && normalized[1] == ':' ? 2 : 0;
    }

    /// <summary>
    /// Windows folders whose POSIX name is remapped by the runtime's automatic mounts, so a RELATIVE path
    /// resolved through the POSIX working directory lands somewhere else. MSYS2 mounts &lt;root&gt;\usr\bin at /bin
    /// (mount.cc), so files under &lt;root&gt;\bin are unreachable by relative path; Cygwin mounts &lt;root&gt;\bin at
    /// /usr/bin and &lt;root&gt;\lib at /usr/lib, so &lt;root&gt;\usr\bin and &lt;root&gt;\usr\lib are the traps.
    /// </summary>
    public static IReadOnlyList<string> RemappedFolders(string installDirectory, RuntimeFlavor flavor)
    {
        var root = PosixRoot(installDirectory, flavor).TrimEnd('\\');
        return flavor switch
        {
            RuntimeFlavor.Msys2 => [root + @"\bin"],
            RuntimeFlavor.Cygwin => [root + @"\usr\bin", root + @"\usr\lib"],
            _ => [],
        };
    }

    /// <summary>True when <paramref name="windowsPath"/> is inside one of <see cref="RemappedFolders"/>.</summary>
    public static bool IsInRemappedFolder(string windowsPath, string installDirectory, RuntimeFlavor flavor)
    {
        var p = NormalizeWindows(windowsPath).TrimEnd('\\');
        foreach (var folder in RemappedFolders(installDirectory, flavor))
        {
            if (p.Equals(folder, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Translates a path written in redis.conf into a Windows path the wrapper can open (to read an include or the
    /// logfile). Handles C:/..., /cygdrive/c/... and root-relative POSIX paths (no fstab: the default layout).
    /// Relative paths are resolved against <paramref name="workingDirectory"/>.
    /// </summary>
    public static string ConfPathToWindows(string confPath, string workingDirectory, string installDirectory, RuntimeFlavor flavor)
    {
        if (IsWindowsAbsolute(confPath)) return NormalizeWindows(confPath);
        if (confPath.StartsWith('/'))
        {
            if (flavor == RuntimeFlavor.Native) return confPath;
            if (confPath.StartsWith("/cygdrive/", StringComparison.Ordinal) && confPath.Length >= 11 && char.IsAsciiLetter(confPath[10])
                && (confPath.Length == 11 || confPath[11] == '/'))
                return NormalizeWindows(confPath[10] + ":" + (confPath.Length > 11 ? confPath[11..] : "\\"));

            var root = PosixRoot(installDirectory, flavor).TrimEnd('\\');
            var posix = confPath;
            if (flavor == RuntimeFlavor.Msys2 && (posix == "/bin" || posix.StartsWith("/bin/", StringComparison.Ordinal)))
                posix = "/usr" + posix;
            else if (flavor == RuntimeFlavor.Cygwin && (posix.StartsWith("/usr/bin", StringComparison.Ordinal) || posix.StartsWith("/usr/lib", StringComparison.Ordinal)))
                posix = posix[4..];
            return NormalizeWindows(root + posix.Replace('/', '\\'));
        }
        return flavor == RuntimeFlavor.Native
            ? Path.GetFullPath(confPath, workingDirectory)
            : CombineWindows(workingDirectory, confPath);
    }

    /// <summary>True when a path from redis.conf is relative (so Redis resolves it against its POSIX working directory).</summary>
    public static bool IsConfPathRelative(string confPath) => !IsWindowsAbsolute(confPath) && !confPath.StartsWith('/');
}
