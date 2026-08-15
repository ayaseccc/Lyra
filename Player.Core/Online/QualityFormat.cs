namespace Player.Core.Online;

/// <summary>音质档位的人类可读标注（P4 实机反馈：裸数字 999 看不懂）。</summary>
public static class QualityFormat
{
    /// <summary>br → 可读文本：GD 标记 999=24bit 无损、740=16bit 无损，其余按 kbps 显示。</summary>
    public static string Br(int br)
    {
        if (br >= 999) return "24bit 无损";
        if (br >= 740) return "16bit 无损";
        return br > 0 ? $"{br} kbps" : "未知";
    }

    /// <summary>下拉选项的短标签（与 Br 同规则，仅做展示）。</summary>
    public static string OptionLabel(int br) => Br(br);
}
