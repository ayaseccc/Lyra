using ManagedBass;
using Serilog;

namespace Player.Core.Audio;

/// <summary>BASS 初始化失败。</summary>
public sealed class BassInitException : Exception
{
    public BassInitException(string message) : base(message) { }
}

/// <summary>
/// BASS 运行时的全局初始化与格式插件加载（PLAN 第 2、4 节）。
/// P0 使用 BASS 默认输出（DirectSound）；ASIO / WASAPI 后端在 P2 通过 IOutputBackend 接入。
/// </summary>
public static class BassRuntime
{
    private static readonly object Gate = new();
    private static readonly List<int> PluginHandles = new();
    private static readonly List<string> LoadedList = new();
    private static readonly List<string> MissingList = new();

    /// <summary>
    /// 需要 PluginLoad 的格式插件（仓库 native/x64 里提供的这几个）。
    /// 其它自行放进目录的 bass*.dll 由下面的兜底扫描自动加载，不列在这里以免每次启动都报缺失。
    /// 注意：bassasio / bassmix / basswasapi 是独立库而非"插件"，绝不能走 PluginLoad。
    /// </summary>
    private static readonly string[] PluginCandidates =
    {
        "bassflac.dll",   // FLAC / Ogg FLAC
        "bassalac.dll",   // ALAC
        "bassape.dll",    // Monkey's Audio
        "basswv.dll",     // WavPack
        "bassopus.dll"    // Opus
    };

    /// <summary>这些是核心库/编码器，不是格式插件，扫描兜底时要排除。</summary>
    private static readonly HashSet<string> NotPlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        "bass.dll", "bassasio.dll", "bassmix.dll", "basswasapi.dll", "bassenc.dll",
        "bassenc_mp3.dll", "bassenc_flac.dll", "bassenc_ogg.dll", "bassenc_opus.dll",
        "bassloud.dll", "bass_ssl.dll", "bass_fx.dll"
    };

    public static bool IsInitialized { get; private set; }

    public static IReadOnlyList<string> LoadedPlugins => LoadedList;

    public static IReadOnlyList<string> MissingPlugins => MissingList;

    public static string BassVersion { get; private set; } = "?";

    public static string OutputDeviceName { get; private set; } = "?";

    /// <summary>
    /// 初始化默认输出设备并加载全部可用格式插件。失败抛 <see cref="BassInitException"/>。
    /// </summary>
    /// <param name="windowHandle">窗口句柄，可为 IntPtr.Zero（BASS 会使用桌面窗口）。</param>
    public static void Initialize(IntPtr windowHandle = default)
    {
        lock (Gate)
        {
            if (IsInitialized) return;

            // BASS defaults to 100 ms playback updates. A shorter period keeps the mixer DSP tap
            // supplied steadily on the ordinary DirectSound backend; decode/ASIO/WASAPI paths
            // are unaffected and the spectrum still never pulls data with ChannelGetData.
            Bass.UpdatePeriod = 20;

            // device = -1 表示系统默认输出设备
            if (!Bass.Init(-1, 44100, DeviceInitFlags.Default, windowHandle))
            {
                var error = Bass.LastError;
                Log.Error("Bass.Init 失败：{Error}", error);
                throw new BassInitException($"Bass.Init 失败：{error}");
            }

            // Init 一旦成功就立刻置位，保证后续任何异常都不会漏掉 Bass.Free()
            IsInitialized = true;

            try { BassVersion = Bass.Version.ToString() ?? "?"; }
            catch (Exception ex) { Log.Debug(ex, "读取 BASS 版本失败"); }

            try
            {
                if (Bass.GetDeviceInfo(Bass.CurrentDevice, out var deviceInfo))
                    OutputDeviceName = deviceInfo.Name ?? "?";
            }
            catch (Exception ex) { Log.Debug(ex, "读取输出设备信息失败"); }

            Log.Information("BASS 初始化成功：版本 {Version}，默认输出设备 {Device}", BassVersion, OutputDeviceName);

            LoadPlugins();
        }
    }

    private static void LoadPlugins()
    {
        LoadedList.Clear();
        MissingList.Clear();

        foreach (var name in PluginCandidates)
            TryLoadPlugin(Path.Combine(AppContext.BaseDirectory, name), name, recordMissing: true);

        // 兜底：把用户自己丢进目录里的其它 bass*.dll 也试着当插件加载
        try
        {
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "bass*.dll"))
            {
                var name = Path.GetFileName(file);
                if (NotPlugins.Contains(name)) continue;
                if (LoadedList.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if (PluginCandidates.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                TryLoadPlugin(file, name, recordMissing: false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "扫描附加插件时出错");
        }

        Log.Information("格式插件已加载 {Count} 个：{Plugins}", LoadedList.Count,
            LoadedList.Count == 0 ? "(无)" : string.Join(", ", LoadedList));
        if (MissingList.Count > 0)
            Log.Warning("以下插件缺失或加载失败：{Missing}", string.Join(", ", MissingList));
    }

    private static void TryLoadPlugin(string fullPath, string name, bool recordMissing)
    {
        if (!File.Exists(fullPath))
        {
            if (recordMissing) MissingList.Add(name);
            return;
        }

        int handle;
        try
        {
            handle = Bass.PluginLoad(fullPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "插件 {Plugin} 加载异常", name);
            if (recordMissing) MissingList.Add(name);
            return;
        }

        if (handle == 0)
        {
            Log.Warning("插件 {Plugin} 加载失败：{Error}", name, Bass.LastError);
            if (recordMissing) MissingList.Add(name);
            return;
        }

        PluginHandles.Add(handle);
        LoadedList.Add(name);
        Log.Information("插件已加载：{Plugin}", name);
    }

    /// <summary>释放插件与 BASS 本身。退出前必须调用，否则可能留下驻留线程。</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            if (!IsInitialized) return;

            foreach (var handle in PluginHandles)
            {
                try { Bass.PluginFree(handle); }
                catch (Exception ex) { Log.Debug(ex, "PluginFree 失败"); }
            }
            PluginHandles.Clear();
            LoadedList.Clear();

            try { Bass.Free(); }
            catch (Exception ex) { Log.Warning(ex, "Bass.Free 失败"); }

            IsInitialized = false;
            Log.Information("BASS 已释放");
        }
    }
}
