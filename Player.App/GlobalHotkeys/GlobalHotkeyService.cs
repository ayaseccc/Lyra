using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Player.App.ViewModels;
using Player.Core.Hotkeys;

namespace Player.App.GlobalHotkeys;

/// <summary>
/// L2 全局热键（PLAN：RegisterHotKey 实现，默认全关；注册冲突逐条明确提示，不静默）。
/// 组合可改绑（默认 Ctrl+Alt+P 播放/暂停，Ctrl+Alt+←/→ 上下曲）；被占用的组合不抢注册，
/// 随 Conflicts 交给调用方提示；注册成功/失败/触发都留日志（便于排查"不生效"）。
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const int WmHotkey = 0x0312;

    /// <summary>预设组合（名字 → 默认组合；配置可按名字覆盖）。</summary>
    public static IReadOnlyList<(string Name, string Combo)> DefaultCombos { get; } = new[]
    {
        ("PlayPause", "Ctrl+Alt+P"),
        ("PrevTrack", "Ctrl+Alt+Left"),
        ("NextTrack", "Ctrl+Alt+Right")
    };

    private static string NameOf(string name) => name switch
    {
        "PlayPause" => "播放/暂停",
        "PrevTrack" => "上一曲",
        "NextTrack" => "下一曲",
        _ => name
    };

    private readonly HwndSource _source;
    private readonly PlayerViewModel _player;
    private readonly List<(int Id, string Name, string Display)> _active = new();
    private bool _disposed;

    /// <summary>注册失败的组合（可能被其他程序占用或组合无效），调用方据此提示用户。</summary>
    public IReadOnlyList<string> Conflicts { get; }

    /// <summary>combos：名字（PlayPause/PrevTrack/NextTrack）→ 组合串。全局热键必须带修饰键。</summary>
    public GlobalHotkeyService(IntPtr hwnd, PlayerViewModel player, IReadOnlyList<(string Name, string Combo)> combos)
    {
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("窗口 HwndSource 不存在");
        _player = player;
        _source.AddHook(WndProc);

        var conflicts = new List<string>();
        var id = 1;
        foreach (var (name, combo) in combos)
        {
            var display = $"{NameOf(name)}（{combo}）";
            if (TryParseWin32(combo, out var mods, out var vk))
            {
                TryRegister(id, name, mods, vk, display, conflicts);
            }
            else
            {
                Serilog.Log.Warning("全局热键组合无效：{Combo}", combo);
                conflicts.Add(display + "（组合无效）");
            }
            id++;
        }
        Conflicts = conflicts;
    }

    private static bool TryParseWin32(string combo, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (!ShortcutMap.TryParse(combo, out var mask, out var key)) return false;
        if (mask == ModifierMask.None) return false;   // 全局热键必须带修饰键
        if (mask.HasFlag(ModifierMask.Ctrl)) mods |= ModControl;
        if (mask.HasFlag(ModifierMask.Shift)) mods |= ModShift;
        if (mask.HasFlag(ModifierMask.Alt)) mods |= ModAlt;
        return TryGetVk(key, out vk);
    }

    private static bool TryGetVk(string key, out uint vk)
    {
        if (KeyNames.IsLetter(key))
        {
            vk = (uint)key[0];   // 'A'..'Z' → 0x41..0x5A
            return true;
        }
        vk = key switch
        {
            "D0" => 0x30, "D1" => 0x31, "D2" => 0x32, "D3" => 0x33, "D4" => 0x34,
            "D5" => 0x35, "D6" => 0x36, "D7" => 0x37, "D8" => 0x38, "D9" => 0x39,
            "F1" => 0x70, "F2" => 0x71, "F3" => 0x72, "F4" => 0x73, "F5" => 0x74,
            "F6" => 0x75, "F7" => 0x76, "F8" => 0x77, "F9" => 0x78, "F10" => 0x79,
            "F11" => 0x7A, "F12" => 0x7B,
            "Space" => 0x20, "Left" => 0x25, "Right" => 0x27, "Up" => 0x26, "Down" => 0x28,
            "Enter" => 0x0D, "Delete" => 0x2E, "Home" => 0x24, "End" => 0x23,
            "PageUp" => 0x21, "PageDown" => 0x22, "Insert" => 0x2D,
            _ => 0
        };
        return vk != 0;
    }

    private void TryRegister(int id, string name, uint modifiers, uint vk, string display, List<string> conflicts)
    {
        if (RegisterHotKey(_source.Handle, id, modifiers | ModNoRepeat, vk))
        {
            _active.Add((id, name, display));
            Serilog.Log.Information("全局热键已注册：{Display}", display);
        }
        else
        {
            var error = Marshal.GetLastWin32Error();
            Serilog.Log.Warning("全局热键注册失败（错误码 0x{Code:X8}，可能被其他程序占用）：{Display}", error, display);
            conflicts.Add(display);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey) return IntPtr.Zero;
        var id = wParam.ToInt32();
        foreach (var (hotkeyId, name, display) in _active)
        {
            if (hotkeyId != id) continue;
            Serilog.Log.Information("全局热键触发：{Display}", display);
            switch (name)
            {
                case "PlayPause": _player.PlayPauseCommand.Execute(null); break;
                case "PrevTrack": _player.PreviousCommand.Execute(null); break;
                case "NextTrack": _player.NextCommand.Execute(null); break;
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
        foreach (var (id, _, _) in _active)
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
