using NesEmu.App;

namespace NesEmu.Tests;

public sealed class AudioOutputSettingsTests
{
    [Fact]
    public void CreateDefault_UsesSdlBackend()
    {
        var settings = AudioOutputSettings.CreateDefault();

        Assert.Equal(AudioBackendKind.Sdl, settings.Backend);
    }
}
