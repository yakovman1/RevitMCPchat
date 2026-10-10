using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCPchat;

[Transaction(TransactionMode.Manual)]
public sealed class ShowChatCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        commandData.Application.GetDockablePane(RevitApp.ChatPaneId).Show();
        return Result.Succeeded;
    }
}
