using System.Windows;
using System.Windows.Input;
using PdvLocal.Core;

namespace PdvLocal.App;

public partial class LoginWindow : Window
{
    private readonly PdvOperatorRepository _operatorRepository;

    public PdvOperator? AuthenticatedOperator { get; private set; }

    public LoginWindow(string connectionString)
    {
        _operatorRepository = new PdvOperatorRepository(connectionString);
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoginTextBox.Focus();
    }

    private void EntrarButton_Click(object sender, RoutedEventArgs e)
    {
        _ = TryLoginAsync();
    }

    private async Task TryLoginAsync()
    {
        var login = LoginTextBox.Text.Trim();
        var password = SenhaPasswordBox.Password;

        ErrorText.Visibility = Visibility.Collapsed;
        EntrarButton.IsEnabled = false;

        try
        {
            var op = await _operatorRepository.AuthenticateAsync(login, password, CancellationToken.None);
            if (op is null)
            {
                ShowError("Usuário ou senha inválidos.");
                return;
            }
            AuthenticatedOperator = op;
            DialogResult = true;
        }
        catch
        {
            ShowError("Erro ao conectar ao banco de dados local.");
        }
        finally
        {
            EntrarButton.IsEnabled = true;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        SenhaPasswordBox.Clear();
        SenhaPasswordBox.Focus();
    }
}
