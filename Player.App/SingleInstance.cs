using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Player.App;

/// <summary>
/// P6 单实例：Mutex 判定首个实例；第二实例把命令行文件（双击/多选/拖到 exe）
/// 经命名管道转交运行实例后退出。运行实例在 UI 线程回调处理（导入曲库并播放）。
/// </summary>
internal static class SingleInstance
{
    private static readonly string InstanceScope = BuildInstanceScope();
    private static readonly string PipeName = "Lyra_InstancePipe_v1_" + InstanceScope;
    private static readonly string MutexName = @"Local\Lyra_" + InstanceScope;
    private static readonly string LegacyPipeName = "PlayerMusicPlayer_InstancePipe_v1_" + InstanceScope;
    private static readonly string LegacyMutexName = @"Local\PlayerMusicPlayer_" + InstanceScope;
    private static readonly object ServerGate = new();
    private static readonly object DeliveryGate = new();
    private static readonly object RequestGate = new();
    private static readonly List<Thread> ServerThreads = new();
    private static readonly List<DispatcherOperation> PendingDeliveries = new();
    private static readonly Dictionary<Guid, bool> CompletedRequests = new();
    private static readonly Queue<Guid> CompletedRequestOrder = new();
    private static Mutex? _legacyMutex;
    private static string? _forwardPipeName;
    private static CancellationTokenSource? _serverCancellation;
    private static bool _serverStarted;
    private static volatile bool _stopping;
    private const int GuidByteCount = 16;
    private const int MaxPayloadBytes = 1024 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

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
        mutex = null;
        Mutex? legacyMutex = null;
        try
        {
            // Hold the old product-name mutex during the rename transition. This keeps an
            // already-running Player and Lyra from opening the same portable data directory,
            // and lets old shortcuts hand files to Lyra through the compatibility pipe.
            legacyMutex = new Mutex(initiallyOwned: true, LegacyMutexName, out var legacyCreatedNew);
            if (!legacyCreatedNew)
            {
                legacyMutex.Dispose();
                _forwardPipeName = Mutex.TryOpenExisting(MutexName, out var existingLyra)
                    ? PipeName
                    : LegacyPipeName;
                existingLyra?.Dispose();
                return false;
            }

            var primaryMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                primaryMutex.Dispose();
                legacyMutex.Dispose();
                _forwardPipeName = PipeName;
                return false;
            }

            _legacyMutex = legacyMutex;
            mutex = primaryMutex;
            _forwardPipeName = null;
            return true;
        }
        catch
        {
            legacyMutex?.Dispose();
            mutex?.Dispose();
            mutex = null;
            throw;
        }
    }

    /// <summary>第二实例：把文件清单发给运行实例。发送失败（运行实例未就绪等）静默。</summary>
    public static bool ForwardFiles(IReadOnlyList<string> files)
    {
        if (files.Count == 0) return false;
        var payload = Encoding.UTF8.GetBytes(string.Join("\n", files));
        var targetPipe = _forwardPipeName;
        if (string.IsNullOrEmpty(targetPipe)) return false;
        var requestId = Guid.NewGuid();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (TryForwardToPipe(targetPipe, requestId, payload))
                return true;

            // 首实例可能已拿到 Mutex 但还没来得及创建 ServerStream；短暂重试
            // 避免双击发生在启动窗口期时静默丢歌。
            Thread.Sleep(100);
        }

        return false;
    }

    private static bool TryForwardToPipe(string pipeName, Guid requestId, byte[] payload)
    {
        try
        {
            var usesAcknowledgement = string.Equals(pipeName, PipeName, StringComparison.Ordinal);
            using var client = new NamedPipeClientStream(
                ".", pipeName,
                usesAcknowledgement ? PipeDirection.InOut : PipeDirection.Out,
                PipeOptions.Asynchronous);
            client.Connect(TimeSpan.FromMilliseconds(350));
            if (usesAcknowledgement)
            {
                Span<byte> length = stackalloc byte[sizeof(int)];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                    length, GuidByteCount + payload.Length);
                client.Write(length);
                Span<byte> id = stackalloc byte[GuidByteCount];
                requestId.TryWriteBytes(id);
                client.Write(id);
            }
            client.Write(payload);
            client.Flush();

            if (!usesAcknowledgement) return true;
            using var acknowledgementTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var acknowledgement = new byte[1];
            var read = client.ReadAsync(acknowledgement, acknowledgementTimeout.Token)
                .AsTask().GetAwaiter().GetResult();
            return read == 1 && acknowledgement[0] == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>运行实例：后台线程循环监听管道，收到文件后切到 UI 线程回调。</summary>
    public static void StartServer(Action<IReadOnlyList<string>> onFiles)
    {
        CancellationToken cancellationToken;
        lock (ServerGate)
        {
            if (_serverStarted) return;
            _serverCancellation = new CancellationTokenSource();
            cancellationToken = _serverCancellation.Token;
            _serverStarted = true;
        }
        lock (DeliveryGate)
        {
            _stopping = false;
            PendingDeliveries.Clear();
        }
        StartServer(PipeName, "Lyra 单实例管道服务器", usesAcknowledgement: true, onFiles, cancellationToken);
        StartServer(LegacyPipeName, "旧 Player 单实例兼容管道", usesAcknowledgement: false, onFiles, cancellationToken);
    }

    private static void StartServer(
        string pipeName,
        string threadName,
        bool usesAcknowledgement,
        Action<IReadOnlyList<string>> onFiles,
        CancellationToken cancellationToken)
    {
        var thread = new Thread(() => ServerLoop(pipeName, usesAcknowledgement, onFiles, cancellationToken))
        {
            IsBackground = true,
            Name = threadName
        };
        lock (ServerGate)
            ServerThreads.Add(thread);
        thread.Start();
    }

    private static void ServerLoop(
        string pipeName,
        bool usesAcknowledgement,
        Action<IReadOnlyList<string>> onFiles,
        CancellationToken cancellationToken)
    {
        while (!_stopping)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    pipeName,
                    usesAcknowledgement ? PipeDirection.InOut : PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                server.WaitForConnection();
                (Guid RequestId, string Payload) frame = usesAcknowledgement
                    ? ReadFramedPayloadAsync(server, cancellationToken).GetAwaiter().GetResult()
                    : (Guid.Empty, ReadLegacyPayloadAsync(server, cancellationToken).GetAwaiter().GetResult());
                var payload = frame.Payload;

                if (_stopping)
                {
                    if (usesAcknowledgement) WriteAcknowledgement(server, accepted: false);
                    break;
                }
                if (string.IsNullOrWhiteSpace(payload)) continue;

                var files = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(f => f.Trim('"'))
                    .Where(f => File.Exists(f))
                    .ToList();
                if (files.Count == 0) continue;

                if (usesAcknowledgement && TryGetCompletedRequest(frame.RequestId, out var previousResult))
                {
                    WriteAcknowledgement(server, previousResult);
                    continue;
                }

                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                {
                    if (usesAcknowledgement) RememberCompletedRequest(frame.RequestId, accepted: false);
                    if (usesAcknowledgement) WriteAcknowledgement(server, accepted: false);
                    continue;
                }

                var accepted = DeliverToDispatcher(dispatcher, files, onFiles);
                if (usesAcknowledgement) RememberCompletedRequest(frame.RequestId, accepted);
                if (usesAcknowledgement) WriteAcknowledgement(server, accepted);
                if (_stopping) break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stopping)
            {
                break;
            }
            catch
            {
                // 管道断连/超时等：忽略并继续监听
            }
        }
    }

    private static bool DeliverToDispatcher(
        Dispatcher dispatcher,
        IReadOnlyList<string> files,
        Action<IReadOnlyList<string>> onFiles)
    {
        DispatcherOperation operation;
        lock (DeliveryGate)
        {
            if (_stopping || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                return false;

            operation = dispatcher.InvokeAsync(
                () => onFiles(files),
                DispatcherPriority.Send);
            PendingDeliveries.Add(operation);
        }

        try
        {
            operation.Task.GetAwaiter().GetResult();
            return operation.Status == DispatcherOperationStatus.Completed;
        }
        catch
        {
            return false;
        }
        finally
        {
            lock (DeliveryGate)
                PendingDeliveries.Remove(operation);
        }
    }

    private static async Task<(Guid RequestId, string Payload)> ReadFramedPayloadAsync(
        Stream stream,
        CancellationToken serverCancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        timeout.CancelAfter(ReadTimeout);
        var cancellationToken = timeout.Token;

        var lengthBytes = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(lengthBytes, cancellationToken).ConfigureAwait(false);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
        if (length <= 0) return (Guid.Empty, string.Empty);
        if (length < GuidByteCount || length > GuidByteCount + MaxPayloadBytes)
            throw new InvalidDataException("单实例管道消息长度无效");

        var idBytes = new byte[GuidByteCount];
        await stream.ReadExactlyAsync(idBytes, cancellationToken).ConfigureAwait(false);
        var requestId = new Guid(idBytes);
        var payload = new byte[length - GuidByteCount];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return (requestId, Encoding.UTF8.GetString(payload));
    }

    private static async Task<string> ReadLegacyPayloadAsync(
        Stream stream,
        CancellationToken serverCancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverCancellation);
        timeout.CancelAfter(ReadTimeout);
        var cancellationToken = timeout.Token;
        using var payload = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (payload.Length + read > MaxPayloadBytes)
                throw new InvalidDataException("旧版单实例管道消息过大");
            payload.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(payload.GetBuffer(), 0, checked((int)payload.Length));
    }

    private static void WriteAcknowledgement(Stream stream, bool accepted)
    {
        try
        {
            stream.WriteByte(accepted ? (byte)1 : (byte)0);
            stream.Flush();
        }
        catch
        {
            // 客户端已退出时不影响监听线程收尾。
        }
    }

    private static bool TryGetCompletedRequest(Guid requestId, out bool accepted)
    {
        lock (RequestGate)
            return CompletedRequests.TryGetValue(requestId, out accepted);
    }

    private static void RememberCompletedRequest(Guid requestId, bool accepted)
    {
        if (requestId == Guid.Empty) return;
        lock (RequestGate)
        {
            if (CompletedRequests.ContainsKey(requestId)) return;
            CompletedRequests[requestId] = accepted;
            CompletedRequestOrder.Enqueue(requestId);
            while (CompletedRequestOrder.Count > 256)
            {
                var expired = CompletedRequestOrder.Dequeue();
                CompletedRequests.Remove(expired);
            }
        }
    }

    /// <summary>退出前停止两个监听入口，避免清理服务期间仍接受无法处理的新文件。</summary>
    public static void StopServer()
    {
        CancellationTokenSource? cancellation;
        lock (ServerGate)
        {
            if (!_serverStarted) return;
            _serverStarted = false;
            cancellation = _serverCancellation;
            _serverCancellation = null;
        }

        DispatcherOperation[] pending;
        lock (DeliveryGate)
        {
            _stopping = true;
            pending = PendingDeliveries.ToArray();
        }
        foreach (var operation in pending)
            operation.Abort();

        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { }

        WakeServer(PipeName);
        WakeServer(LegacyPipeName);

        Thread[] threads;
        lock (ServerGate)
            threads = ServerThreads.ToArray();

        foreach (var thread in threads)
            thread.Join(TimeSpan.FromSeconds(2));

        lock (ServerGate)
            ServerThreads.Clear();

        cancellation?.Dispose();
    }

    private static void WakeServer(string pipeName)
    {
        try
        {
            var usesAcknowledgement = string.Equals(pipeName, PipeName, StringComparison.Ordinal);
            using var client = new NamedPipeClientStream(
                ".", pipeName,
                usesAcknowledgement ? PipeDirection.InOut : PipeDirection.Out);
            client.Connect(TimeSpan.FromMilliseconds(300));
            if (usesAcknowledgement)
            {
                Span<byte> emptyFrame = stackalloc byte[sizeof(int)];
                client.Write(emptyFrame);
                client.Flush();
            }
        }
        catch
        {
            // 监听线程尚未启动或已经退出时无需唤醒。
        }
    }

    /// <summary>服务释放完成后，由创建 Mutex 的 UI 线程释放旧产品名兼容锁。</summary>
    public static void ReleaseLegacyMutex()
    {
        var mutex = _legacyMutex;
        _legacyMutex = null;
        if (mutex is null) return;

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 未持有或进程已进入异常退出路径；关闭句柄仍然必要。
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
