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


    private async void Window_OnClosing(
        object? sender, CancelEventArgs e)
    {
        if (_allowClose ||
    (System.Windows.Application.Current is App app &&
     app.IsAbortingStartup))
        {
            return;
        }

        e.Cancel = true;

        if (_shutdownInProgress)
            return;

        _shutdownInProgress = true;

        try
        {
            await _viewModel.ShutdownAsync();

            _allowClose = true;

            // Zamknięcie dopiero po zakończeniu zdarzenia Closing
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
        catch (Exception ex)
        {
            _shutdownInProgress = false;

            System.Windows.MessageBox.Show(
                ex.ToString(),
                "Błąd zamykania NovaBrowser",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }


    private void FocusAddress()
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }
}
