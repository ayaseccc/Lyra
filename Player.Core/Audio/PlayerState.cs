namespace Player.Core.Audio;

/// <summary>
/// 播放器状态。刻意不叫 PlaybackState，避免与 ManagedBass.PlaybackState 同名冲突。
/// </summary>
public enum PlayerState
{
    Stopped,
    Playing,
    Paused
}
