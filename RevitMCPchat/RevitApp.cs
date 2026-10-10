using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace RevitMCPchat;

public sealed class RevitApp : IExternalApplication
{
    public static readonly DockablePaneId ChatPaneId = new(new Guid("87e65a3f-c768-4d13-bd68-0d895f78d77c"));

    private ChatPaneProvider? _paneProvider;

    public Result OnStartup(UIControlledApplication application)
    {
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
        _paneProvider?.Dispose();
        _paneProvider = null;
        return Result.Succeeded;
    }
}
