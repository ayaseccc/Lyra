using System.IO;
using Microsoft.Win32;

namespace Player.App;

/// <summary>
/// P6 文件关联：HKCU（免管理员）注册九种音频格式的"打开方式"（ProgID + 命令 + 默认图标）。
/// 每次启动幂等注册：便携版移动后命令路径自动刷新。三种路径统一走命令行参数：
/// 双击（资源管理器"打开方式"）/ 多选打开 / 拖到 exe 图标。
/// </summary>
internal static class FileAssociation
{
    private static readonly string[] Extensions =
        { ".mp3", ".flac", ".m4a", ".ape", ".wv", ".ogg", ".opus", ".wav", ".aiff" };

    private const string ProgIdBase = "Lyra.Audio.";
    private const string ClassesRoot = @"Software\Classes";
    private const string CapabilitiesPath = @"Software\Lyra\Capabilities";
    private const string RegisteredApplicationName = "Lyra";
    private const string LegacyProgIdBase = "PlayerMusicPlayer";
    private const string ExplorerFileExtsRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";

    public static void Register()
    {
        try
        {
            // 用户实机反馈「没有注册类」：此前 ClassesRoot 丢了反斜杠（SoftwareClasses），
            // 注册落到了错误键位。这里先完整写 ProgID，再写 .ext 关联；最后刷新关联缓存。
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                      ?? Environment.ProcessPath;
            Serilog.Log.Information("文件关联注册开始：exe={Exe}", exe);
            if (string.IsNullOrEmpty(exe)) return;

            foreach (var ext in Extensions)
            {
                var progId = ProgIdBase + ext.TrimStart('.').ToUpperInvariant();
                var progIdPath = ClassesRoot + @"\" + progId;

                // 1) ProgID：描述 + shell\open 默认动词 + 命令 + 图标（完整再关联）
                using (var key = Registry.CurrentUser.CreateSubKey(progIdPath))
                    key.SetValue(string.Empty, "Lyra 音频文件");
                using (var key = Registry.CurrentUser.CreateSubKey(progIdPath + @"\shell\open"))
                    key.SetValue(string.Empty, "&Open");
                using (var key = Registry.CurrentUser.CreateSubKey(progIdPath + @"\shell\open\command"))
                    key.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
                using (var key = Registry.CurrentUser.CreateSubKey(progIdPath + @"\DefaultIcon"))
                    key.SetValue(string.Empty, $"\"{exe}\",0");

                // 2) .ext → ProgID（+ OpenWithProgids，供「打开方式」对话框识别）
                using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + @"\" + ext))
                {
                    // 不覆盖用户已选择的其他处理器；旧 Player 值仅作为改名过渡时替换。
                    var current = key.GetValue(string.Empty) as string;
                    if (IsLyraOrLegacyProgId(current, ext))
                        key.SetValue(string.Empty, progId);
                }
                using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + @"\" + ext + @"\OpenWithProgids"))
                    key.SetValue(progId, string.Empty);
            }

            // 3) 注册为 Windows 的“默认应用”候选。仅写 .ext 默认值在
            // Windows 10/11 遇到已有 UserChoice 时会被忽略；Capabilities +
            // RegisteredApplications 才能让 Lyra 出现在“打开方式/默认应用”列表。
            var applicationKeyPath = ClassesRoot + @"\Applications\Lyra.exe";
            using (var applicationKey = Registry.CurrentUser.CreateSubKey(applicationKeyPath))
            {
                applicationKey.SetValue("FriendlyAppName", "Lyra");
                applicationKey.SetValue("ApplicationDescription", "本地音乐播放器");
                applicationKey.DeleteValue("DefaultIcon", throwOnMissingValue: false);
            }
            using (var applicationIcon = Registry.CurrentUser.CreateSubKey(applicationKeyPath + @"\DefaultIcon"))
                applicationIcon.SetValue(string.Empty, $"\"{exe}\",0");
            using (var commandKey = Registry.CurrentUser.CreateSubKey(applicationKeyPath + @"\shell\open\command"))
                commandKey.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
            using (var supportedTypes = Registry.CurrentUser.CreateSubKey(applicationKeyPath + @"\SupportedTypes"))
            {
                foreach (var ext in Extensions)
                    supportedTypes.SetValue(ext, string.Empty);
            }

            using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
            {
                capabilities.SetValue("ApplicationName", "Lyra");
                capabilities.SetValue("ApplicationDescription", "本地音乐播放器");
            }
            using (var fileAssociations = Registry.CurrentUser.CreateSubKey(CapabilitiesPath + @"\FileAssociations"))
            {
                foreach (var ext in Extensions)
                {
                    var progId = ProgIdBase + ext.TrimStart('.').ToUpperInvariant();
                    fileAssociations.SetValue(ext, progId);
                }
            }
            using (var registeredApplications = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
                registeredApplications.SetValue(RegisteredApplicationName, CapabilitiesPath);

            // 4) 清理旧 Player 候选。这一步必须放在 Lyra 候选完整注册之后；
            // Windows 的 UserChoice 含哈希且不能由应用静默改写，旧值仍被选中时保留隐藏兼容桥，避免升级后“没有注册类”。
            RemoveLegacyRegistration(exe);

            // 5) 记录 Windows 当前的用户选择，便于诊断旧处理器/DelegateExecute
            // 导致的“没有注册类”；不覆盖用户已有的其他默认播放器。
            using (var userChoice = Registry.CurrentUser.OpenSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\.flac\UserChoice"))
            {
                var selected = userChoice?.GetValue("ProgId") as string;
                if (!string.IsNullOrWhiteSpace(selected)
                    && !selected.StartsWith(ProgIdBase, StringComparison.OrdinalIgnoreCase))
                {
                    Serilog.Log.Information("Windows 当前 .flac 默认处理器为 {ProgId}；Lyra 已注册到‘打开方式’，需在系统默认应用中选择 Lyra 才会覆盖该用户选择", selected);
                }
            }

            // 6) 通知资源管理器刷新关联缓存（否则双击仍走旧关联/报错）
            NotifyAssocChanged();

            Serilog.Log.Information("文件关联注册完成（{Count} 个格式）", Extensions.Length);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "文件关联注册失败（不影响运行）");
        }
    }

    private static void RemoveLegacyRegistration(string exe)
    {
        var keepLegacyApplicationBridge = false;

        foreach (var ext in Extensions)
        {
            try
            {
                var oldProgIds = new[]
                {
                    LegacyProgIdBase + ext.TrimStart('.').ToUpperInvariant(),
                    "Player.Audio." + ext.TrimStart('.').ToUpperInvariant()
                };

                var choiceState = ReadUserChoiceProgId(ext, out var selected);
                if (choiceState == UserChoiceState.Unreadable)
                {
                    // 受保护的 UserChoice 读取失败时只能保守保留桥，绝不误删用户原有处理器。
                    foreach (var oldProgId in oldProgIds)
                        RegisterLegacyProgIdBridge(oldProgId, exe);
                    keepLegacyApplicationBridge = true;
                    Serilog.Log.Warning(
                        "无法读取 {Extension} 的 Windows UserChoice，保留旧关联兼容桥",
                        ext);
                    continue;
                }

                // 在任何可写清理操作之前记住旧应用选择，保证异常分支也不会删掉它的兼容桥。
                if (string.Equals(selected, @"Applications\Player.exe", StringComparison.OrdinalIgnoreCase))
                    keepLegacyApplicationBridge = true;

                using (var openWith = Registry.CurrentUser.OpenSubKey(
                           ClassesRoot + @"\" + ext + @"\OpenWithProgids", writable: true))
                {
                    if (openWith is not null)
                    {
                        foreach (var oldProgId in oldProgIds)
                            openWith.DeleteValue(oldProgId, throwOnMissingValue: false);
                    }
                }

                foreach (var oldProgId in oldProgIds)
                {
                    if (string.Equals(selected, oldProgId, StringComparison.OrdinalIgnoreCase))
                    {
                        RegisterLegacyProgIdBridge(oldProgId, exe);
                        Serilog.Log.Information(
                            "Windows 仍以旧 ProgID 打开 {Extension}；保留指向 Lyra 的兼容桥，用户重新选择 Lyra 后将自动清理",
                            ext);
                    }
                    else
                    {
                        Registry.CurrentUser.DeleteSubKeyTree(
                            ClassesRoot + @"\" + oldProgId,
                            throwOnMissingSubKey: false);
                    }
                }

                RemoveLegacyExplorerProgIds(ext, oldProgIds);
            }
            catch (Exception ex)
            {
                // 旧键清理是尽力而为；不得影响前面已完成的 Lyra 注册。
                Serilog.Log.Warning(ex, "清理旧 {Extension} 文件关联失败（保留现有关联）", ext);
            }
        }

        try
        {
            if (keepLegacyApplicationBridge)
                RegisterLegacyApplicationBridge(exe);
            else
                Registry.CurrentUser.DeleteSubKeyTree(
                    ClassesRoot + @"\Applications\Player.exe",
                    throwOnMissingSubKey: false);

            Registry.CurrentUser.DeleteSubKeyTree(ClassesRoot + @"\PlayerMusicPlayer", throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\PlayerMusicPlayer", throwOnMissingSubKey: false);
            using var registeredApplications = Registry.CurrentUser.OpenSubKey(
                @"Software\RegisteredApplications", writable: true);
            registeredApplications?.DeleteValue("Player", throwOnMissingValue: false);
            registeredApplications?.DeleteValue("PlayerMusicPlayer", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "清理旧 Player 全局注册项失败（不影响 Lyra）");
        }
    }

    private enum UserChoiceState
    {
        Missing,
        Present,
        Unreadable
    }

    private static UserChoiceState ReadUserChoiceProgId(string extension, out string? progId)
    {
        try
        {
            using var userChoice = Registry.CurrentUser.OpenSubKey(
                ExplorerFileExtsRoot + @"\" + extension + @"\UserChoice",
                writable: false);
            if (userChoice is null)
            {
                progId = null;
                return UserChoiceState.Missing;
            }

            progId = userChoice.GetValue("ProgId") as string;
            return string.IsNullOrWhiteSpace(progId)
                ? UserChoiceState.Unreadable
                : UserChoiceState.Present;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "读取 {Extension} 的 Windows UserChoice 失败", extension);
            progId = null;
            return UserChoiceState.Unreadable;
        }
    }

    private static bool IsLyraOrLegacyProgId(string? progId, string extension)
    {
        if (string.IsNullOrWhiteSpace(progId)) return true;
        var suffix = extension.TrimStart('.').ToUpperInvariant();
        return string.Equals(progId, ProgIdBase + suffix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(progId, LegacyProgIdBase + suffix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(progId, "Player.Audio." + suffix, StringComparison.OrdinalIgnoreCase)
               || string.Equals(progId, @"Applications\Player.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static void RemoveLegacyExplorerProgIds(
        string extension,
        IReadOnlyCollection<string> oldProgIds)
    {
        // Explorer\FileExts\OpenWithProgids 是系统缓存，只删已知的旧候选值；不碰 OpenWithList/MRU。
        // UserChoice 不可读时上方会直接保留，旧选择本身也由桥保护。
        try
        {
            var path = ExplorerFileExtsRoot + @"\" + extension + @"\OpenWithProgids";
            using var key = Registry.CurrentUser.OpenSubKey(path, writable: true);
            if (key is null) return;
            foreach (var oldProgId in oldProgIds)
                key.DeleteValue(oldProgId, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "清理 Explorer 的旧 {Extension} OpenWithProgids 失败", extension);
        }
    }

    private static void RegisterLegacyProgIdBridge(string progId, string exe)
    {
        var path = ClassesRoot + @"\" + progId;
        using (var key = Registry.CurrentUser.CreateSubKey(path))
            key.SetValue(string.Empty, "Lyra 音频文件");
        using (var key = Registry.CurrentUser.CreateSubKey(path + @"\shell\open"))
            key.SetValue(string.Empty, "&Open");
        using (var key = Registry.CurrentUser.CreateSubKey(path + @"\shell\open\command"))
            key.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
        using (var key = Registry.CurrentUser.CreateSubKey(path + @"\DefaultIcon"))
            key.SetValue(string.Empty, $"\"{exe}\",0");
    }

    private static void RegisterLegacyApplicationBridge(string exe)
    {
        var path = ClassesRoot + @"\Applications\Player.exe";
        using (var key = Registry.CurrentUser.CreateSubKey(path))
        {
            key.SetValue("FriendlyAppName", "Lyra");
            key.SetValue("ApplicationDescription", "Lyra 兼容打开项");
            key.SetValue("NoOpenWith", string.Empty);
            key.DeleteValue("DefaultIcon", throwOnMissingValue: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(path + @"\SupportedTypes", throwOnMissingSubKey: false);
        using (var icon = Registry.CurrentUser.CreateSubKey(path + @"\DefaultIcon"))
            icon.SetValue(string.Empty, $"\"{exe}\",0");
        using (var command = Registry.CurrentUser.CreateSubKey(path + @"\shell\open\command"))
            command.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
    }

    /// <summary>SHChangeNotify(SHCNE_ASSOCCHANGED)：让资源管理器立即采用新关联。</summary>
    private static void NotifyAssocChanged()
    {
        try
        {
            const uint ShcnfIdlist = 0x0000;
            const uint ShcneAssocchanged = 0x08000000;
            NativeMethods.ShChangeNotify(ShcneAssocchanged, ShcnfIdlist, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "关联缓存刷新通知失败（资源管理器重启后仍会生效）");
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport(
            "shell32.dll", EntryPoint = "SHChangeNotify", ExactSpelling = true,
            CallingConvention = System.Runtime.InteropServices.CallingConvention.Winapi)]
        internal static extern void ShChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
