namespace Playnite.SDK;

/// <summary>Represents SDK version properties.</summary>
public static class SdkVersions
{
    public static Version SDKVersion => typeof(SdkVersions).Assembly.GetName().Version!;
}
