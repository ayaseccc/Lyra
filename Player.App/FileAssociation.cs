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
    private const string ClassesRoot = @"Software\Classes";

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
                    key.SetValue(string.Empty, "Player 音频文件");
                using (var key = Registry.CurrentUser.CreateSubKey(progIdPath + @"\shell\open"))
                    key.SetValue(string.Empty, "&Open");
                using (var key = Registry.CurrentUser.CreateSubKey(progIdPath + @"\shell\open\command"))
                    key.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
                using (var key = Registry.CurrentUser.CreateSubKey(progIdPath + @"\DefaultIcon"))
                    key.SetValue(string.Empty, $"\"{exe}\",0");

                // 2) .ext → ProgID（+ OpenWithProgids，供「打开方式」对话框识别）
                using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + @"\" + ext))
                    key.SetValue(string.Empty, progId);
                using (var key = Registry.CurrentUser.CreateSubKey(ClassesRoot + @"\" + ext + @"\OpenWithProgids"))
                    key.SetValue(progId, string.Empty);
            }

            // 3) 通知资源管理器刷新关联缓存（否则双击仍走旧关联/报错）
            NotifyAssocChanged();

            Serilog.Log.Information("文件关联注册完成（{Count} 个格式）", Extensions.Length);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "文件关联注册失败（不影响运行）");
        }
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
        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        internal static extern void ShChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
    }
}
