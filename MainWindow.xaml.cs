using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using NovaBrowser.ViewModels;

namespace NovaBrowser;

public partial class MainWindow : Window
{
    private readonly BrowserShellViewModel _viewModel;
    private bool _shutdownInProgress;
    private bool _allowClose;

    public MainWindow(BrowserShellViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        _viewModel.FocusAddressRequested += FocusAddress;
    }

    private void AddressBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _viewModel.NavigateCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.L:
                _viewModel.FocusAddressCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.T when (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift:
                _viewModel.RestoreClosedTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.T:
                _viewModel.NewTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.W:
                _viewModel.CloseActiveTabCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Tab:
                _viewModel.NextTabCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private async void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownInProgress)
        {
            return;
        }

        _shutdownInProgress = true;
        try
        {
            await _viewModel.ShutdownAsync();
            _allowClose = true;
            Close();
        }
        catch (Exception exception)
        {
            _shutdownInProgress = false;
            System.Windows.MessageBox.Show(
                $"Nie udało się bezpiecznie zamknąć NovaBrowser.\n\n{exception.Message}",
                "Błąd zamykania",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void FocusAddress()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }
}
