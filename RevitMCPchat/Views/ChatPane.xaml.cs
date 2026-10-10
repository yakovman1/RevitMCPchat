using System.Windows.Controls;
using System;
using RevitMCPchat.Model;
using RevitMCPchat.ViewModels;

namespace RevitMCPchat.Views;

public partial class ChatPane : Page, IDisposable
{
    private readonly ChatPaneViewModel? _viewModel;
    private readonly DocumentEventBridge? _documentBridge;
    private bool _initialized;

    public ChatPane()
    {
        InitializeComponent();
        if (RevitApp.PipeClient is not null)
        {
            _viewModel = new ChatPaneViewModel(RevitApp.PipeClient);
            DataContext = _viewModel;
            _viewModel.SetStartupGuidance(RevitApp.AgentStartupMessage);
        }

        _documentBridge = RevitApp.DocumentBridge;
        if (_documentBridge is not null)
        {
            _documentBridge.ActiveModelChanged += OnActiveModelChanged;
        }

        Loaded += OnLoaded;
    }

    public void Dispose()
    {
        Loaded -= OnLoaded;
        if (_documentBridge is not null)
        {
            _documentBridge.ActiveModelChanged -= OnActiveModelChanged;
        }

        DataContext = null;
    }

    private async void OnLoaded(object sender, System.Windows.RoutedEventArgs args)
    {
        if (_initialized || _viewModel is null)
        {
            return;
        }

        _initialized = true;
        if (_documentBridge is not null)
        {
            try
            {
                var model = await _documentBridge.ReadActiveModelAsync();
                _viewModel.SetActiveModel(model);
            }
            catch (Exception exception)
            {
                _viewModel.Chat.ApplyLocalError("Не удалось прочитать активную модель Revit: " + exception.Message);
            }
        }

        await _viewModel.InitializeAsync();
    }

    private void OnActiveModelChanged(object? sender, RevitMcpChat.Contracts.ActiveModelInfo model) =>
        _viewModel?.SetActiveModel(model);
}
