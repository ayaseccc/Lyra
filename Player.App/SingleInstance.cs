using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace Player.App;

/// <summary>
/// P6 单实例：Mutex 判定首个实例；第二实例把命令行文件（双击/多选/拖到 exe）
/// 经命名管道转交运行实例后退出。运行实例在 UI 线程回调处理（导入曲库并播放）。
/// </summary>
internal static class SingleInstance
{
    private static readonly string InstanceScope = BuildInstanceScope();
    private static readonly string PipeName = "PlayerMusicPlayer_InstancePipe_v1_" + InstanceScope;
    private static readonly string MutexName = @"Local\PlayerMusicPlayer_" + InstanceScope;

    private static string BuildInstanceScope()
    {
        // Keep both kernel objects in the same user/session scope. A machine-wide
        // pipe name could otherwise hand one user's files to another session.
        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
                return sid + "_" + sessionId;
        }
        catch
        {
            // Fall back to the account name on restricted hosts.
        }

        return Environment.UserName + "_" + sessionId;
    }

    /// <summary>首次实例成功获得互斥体返回 true；第二实例返回 false（调用方应转交文件后退出）。</summary>
    public static bool TryAcquire(out Mutex? mutex)
    {
        mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return createdNew;
    }

    /// <summary>第二实例：把文件清单发给运行实例。发送失败（运行实例未就绪等）静默。</summary>
    public static bool ForwardFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return false;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                client.Connect(TimeSpan.FromMilliseconds(700));
                var payload = string.Join("\n", files);
                var bytes = Encoding.UTF8.GetBytes(payload);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                return true;
            }
            catch when (DateTime.UtcNow < deadline)
            {
                // 首实例可能已拿到 Mutex 但还没来得及创建 ServerStream；短暂重试
                // 避免双击发生在启动窗口期时静默丢歌。
                Thread.Sleep(100);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>运行实例：后台线程循环监听管道，收到文件后切到 UI 线程回调。</summary>
    public static void StartServer(Action<IReadOnlyList<string>> onFiles)
    {
        var thread = new Thread(() => ServerLoop(onFiles))
        {
            IsBackground = true,
            Name = "单实例管道服务器"
        };
        thread.Start();
    }

    private static void ServerLoop(Action<IReadOnlyList<string>> onFiles)
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                server.WaitForConnection();
                using var reader = new StreamReader(server, Encoding.UTF8);
                var payload = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(payload)) continue;

                var files = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(f => f.Trim('"'))
                    .Where(f => File.Exists(f))
                    .ToList();
                if (files.Count == 0) continue;

                Application.Current?.Dispatcher.BeginInvoke(() => onFiles(files));
            }
            catch
            {
                // 管道断连/超时等：忽略并继续监听
            }
        }
    }
}
