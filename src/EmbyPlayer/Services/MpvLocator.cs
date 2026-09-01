namespace EmbyPlayer.Services;

public static class MpvLocator
{
    public static string? Find(SettingsService settings)
    {
        var custom = settings.Current.MpvPath;
        if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
        {
            return custom;
        }

        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "mpv.exe"),
            // 开发目录布局：<root>/src/EmbyPlayer/bin/... → <root>/tools/mpv/mpv.exe
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "mpv", "mpv.exe")),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "mpv", "mpv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", "mpv.exe"),
            Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "scoop", "apps", "mpv", "current", "mpv.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "chocolatey", "bin", "mpv.exe")
        };

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        candidates.AddRange(pathDirs.Select(d => Path.Combine(d, "mpv.exe")));

        return candidates.FirstOrDefault(File.Exists);
    }
}
