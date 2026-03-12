using NesEmu.Core;
using NesEmu.Core.Cartridge;

namespace NesEmu.Tests;

public sealed class HelloWorldRomTests
{
    [Fact]
    public void HelloWorldRom_RunsAndDrawsVisibleText()
    {
        var romPath = FindHelloWorldRom();
        Assert.True(File.Exists(romPath), $"No se encontró la ROM en {romPath}");

        using var console = new NesConsole(CartridgeImage.Load(romPath));
        for (var frame = 0; frame < 3; frame++)
        {
            console.RunFrame();
        }

        var distinctColors = console.FrameBuffer.ToArray().Distinct().Count();
        Assert.True(distinctColors > 1, "La ROM no dibujó texto visible en pantalla.");
    }

    private static string FindHelloWorldRom()
    {
        var roots = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        };

        foreach (var root in roots)
        {
            var directory = new DirectoryInfo(root);
            for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
            {
                var romPath = Path.Combine(directory.FullName, "Samples", "HelloWorld.nes");
                if (File.Exists(romPath))
                {
                    return romPath;
                }
            }
        }

        return Path.Combine(Environment.CurrentDirectory, "Samples", "HelloWorld.nes");
    }
}
