using System.Windows;
using PdvLocal.Core;

namespace PdvLocal.App;

public partial class SupervisorAuthorizationDialog : Window
{
    private readonly PdvOperatorRepository _operatorRepository;

    internal SupervisorAuthorization? Result { get; private set; }

    public SupervisorAuthorizationDialog(PdvOperatorRepository operatorRepository, string operationDescription)
    {
        _operatorRepository = operatorRepository;
        InitializeComponent();
        OperationDescriptionText.Text = $"Operacao: {operationDescription}.";
    }

    internal static SupervisorAuthorization? Request(
        Window owner,
        PdvOperatorRepository operatorRepository,
        string operationDescription)
    {
        var dialog = new SupervisorAuthorizationDialog(operatorRepository, operationDescription) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SupervisorLoginTextBox.Focus();
    }

    private void AuthorizeSupervisorButton_Click(object sender, RoutedEventArgs e)
    {
        _ = TryAuthorizeAsync();
    }

    private async Task TryAuthorizeAsync()
    {
        var login = SupervisorLoginTextBox.Text.Trim();
        var password = SupervisorPasswordBox.Password;
        var reason = SupervisorReasonTextBox.Text.Trim();

        AuthorizationErrorText.Visibility = Visibility.Collapsed;

        if (string.IsNullOrWhiteSpace(login))
        {
            ShowError("Informe o login do supervisor.");
            return;
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            ShowError("Informe o motivo da autorizacao.");
            return;
        }

        AuthorizeSupervisorButton.IsEnabled = false;
        try
        {
            var supervisor = await _operatorRepository.AuthenticateAsync(login, password, CancellationToken.None);
            if (supervisor is null)
            {
                ShowError("Usuario ou senha invalidos.");
                return;
            }

            if (!PdvValidation.IsSupervisorRole(supervisor.Role))
            {
                ShowError($"Operador {supervisor.Login} nao possui papel de supervisor/admin.");
                return;
            }

            Result = new SupervisorAuthorization(supervisor, reason);
            DialogResult = true;
        }
        catch
        {
            ShowError("Erro ao conectar ao banco de dados local.");
        }
        finally
        {
            AuthorizeSupervisorButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        AuthorizationErrorText.Text = message;
        AuthorizationErrorText.Visibility = Visibility.Visible;
        SupervisorPasswordBox.Clear();
    }
}
