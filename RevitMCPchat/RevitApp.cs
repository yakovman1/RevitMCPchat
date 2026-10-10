using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
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
    private AgentProcessHost? _agentHost;

    public Result OnStartup(UIControlledApplication application)
    {
        var revitPid = Process.GetCurrentProcess().Id;
        var pipeName = AgentPipeNames.ForRevitPid(revitPid);
        PipeClient = new AgentPipeClient(pipeName);
        try
        {
            _agentHost = new AgentProcessHost(AgentProcessHost.FindInstalledAgentPath());
            _agentHost.StartAsync(revitPid, pipeName, CancellationToken.None).GetAwaiter().GetResult();
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
        DocumentBridge?.Dispose();
        DocumentBridge = null;
        PipeClient = null;
        _agentHost?.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _agentHost?.Dispose();
        _agentHost = null;
        _paneProvider?.Dispose();
        _paneProvider = null;
        return Result.Succeeded;
    }
}
