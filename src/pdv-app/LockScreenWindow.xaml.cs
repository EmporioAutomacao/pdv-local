using System.ComponentModel;
using System.Windows;
using PdvLocal.Core;

namespace PdvLocal.App;

public partial class LockScreenWindow : Window
{
    private readonly PdvOperator _operator;
    private readonly PdvOperatorRepository _operatorRepository;
    private bool _unlocked;

    public LockScreenWindow(PdvOperator @operator, PdvOperatorRepository operatorRepository)
    {
        _operator = @operator;
        _operatorRepository = operatorRepository;
        InitializeComponent();
        LockedOperatorText.Text = $"Informe a senha de {@operator.DisplayName} ({@operator.Login}) para continuar.";
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_unlocked)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UnlockPasswordBox.Focus();
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        _ = TryUnlockAsync();
    }

    private async Task TryUnlockAsync()
    {
        UnlockErrorText.Visibility = Visibility.Collapsed;
        UnlockButton.IsEnabled = false;
        try
        {
            var authenticated = await _operatorRepository.AuthenticateAsync(
                _operator.Login,
                UnlockPasswordBox.Password,
                CancellationToken.None);
            if (authenticated is null || authenticated.OperatorId != _operator.OperatorId)
            {
                ShowError("Senha invalida.");
                return;
            }

            _unlocked = true;
            DialogResult = true;
        }
        catch
        {
            ShowError("Erro ao conectar ao banco de dados local.");
        }
        finally
        {
            UnlockButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        UnlockErrorText.Text = message;
        UnlockErrorText.Visibility = Visibility.Visible;
        UnlockPasswordBox.Clear();
        UnlockPasswordBox.Focus();
    }
}
