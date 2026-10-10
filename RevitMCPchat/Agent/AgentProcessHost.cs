using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RevitMcpChat.Contracts;

namespace RevitMCPchat.Agent;

public interface IAgentChildProcess : IDisposable
{
    bool HasExited { get; }
    void Start();
    void Kill();
    Task WaitForExitAsync(CancellationToken cancellationToken);
}

public interface IAgentChildProcessFactory
{
    IAgentChildProcess Create(ProcessStartInfo startInfo);
}

public sealed class AgentProcessHost : IDisposable
{
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(3);
    private readonly string _agentExecutablePath;
    private readonly IAgentChildProcessFactory _processFactory;
    private readonly Func<string, CancellationToken, Task> _sendShutdown;
    private IAgentChildProcess? _process;

    public AgentProcessHost(
        string agentExecutablePath,
        IAgentChildProcessFactory? processFactory = null,
        Func<string, CancellationToken, Task>? sendShutdown = null)
    {
        if (string.IsNullOrWhiteSpace(agentExecutablePath))
        {
            throw new ArgumentException("An agent executable path is required.", nameof(agentExecutablePath));
        }

        _agentExecutablePath = Path.GetFullPath(agentExecutablePath);
        _processFactory = processFactory ?? new SystemAgentChildProcessFactory();
        _sendShutdown = sendShutdown ?? SendShutdownAsync;
    }

    public int RevitPid { get; private set; }
    public string? PipeName { get; private set; }
    public bool IsRunning => _process is not null && !_process.HasExited;

    public static string FindInstalledAgentPath(string? addinAssemblyPath = null)
    {
        var addinPath = string.IsNullOrWhiteSpace(addinAssemblyPath)
            ? typeof(AgentProcessHost).Assembly.Location
            : addinAssemblyPath;
        if (!string.IsNullOrWhiteSpace(addinPath))
        {
            var addinDirectory = Path.GetDirectoryName(Path.GetFullPath(addinPath));
            if (!string.IsNullOrWhiteSpace(addinDirectory))
            {
                var relativeAgentPath = Path.GetFullPath(Path.Combine(
                    addinDirectory,
                    "..",
                    "..",
                    "agent",
                    "RevitMcpChat.Agent.exe"));
                if (File.Exists(relativeAgentPath))
                {
                    return relativeAgentPath;
                }
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The current user's local application data folder is unavailable.");
        }

        return Path.Combine(localAppData, "RevitMcpChat", "agent", "RevitMcpChat.Agent.exe");
    }

    public Task EnsureRunningAsync(int revitPid, string pipeName, CancellationToken cancellationToken) =>
        IsRunning ? Task.CompletedTask : StartAsync(revitPid, pipeName, cancellationToken);

    public Task StartAsync(int revitPid, string pipeName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (revitPid <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revitPid));
        }

        if (!string.Equals(pipeName, AgentPipeNames.ForRevitPid(revitPid), StringComparison.Ordinal))
        {
            throw new ArgumentException("The pipe name must be bound to the supplied Revit process ID.", nameof(pipeName));
        }

        if (IsRunning)
        {
            throw new InvalidOperationException("An agent is already running for this Revit process.");
        }

        if (_process is not null)
        {
            _process.Dispose();
            _process = null;
        }

        if (_processFactory is SystemAgentChildProcessFactory && !File.Exists(_agentExecutablePath))
        {
            throw new FileNotFoundException(
                "The local Revit MCP Chat agent is missing. Repair or reinstall Revit MCP Chat.",
                _agentExecutablePath);
        }

        var startInfo = CreateStartInfo(_agentExecutablePath, revitPid, pipeName);
        var process = _processFactory.Create(startInfo);
        try
        {
            process.Start();
            _process = process;
            RevitPid = revitPid;
            PipeName = pipeName;
        }
        catch
        {
            process.Dispose();
            throw;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        var pipeName = PipeName;
        if (process is null)
        {
            return;
        }

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(GracefulShutdownTimeout);
            if (!process.HasExited && !string.IsNullOrWhiteSpace(pipeName))
            {
                try
                {
                    await _sendShutdown(pipeName!, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    // If the agent cannot shut down through its private pipe, stop this child process below.
                }
                catch (IOException)
                {
                    // If the agent is not accepting requests, stop only the process started by this host.
                }
                catch (Exception)
                {
                    // A failed private-pipe shutdown falls back to this host's child process only.
                }
            }

            if (!process.HasExited)
            {
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill();
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        _process = null;
        PipeName = null;
        RevitPid = 0;
        process.Dispose();
    }

    public void Dispose() => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    private static ProcessStartInfo CreateStartInfo(string path, int revitPid, string pipeName)
    {
        var isAssembly = string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);
        var arguments = isAssembly
            ? QuoteArgument(path) + " "
            : string.Empty;
        arguments += "agent --revit-pid " + revitPid.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " --pipe-name " + QuoteArgument(pipeName);

        return new ProcessStartInfo
        {
            FileName = isAssembly
                ? (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
                : path,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static async Task SendShutdownAsync(string pipeName, CancellationToken cancellationToken)
    {
        var client = new AgentPipeClient(pipeName);
        var reply = await client.SendAsync(new AgentCommand { Kind = AgentCommandKinds.Shutdown }, cancellationToken)
            .ConfigureAwait(false);
        if (reply.ErrorCode is not null)
        {
            throw new IOException("The local agent rejected its shutdown request.");
        }
    }

    private sealed class SystemAgentChildProcessFactory : IAgentChildProcessFactory
    {
        public IAgentChildProcess Create(ProcessStartInfo startInfo) => new SystemAgentChildProcess(startInfo);
    }

    private sealed class SystemAgentChildProcess : IAgentChildProcess
    {
        private readonly Process _process;

        public SystemAgentChildProcess(ProcessStartInfo startInfo)
        {
            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        }

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        public void Start()
        {
            if (!_process.Start())
            {
                throw new InvalidOperationException("The local agent process did not start.");
            }
        }

        public void Kill()
        {
            if (!HasExited)
            {
                _process.Kill();
            }
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (HasExited)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? handler = null;
            handler = (_, _) => completion.TrySetResult(true);
            _process.Exited += handler;
            var cancellation = cancellationToken.Register(() => completion.TrySetCanceled());
            if (HasExited)
            {
                completion.TrySetResult(true);
            }

            return AwaitExitAsync(completion.Task, cancellation, handler);
        }

        private async Task AwaitExitAsync(
            Task<bool> completion,
            CancellationTokenRegistration cancellation,
            EventHandler handler)
        {
            try
            {
                await completion.ConfigureAwait(false);
            }
            finally
            {
                _process.Exited -= handler;
                cancellation.Dispose();
            }
        }

        public void Dispose() => _process.Dispose();
    }
}
