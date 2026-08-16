namespace Player.Core.Infra;

public enum MiniPlayerContentMode
{
    Spectrum,
    Lyrics
}

public static class MiniPlayerContentModePolicy
{
    public const string SpectrumValue = "Spectrum";
    public const string LyricsValue = "Lyrics";

    public static MiniPlayerContentMode Resolve(UiConfig ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        return Parse(ui.MiniContentMode) ?? MiniPlayerContentMode.Lyrics;
    }

    public static MiniPlayerContentMode? Parse(string? value)
    {
        if (string.Equals(value, SpectrumValue, StringComparison.OrdinalIgnoreCase))
            return MiniPlayerContentMode.Spectrum;
        if (string.Equals(value, LyricsValue, StringComparison.OrdinalIgnoreCase))
            return MiniPlayerContentMode.Lyrics;
        return null;
    }

    public static void Apply(UiConfig ui, MiniPlayerContentMode mode)
    {
        ArgumentNullException.ThrowIfNull(ui);
        ui.MiniContentMode = mode == MiniPlayerContentMode.Spectrum ? SpectrumValue : LyricsValue;
        ui.MiniSpectrum = mode == MiniPlayerContentMode.Spectrum;
    }
}
