namespace Player.Core.Audio;

/// <summary>
/// 无缝播放的决策逻辑（PLAN 第 4 节）。刻意做成纯函数：
/// 真正的出声效果只能在有声卡的机器上听，但"要不要预载""这两首能不能无缝"
/// 这些判断是可以离线自动化验证的，见 tools/Player.Harness。
/// </summary>
public static class SeamlessPolicy
{
    /// <summary>提前多少秒预创建下一曲的解码流。</summary>
    public const double PreloadLeadSeconds = 5.0;

    /// <summary>
    /// 给定曲目采样率与输出设置，算出 mixer / 设备应该跑在哪个采样率上。
    /// Follow：跟随源文件（设备支持即位完美）；Fixed：固定值，由 BASS 重采样。
    /// </summary>
    public static int ResolveOutputRate(int trackSampleRate, OutputSettings settings)
    {
        if (settings.RateMode == SampleRateMode.Fixed && settings.FixedSampleRate > 0)
            return settings.FixedSampleRate;

        return trackSampleRate > 0 ? trackSampleRate : 44100;
    }

    /// <summary>
    /// 两首曲目能否走同一条链路无缝衔接。
    /// 判据就一条：它们解析出来的输出采样率是否相同——不同就必须重建 mixer 与设备，
    /// 那样必然有一个极短间隙，也就谈不上无缝了。
    /// </summary>
    public static bool CanTransitionSeamlessly(int currentTrackRate, int nextTrackRate, OutputSettings settings)
    {
        if (nextTrackRate <= 0) return false;

        return ResolveOutputRate(currentTrackRate, settings) == ResolveOutputRate(nextTrackRate, settings);
    }

    /// <summary>是否到了该预载下一曲的时刻。</summary>
    public static bool ShouldPreload(double positionSeconds, double durationSeconds, bool alreadyPreloaded)
    {
        if (alreadyPreloaded) return false;
        if (durationSeconds <= 0 || positionSeconds <= 0) return false;

        return durationSeconds - positionSeconds <= PreloadLeadSeconds;
    }
}
