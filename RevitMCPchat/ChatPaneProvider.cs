using Autodesk.Revit.UI;

namespace RevitMCPchat;

public sealed class ChatPaneProvider : IDockablePaneProvider, System.IDisposable
{
    private Views.ChatPane? _page;

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        _page ??= new Views.ChatPane();
        data.FrameworkElement = _page;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Right
        };
    }

    public void Dispose()
    {
        if (_page is null)
        {
            return;
        }

        _page.DataContext = null;
        _page.Content = null;
        _page = null;
    }
}
