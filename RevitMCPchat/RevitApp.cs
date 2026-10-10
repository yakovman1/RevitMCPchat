using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using RevitMCPchat.Agent;
using RevitMCPchat.Model;
using RevitMcpChat.Contracts;

namespace RevitMCPchat;

public sealed class RevitApp : IExternalApplication
{
    public static readonly DockablePaneId ChatPaneId = new(new Guid("87e65a3f-c768-4d13-bd68-0d895f78d77c"));

    public static AgentPipeClient? PipeClient { get; private set; }
    public static DocumentEventBridge? DocumentBridge { get; private set; }
    public static string? AgentStartupMessage { get; private set; }

    private ChatPaneProvider? _paneProvider;
    private static readonly SemaphoreSlim AgentRecoveryLock = new(1, 1);
    private static AgentProcessHost? _agentHost;
    private static int _revitPid;
    private static string? _pipeName;
    private static bool _shuttingDown;

    public Result OnStartup(UIControlledApplication application)
    {
        _shuttingDown = false;
        _revitPid = Process.GetCurrentProcess().Id;
        _pipeName = AgentPipeNames.ForRevitPid(_revitPid);
        PipeClient = new AgentPipeClient(_pipeName);
        try
        {
            _agentHost = new AgentProcessHost(AgentProcessHost.FindInstalledAgentPath(Assembly.GetExecutingAssembly().Location));
            _agentHost.EnsureRunningAsync(_revitPid, _pipeName, CancellationToken.None).GetAwaiter().GetResult();
            AgentStartupMessage = "Connected to local agent.";
        }
        catch (FileNotFoundException exception)
        {
            AgentStartupMessage = exception.Message;
        }
        catch (Exception)
        {
            AgentStartupMessage = "The local agent could not start. Repair or reinstall Revit MCP Chat.";
        }

        DocumentBridge = new DocumentEventBridge(application, new ActiveDocumentReader(), PipeClient);
        _paneProvider = new ChatPaneProvider();
        application.RegisterDockablePane(ChatPaneId, "Revit MCP Chat", _paneProvider);

        var panel = application.CreateRibbonPanel(Tab.AddIns, "Revit MCP Chat");
        var button = new PushButtonData(
            "RevitMcpChat.ShowChat",
            "AI Chat",
            Assembly.GetExecutingAssembly().Location,
            typeof(ShowChatCommand).FullName!);
        button.ToolTip = "Open the Revit MCP Chat panel.";
        panel.AddItem(button);

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _shuttingDown = true;
        DocumentBridge?.Dispose();
        DocumentBridge = null;
        PipeClient = null;
        AgentRecoveryLock.Wait();
        try
        {
            _agentHost?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _agentHost?.Dispose();
            _agentHost = null;
        }
        finally
        {
            AgentRecoveryLock.Release();
        }

        _paneProvider?.Dispose();
        _paneProvider = null;
        return Result.Succeeded;
    }

    public static async Task EnsureAgentRunningAsync(CancellationToken cancellationToken)
    {
        await AgentRecoveryLock.WaitAsync(cancellationToken);
        try
        {
            if (_shuttingDown)
            {
                throw new InvalidOperationException("Revit is shutting down; the local agent cannot restart.");
            }

            if (_revitPid <= 0)
            {
                throw new InvalidOperationException("The Revit agent session has not been initialized.");
            }
            var pipeName = _pipeName ?? throw new InvalidOperationException(
                "The Revit agent session has not been initialized.");

            if (_agentHost is null)
            {
                _agentHost = new AgentProcessHost(AgentProcessHost.FindInstalledAgentPath(Assembly.GetExecutingAssembly().Location));
            }

            await _agentHost.EnsureRunningAsync(_revitPid, pipeName, cancellationToken);
            AgentStartupMessage = "Connected to local agent.";
        }
        finally
        {
            AgentRecoveryLock.Release();
        }
    }
}
