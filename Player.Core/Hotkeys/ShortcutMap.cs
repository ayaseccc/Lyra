using System;
using System.Collections.Generic;
using System.Linq;

namespace Player.Core.Hotkeys;

/// <summary>修饰键（与 WPF 解耦，App 层转换）。</summary>
[Flags]
public enum ModifierMask
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4
}

/// <summary>键名词表：与 WPF Key.ToString() 对齐（Core 不引 WPF）。</summary>
public static class KeyNames
{
    /// <summary>字母键：单独按容易误触，必须带修饰键。</summary>
    public static bool IsLetter(string key) => key.Length == 1 && key[0] is >= 'A' and <= 'Z';

    /// <summary>允许改绑的键名全集。</summary>
    public static bool IsAllowed(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (IsLetter(key)) return true;
        if (key.Length == 2 && key[0] == 'D' && key[1] is >= '0' and <= '9') return true; // 主键盘数字 D0-D9
        if (key.Length == 2 && key[0] == 'F' && key[1] is >= '1' and <= '9') return true; // F1-F9
        if (key == "F10" || key == "F11" || key == "F12") return true;
        return key is "Space" or "Left" or "Right" or "Up" or "Down" or "Enter"
            or "Delete" or "Home" or "End" or "PageUp" or "PageDown" or "Insert";
    }
}

/// <summary>一条改绑：动作 → 组合串（如 "Ctrl+Shift+K"，修饰键固定顺序 Ctrl+Shift+Alt）。</summary>
public sealed record ShortcutBinding(ShortcutKey Action, string Combo);

/// <summary>
/// 快捷键映射（L2 自定义改绑）：默认表 + 配置覆盖，纯函数可离线断言。
/// 覆盖无效/冲突时回退默认；组合唯一性校验在 TryAddOverride 完成。
/// </summary>
public sealed class ShortcutMap
{
    private readonly Dictionary<ShortcutKey, string> _byAction;
    private readonly Dictionary<string, ShortcutKey> _byCombo;

    public static IReadOnlyList<ShortcutBinding> Defaults { get; } = new[]
    {
        new ShortcutBinding(ShortcutKey.Space, "Space"),
        new ShortcutBinding(ShortcutKey.SeekBack, "Left"),
        new ShortcutBinding(ShortcutKey.SeekForward, "Right"),
        new ShortcutBinding(ShortcutKey.PrevTrack, "Ctrl+Left"),
        new ShortcutBinding(ShortcutKey.NextTrack, "Ctrl+Right"),
        new ShortcutBinding(ShortcutKey.FocusSearch, "Ctrl+F"),
        new ShortcutBinding(ShortcutKey.Enter, "Enter"),
        new ShortcutBinding(ShortcutKey.Delete, "Delete"),
        new ShortcutBinding(ShortcutKey.Locate, "Ctrl+L"),
        new ShortcutBinding(ShortcutKey.Rescan, "F5")
    };

    /// <summary>动作描述（设置页展示用）。</summary>
    public static string Describe(ShortcutKey action) => action switch
    {
        ShortcutKey.Space => "播放 / 暂停（全局；大歌词页内同样生效）",
        ShortcutKey.SeekBack => "后退 5 秒",
        ShortcutKey.SeekForward => "前进 5 秒",
        ShortcutKey.PrevTrack => "上一曲",
        ShortcutKey.NextTrack => "下一曲",
        ShortcutKey.FocusSearch => "聚焦搜索框",
        ShortcutKey.Enter => "播放选中曲目（列表聚焦时）",
        ShortcutKey.Delete => "从歌单移除（歌单页）",
        ShortcutKey.Locate => "定位正在播放的曲目",
        ShortcutKey.Rescan => "重扫媒体库",
        _ => action.ToString()
    };

    public ShortcutMap(IReadOnlyDictionary<string, string>? overrides = null)
    {
        _byAction = Defaults.ToDictionary(d => d.Action, d => d.Combo);
        _byCombo = Defaults.ToDictionary(d => d.Combo, d => d.Action);

        if (overrides is not null)
        {
            foreach (var (actionName, combo) in overrides)
            {
                if (!Enum.TryParse<ShortcutKey>(actionName, out var action)) continue;
                if (!TryAddOverride(action, combo)) continue;   // 无效/冲突 → 保持默认
            }
        }
    }

    /// <summary>尝试应用一条覆盖；失败（组合非法/与现有冲突）返回 false 且不改动。</summary>
    public bool TryAddOverride(ShortcutKey action, string combo)
    {
        if (!TryParse(combo, out _, out var key) || !KeyNames.IsAllowed(key)) return false;
        if (RequiresModifier(key) && ModifiersOf(combo) == ModifierMask.None) return false;
        if (_byCombo.ContainsKey(combo)) return false;      // 组合已被占用
        if (_byAction.TryGetValue(action, out var old)) _byCombo.Remove(old);
        _byAction[action] = combo;
        _byCombo[combo] = action;
        return true;
    }

    public string GetCombo(ShortcutKey action) => _byAction.TryGetValue(action, out var combo) ? combo : string.Empty;

    /// <summary>把按键解析成动作；焦点规则（ShortcutPolicy）一并裁决。</summary>
    public bool TryResolve(string keyName, ModifierMask mods, FocusKind focus, out ShortcutKey action)
    {
        action = default;
        if (!_byCombo.TryGetValue(Format(mods, Canonical(keyName)), out var hit)) return false;
        if (!ShortcutPolicy.ShouldHandle(focus, hit)) return false;
        action = hit;
        return true;
    }

    /// <summary>键名规范化：WPF 的 Key.Enter.ToString() 是 "Return"，统一成界面使用的 "Enter"（P4 实测修正）。</summary>
    public static string Canonical(string key) => key == "Return" ? "Enter" : key;

    /// <summary>字母键必须带修饰键（防止在任意窗口误触）。</summary>
    public static bool RequiresModifier(string key) => KeyNames.IsLetter(key);

    public static ModifierMask ModifiersOf(string combo) => TryParse(combo, out var mods, out _) ? mods : ModifierMask.None;

    public static string Format(ModifierMask mods, string key)
    {
        var s = string.Empty;
        if (mods.HasFlag(ModifierMask.Ctrl)) s += "Ctrl+";
        if (mods.HasFlag(ModifierMask.Shift)) s += "Shift+";
        if (mods.HasFlag(ModifierMask.Alt)) s += "Alt+";
        return s + key;
    }

    /// <summary>解析 "Ctrl+Shift+K"；修饰键按顺序，未知段直接失败。</summary>
    public static bool TryParse(string combo, out ModifierMask mods, out string key)
    {
        mods = ModifierMask.None;
        key = string.Empty;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        var parts = combo.Split('+');
        var last = parts[^1];
        if (!KeyNames.IsAllowed(last)) return false;
        foreach (var p in parts[..^1])
        {
            switch (p)
            {
                case "Ctrl": mods |= ModifierMask.Ctrl; break;
                case "Shift": mods |= ModifierMask.Shift; break;
                case "Alt": mods |= ModifierMask.Alt; break;
                default: return false;
            }
        }
        key = last;
        return true;
    }
}
