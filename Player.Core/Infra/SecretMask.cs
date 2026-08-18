namespace Player.Core.Infra;

/// <summary>敏感字段的界面展示掩码。</summary>
public static class SecretMask
{
    public static string ForDisplay(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= 4 ? "********" : "********" + value[^4..];
    }
}
