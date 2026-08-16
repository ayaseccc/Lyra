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

    private const string ProgIdBase = "PlayerMusicPlayer";
    private const string ClassesRoot = @"SoftwareClasses";

    public static void Register()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            Serilog.Log.Information("文件关联注册开始：exe={Exe}", exe);
            if (string.IsNullOrEmpty(exe)) return;

            foreach (var ext in Extensions)
            {
                var progId = ProgIdBase + ext.TrimStart('.').ToUpperInvariant();

                // .ext → ProgID
                using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + @"" + ext))
                    key.SetValue(string.Empty, progId);

                // ProgID → 描述
                using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + @"" + progId))
                    key.SetValue(string.Empty, "Player 音频文件");

                // open command（多选/拖到 exe 时系统会逐个传参）
                using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + @"" + progId + @"shellopencommand"))
                    key.SetValue(string.Empty, $"\"{exe}\" \"%1\"");

                // 默认图标 = 播放器 exe 自带图标
                using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + @"" + progId + @"DefaultIcon"))
                    key.SetValue(string.Empty, $"\"{exe}\",0");
            }
            Serilog.Log.Information("文件关联注册完成（{Count} 个格式）", Extensions.Length);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "文件关联注册失败（不影响运行）");
        }
    }
}
