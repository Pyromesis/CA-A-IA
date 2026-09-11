// CA-A-IA — Tests de ScriptLaunch (shims npm .cmd/.ps1 vía shell).

namespace CaAIA.Tests.Unit;

public sealed class ScriptLaunchTests
{
    [Fact]
    public void Wrap_Exe_Passthrough()
    {
        var (file, args) = CaAIA.Infrastructure.Process.ScriptLaunch.Wrap(
            @"C:\tools\opencode.exe", new[] { "--version" });
        Assert.Equal(@"C:\tools\opencode.exe", file);
        Assert.Equal(new[] { "--version" }, args);
    }

    [Fact]
    public void Wrap_Cmd_ViaCmdExe()
    {
        var (file, args) = CaAIA.Infrastructure.Process.ScriptLaunch.Wrap(
            @"C:\npm\opencode.cmd", new[] { "serve", "--port", "4099" });
        Assert.Equal("cmd.exe", file);
        Assert.Equal(new[] { "/d", "/c", @"C:\npm\opencode.cmd", "serve", "--port", "4099" }, args);
    }

    [Fact]
    public void Wrap_Ps1_ViaPowershell()
    {
        var (file, args) = CaAIA.Infrastructure.Process.ScriptLaunch.Wrap(
            @"C:\npm\opencode.ps1", new[] { "--version" });
        Assert.Equal("powershell.exe", file);
        Assert.Contains("-File", args);
    }
}
