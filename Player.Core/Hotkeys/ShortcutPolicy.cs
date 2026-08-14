namespace Player.Core.Hotkeys;

/// <summary>焦点元素类别（L2 快捷键策略的输入）。</summary>
public enum FocusKind
{
    None,
    /// <summary>文本输入框（搜索/重命名/Key 输入等）：快捷键一律不响应。</summary>
    TextInput,
    /// <summary>下拉框（含可编辑输入）：快捷键不响应，放行给控件。</summary>
    ComboBox,
    /// <summary>按钮/开关类：Space 归按钮（激活），其余快捷键照常。</summary>
    ButtonBase,
    /// <summary>滑条：方向键归滑条（seek/音量），其余快捷键照常。</summary>
    Slider,
    /// <summary>曲目列表（含行内元素）：Enter 播放选中 / Delete 歌单移除。</summary>
    ListBox,
    Other
}

/// <summary>应用内快捷键（L2）。Tab 与 Esc 等由窗口级单独处理，不在此列。</summary>
public enum ShortcutKey
{
    Space,          // 播放/暂停（全局，按钮聚焦时归按钮）
    SeekBack,       // ← 后退 5 秒
    SeekForward,    // → 前进 5 秒
    PrevTrack,      // Ctrl+← 上一曲
    NextTrack,      // Ctrl+→ 下一曲
    FocusSearch,    // Ctrl+F 聚焦搜索框
    Enter,          // 列表内播放选中
    Delete,         // 歌单内移除
    Locate,         // Ctrl+L 定位当前曲
    Rescan          // F5 重扫
}

/// <summary>
/// 快捷键响应策略（纯函数，harness 可离线断言）。
/// 核心规则：任何文本输入框（搜索/重命名/Key 输入）聚焦时快捷键一律不响应；
/// 按钮聚焦时 Space 归按钮；滑条聚焦时方向键归滑条；列表聚焦时 Enter/Delete 生效。
/// </summary>
public static class ShortcutPolicy
{
    public static bool ShouldHandle(FocusKind focus, ShortcutKey key) => focus switch
    {
        FocusKind.TextInput => false,
        FocusKind.ComboBox => false,
        FocusKind.ButtonBase => key != ShortcutKey.Space,
        FocusKind.Slider => key is not (ShortcutKey.SeekBack or ShortcutKey.SeekForward
                                         or ShortcutKey.PrevTrack or ShortcutKey.NextTrack),
        FocusKind.ListBox => key is ShortcutKey.Enter or ShortcutKey.Delete or ShortcutKey.Space,
        _ => true
    };
}
