using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RevitMcpChat.Agent.Mcp;
using Xunit;

namespace RevitMcpChat.Agent.Tests;

public sealed class PackageTests : IClassFixture<PackageFixture>
{
    private readonly PackageFixture _fixture;

    public PackageTests(PackageFixture fixture) => _fixture = fixture;

    [Fact]
    public void PublicPackageHasNoEmbeddedKeySource()
    {
        using var archive = ZipFile.OpenRead(_fixture.PublicPackagePath);

        Assert.DoesNotContain(archive.Entries, entry =>
            entry.FullName.EndsWith("GeneratedAdminKeySource.cs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(archive.Entries, entry =>
            entry.FullName.Contains("admin-keys", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PackageContainsThreeYearManifests()
    {
        using var archive = ZipFile.OpenRead(_fixture.PublicPackagePath);

        foreach (var year in new[] { "2022", "2023", "2024" })
        {
            Assert.Contains(archive.Entries, entry =>
                entry.FullName.Equals($"manifests/RevitMcpChat-{year}.addin.template", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task InstallWithoutRvtMcpShowsGuidance()
    {
        var paths = _fixture.CreateInstallFixture();
        var output = await _fixture.RunPowerShellAsync(
            Path.Combine(paths.PackageRoot, "install.ps1"),
            "-PackageRoot", paths.PackageRoot,
            "-InstallRoot", paths.InstallRoot,
            "-AddinsRoot", paths.AddinsRoot,
            "-RevitYears", "2022",
            "-RvtMcpPath", Path.Combine(paths.Root, "missing", "rvt-mcp.exe"));

        Assert.Contains("rvt-mcp", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("github.com/bimwright/rvt-mcp", output, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(paths.InstallRoot, "agent", "RevitMcpChat.Agent.exe")));
    }

    [Fact]
    public void IncompatibleRvtMcpShowsUpdateGuidance()
    {
        var message = RvtMcpCompatibility.GetIncompatibleGuidance(
            new[] { new McpToolDescriptor("unrelated_tool", "", "{}", null) });

        Assert.Contains("update", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("github.com/bimwright/rvt-mcp", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UninstallPreservesHistory()
    {
        var paths = _fixture.CreateInstallFixture();
        await _fixture.RunPowerShellAsync(
            Path.Combine(paths.PackageRoot, "install.ps1"),
            "-PackageRoot", paths.PackageRoot,
            "-InstallRoot", paths.InstallRoot,
            "-AddinsRoot", paths.AddinsRoot,
            "-RevitYears", "2022",
            "-RvtMcpPath", Path.Combine(paths.Root, "missing", "rvt-mcp.exe"));

        var historyDirectory = Path.Combine(paths.InstallRoot, "data");
        Directory.CreateDirectory(historyDirectory);
        var historyPath = Path.Combine(historyDirectory, "history.db");
        await File.WriteAllTextAsync(historyPath, "preserve-this-user-history");

        await _fixture.RunPowerShellAsync(
            Path.Combine(paths.PackageRoot, "uninstall.ps1"),
            "-InstallRoot", paths.InstallRoot,
            "-AddinsRoot", paths.AddinsRoot,
            "-RevitYears", "2022");

        Assert.True(File.Exists(historyPath));
        Assert.False(File.Exists(Path.Combine(paths.AddinsRoot, "2022", "RevitMcpChat.addin")));
    }

    [Fact]
    public async Task PrivateBuildKeyIsNotWrittenToConfig()
    {
        const string disposableKey = "package-test-disposable-key-7f29";
        var privatePackage = await _fixture.GetPrivatePackageAsync(disposableKey);
        var extracted = Path.Combine(_fixture.Root, "private-extracted");
        ZipFile.ExtractToDirectory(privatePackage, extracted, overwriteFiles: true);

        var localAppData = Path.Combine(_fixture.Root, "private-user-profile");
        Directory.CreateDirectory(localAppData);
        var result = await RunProcessAsync(
            Path.Combine(extracted, "agent", "RevitMcpChat.Agent.exe"),
            new[] { "providers" },
            localAppData);

        Assert.Contains("build-provided access available", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(disposableKey, result, StringComparison.Ordinal);
        Assert.False(Directory.EnumerateFiles(Path.Combine(_fixture.Root, "private-build"), "GeneratedAdminKeySource.cs", SearchOption.AllDirectories).Any());
        var settingsPath = Path.Combine(localAppData, "RevitMcpChat", "settings.json");
        Assert.False(File.Exists(settingsPath));
        using var archive = ZipFile.OpenRead(privatePackage);
        Assert.DoesNotContain(archive.Entries, entry =>
            entry.FullName.EndsWith("GeneratedAdminKeySource.cs", StringComparison.OrdinalIgnoreCase));
        foreach (var jsonEntry in archive.Entries.Where(entry => entry.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var reader = new StreamReader(jsonEntry.Open());
            Assert.DoesNotContain(disposableKey, reader.ReadToEnd(), StringComparison.Ordinal);
        }
    }

    private static async Task<string> RunProcessAsync(string executable, IReadOnlyList<string> arguments, string localAppData)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["LOCALAPPDATA"] = localAppData;
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the packaged agent.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var output = await stdout + await stderr;
        Assert.Equal(0, process.ExitCode);
        return output;
    }
}

public sealed class PackageFixture : IAsyncLifetime
{
    private const string Version = "0.1.0";
    private readonly string _outputDirectory;
    private readonly string _sourceRoot;
    private Task<string>? _privatePackage;

    public PackageFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "RevitMcpChat-PackageTests-" + Guid.NewGuid().ToString("N"));
        _outputDirectory = Path.Combine(Root, "builds");
        _sourceRoot = FindRepositoryRoot();
    }

    public string Root { get; }
    public string PublicPackagePath => Path.Combine(_outputDirectory, $"RevitMcpChat-{Version}-win-x64.zip");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_outputDirectory);
        await RunPowerShellAsync(
            Path.Combine(_sourceRoot, "packaging", "build-release.ps1"),
            "-Configuration", "Release",
            "-OutputDirectory", _outputDirectory,
            "-Version", Version);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }

        return Task.CompletedTask;
    }

    public (string Root, string PackageRoot, string InstallRoot, string AddinsRoot) CreateInstallFixture()
    {
        var root = Path.Combine(Root, "install-case-" + Guid.NewGuid().ToString("N"));
        var packageRoot = Path.Combine(root, "package");
        Directory.CreateDirectory(packageRoot);
        ZipFile.ExtractToDirectory(PublicPackagePath, packageRoot);
        return (root, packageRoot, Path.Combine(root, "local", "RevitMcpChat"), Path.Combine(root, "addins"));
    }

    public Task<string> GetPrivatePackageAsync(string key) =>
        _privatePackage ??= BuildPrivatePackageAsync(key);

    public async Task<string> RunPowerShellAsync(string script, params string[] arguments)
    {
        var shell = FindPowerShell();
        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start PowerShell.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(8));
        var output = await stdout + await stderr;
        Assert.True(process.ExitCode == 0, $"PowerShell failed ({process.ExitCode}):{Environment.NewLine}{output}");
        return output;
    }

    private async Task<string> BuildPrivatePackageAsync(string key)
    {
        var keyFile = Path.Combine(Root, "admin-keys.json");
        var privateOutput = Path.Combine(Root, "private-build");
        Directory.CreateDirectory(privateOutput);
        await File.WriteAllTextAsync(
            keyFile,
            JsonSerializer.Serialize(new Dictionary<string, string> { ["openai"] = key }));
        try
        {
            await RunPowerShellAsync(
                Path.Combine(_sourceRoot, "packaging", "build-release.ps1"),
                "-Configuration", "Release",
                "-OutputDirectory", privateOutput,
                "-Version", Version,
                "-AdminKeysFile", keyFile);
        }
        finally
        {
            if (File.Exists(keyFile))
            {
                File.Delete(keyFile);
            }
        }

        return Path.Combine(privateOutput, $"RevitMcpChat-{Version}-private-win-x64.zip");
    }

    private static string FindPowerShell()
    {
        foreach (var executable in new[] { "pwsh.exe", "powershell.exe" })
        {
            var process = Process.Start(new ProcessStartInfo(executable, "-NoProfile -Command exit")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is not null)
            {
                process.WaitForExit();
                process.Dispose();
                return executable;
            }
        }

        throw new InvalidOperationException("Package tests require PowerShell 5.1 or PowerShell 7 on Windows.");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RevitMCPchat.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the package test directory.");
    }
}
