using System;
using System.IO;
using System.Reflection;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class PackagePowerShellResolutionTests
{
    [Fact]
    public void FindPowerShellFallsBackToWindowsPowerShellWhenPwshIsNotOnPath()
    {
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var windowsPowerShellDirectory = Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0");
        var system32Directory = Path.Combine(windowsDirectory, "System32");

        try
        {
            Environment.SetEnvironmentVariable(
                "PATH",
                string.Join(Path.PathSeparator, windowsPowerShellDirectory, system32Directory));

            var method = typeof(PackageFixture).GetMethod("FindPowerShell", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("PowerShell resolver was not found.");
            var selectedShell = method.Invoke(null, null) as string;

            Assert.Equal("powershell.exe", selectedShell);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
