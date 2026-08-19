namespace Player.Core.Audio;

/// <summary>
/// Maps the compact volume control to BASS' linear gain. A cubic curve gives the
/// low-volume end enough pointer travel for headphone use while remaining smooth.
/// </summary>
public static class VolumeScale
{
    public static double PointerToLinear(double pointerFraction)
    {
        var position = Math.Clamp(pointerFraction, 0.0, 1.0);
        return position * position * position;
    }

    public static double LinearToPointer(double linearGain)
        => Math.Cbrt(Math.Clamp(linearGain, 0.0, 1.0));

    public static double LinearToDecibels(double linearGain)
    {
        var gain = Math.Clamp(linearGain, 0.0, 1.0);
        return gain <= 0.00001 ? -100.0 : Math.Max(-100.0, 20.0 * Math.Log10(gain));
    }
}
