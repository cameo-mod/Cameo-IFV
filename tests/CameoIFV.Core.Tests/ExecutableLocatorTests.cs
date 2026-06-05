using CameoIFV.Core.Install;
using Xunit;

namespace CameoIFV.Core.Tests;

public class ExecutableLocatorTests
{
    [Fact]
    public void Locate_ReturnsConfiguredTopLevelExecutable()
    {
        using var temp = new TempDir();
        var exe = temp.Write("CombinedArms.exe", "MZ");

        Assert.Equal(exe, ExecutableLocator.Locate(temp.Path, "CombinedArms.exe"));
    }

    [Fact]
    public void Locate_ReturnsConfiguredNestedExecutable()
    {
        using var temp = new TempDir();
        var exe = temp.Write(Path.Combine("bin", "CombinedArms.exe"), "MZ");

        Assert.Equal(exe, ExecutableLocator.Locate(temp.Path, "CombinedArms.exe"));
    }

    [Fact]
    public void Locate_FallsBackToSingleTopLevelExecutableWhenConfiguredNameIsMissing()
    {
        using var temp = new TempDir();
        var exe = temp.Write("OnlyGame.exe", "MZ");

        Assert.Equal(exe, ExecutableLocator.Locate(temp.Path, "Missing.exe"));
    }

    [Fact]
    public void Locate_ReturnsNullForMultipleTopLevelExecutablesWithoutConfiguredMatch()
    {
        using var temp = new TempDir();
        temp.Write("CombinedArms.exe", "MZ");
        temp.Write("OpenRA.Server.exe", "MZ");
        temp.Write("createdump.exe", "MZ");

        Assert.Null(ExecutableLocator.Locate(temp.Path, null));
    }

    [Fact]
    public void Locate_ReturnsSingleTopLevelExecutableWithoutConfiguredName()
    {
        using var temp = new TempDir();
        var exe = temp.Write("CameoMod.exe", "MZ");

        Assert.Equal(exe, ExecutableLocator.Locate(temp.Path, null));
    }
}
