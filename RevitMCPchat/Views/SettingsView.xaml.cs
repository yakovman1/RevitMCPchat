using System.ComponentModel;
using System.Windows.Controls;
using RevitMCPchat.ViewModels;

namespace RevitMCPchat.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _viewModel;
    private bool _syncingPassword;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs args)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as SettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        SetPassword(_viewModel?.ApiKeyInput ?? string.Empty);
    }

    private void OnPasswordChanged(object sender, System.Windows.RoutedEventArgs args)
    {
        if (!_syncingPassword && _viewModel is not null)
        {
            _viewModel.ApiKeyInput = ApiKeyBox.Password;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsViewModel.ApiKeyInput)
            && string.IsNullOrEmpty(_viewModel?.ApiKeyInput))
        {
            SetPassword(string.Empty);
        }
    }

    private void SetPassword(string value)
    {
        _syncingPassword = true;
        try
        {
            if (string.IsNullOrEmpty(value))
            {
                ApiKeyBox.Clear();
            }
            else
            {
                ApiKeyBox.Password = value;
            }
        }
        finally
        {
            _syncingPassword = false;
        }
    }
}
