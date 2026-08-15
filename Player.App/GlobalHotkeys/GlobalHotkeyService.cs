using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Player.App.ViewModels;

namespace Player.App.GlobalHotkeys;

/// <summary>
/// L2 全局热键（PLAN：RegisterHotKey 实现，默认全关；注册冲突逐条明确提示，不静默）。
/// 预设：Ctrl+Alt+P 播放/暂停，Ctrl+Alt+← 上一曲，Ctrl+Alt+→ 下一曲。
/// 被占用的组合不抢注册（保持其他程序可用），随 Conflicts 交给调用方提示；
/// 其余组合照常生效；WM_HOTKEY 触发记录日志（便于冒烟验证）。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    private const uint VkP = 0x50;
    private const uint VkLeft = 0x25;
    private const uint VkRight = 0x27;

    private readonly HwndSource _source;
    private readonly PlayerViewModel _player;
    private readonly List<(int Id, string Display)> _active = new();
    private bool _disposed;

    /// <summary>注册失败的组合（可能被其他程序占用），调用方据此提示用户。</summary>
    public IReadOnlyList<string> Conflicts { get; }

    public GlobalHotkeyService(IntPtr hwnd, PlayerViewModel player)
    {
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("窗口 HwndSource 不存在");
        _player = player;
        _source.AddHook(WndProc);

        var conflicts = new List<string>();
        TryRegister(1, ModControl | ModAlt, VkP, "播放/暂停（Ctrl+Alt+P）", conflicts);
        TryRegister(2, ModControl | ModAlt, VkLeft, "上一曲（Ctrl+Alt+←）", conflicts);
        TryRegister(3, ModControl | ModAlt, VkRight, "下一曲（Ctrl+Alt+→）", conflicts);
        Conflicts = conflicts;
    }

    private void TryRegister(int id, uint modifiers, uint vk, string display, List<string> conflicts)
    {
        if (RegisterHotKey(_source.Handle, id, modifiers | ModNoRepeat, vk))
        {
            _active.Add((id, display));
            // 注册成功也留痕：排查"热键不生效"时日志能区分 未启用/注册失败/未触发
            Serilog.Log.Information("全局热键已注册：{Display}", display);
        }
        else
        {
            Serilog.Log.Warning("全局热键注册失败（可能被其他程序占用）：{Display}", display);
            conflicts.Add(display);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        foreach (var (hotkeyId, display) in _active)
        {
            if (hotkeyId != id) continue;
            Serilog.Log.Information("全局热键触发：{Display}", display);
            switch (hotkeyId)
            {
                case 1: _player.PlayPauseCommand.Execute(null); break;
                case 2: _player.PreviousCommand.Execute(null); break;
                case 3: _player.NextCommand.Execute(null); break;
            }
            handled = true;
            break;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(WndProc);
        foreach (var (id, _) in _active)
        {
            UnregisterHotKey(_source.Handle, id);
        }
        _active.Clear();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
