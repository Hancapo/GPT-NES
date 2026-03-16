using NesEmu.App;

namespace NesEmu.Tests;

public sealed class OpenAlRuntimeProbeTests
{
    [Fact]
    public void GetDefaultBackend_UsesSdlOnLinux()
    {
        var backend = OpenAlRuntimeProbe.GetDefaultBackend(
            isLinux: true,
            isOpenAlRuntimeAvailable: true);

        Assert.Equal(AudioBackendKind.Sdl, backend);
    }

    [Fact]
    public void GetDefaultBackend_UsesSdlWhenOpenAlIsUnavailable()
    {
        var backend = OpenAlRuntimeProbe.GetDefaultBackend(
            isLinux: false,
            isOpenAlRuntimeAvailable: false);

        Assert.Equal(AudioBackendKind.Sdl, backend);
    }

    [Fact]
    public void GetDefaultBackend_UsesOpenAlWhenAvailableOffLinux()
    {
        var backend = OpenAlRuntimeProbe.GetDefaultBackend(
            isLinux: false,
            isOpenAlRuntimeAvailable: true);

        Assert.Equal(AudioBackendKind.OpenAl, backend);
    }
}
