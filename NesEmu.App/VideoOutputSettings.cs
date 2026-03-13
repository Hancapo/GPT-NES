namespace NesEmu.App;

public enum VideoRendererKind
{
    Software,
    OpenGl
}

public sealed class VideoOutputSettings
{
    public VideoRendererKind Renderer { get; set; } = VideoRendererKind.Software;

    public static VideoOutputSettings CreateDefault() => new();

    public VideoOutputSettings Clone()
    {
        return new VideoOutputSettings
        {
            Renderer = Renderer
        };
    }
}

public sealed record VideoRendererOption(VideoRendererKind Renderer, string DisplayName, string Description)
{
    public override string ToString() => DisplayName;
}

public static class VideoRendererCatalog
{
    public static IReadOnlyList<VideoRendererOption> Options { get; } =
    [
        new(
            VideoRendererKind.Software,
            "Software",
            "CPU-backed upload through WriteableBitmap. Highest compatibility and the existing behavior."),
        new(
            VideoRendererKind.OpenGl,
            "OpenGL",
            "GPU-backed texture upload and presentation through an OpenGL viewport for the game surface.")
    ];
}
