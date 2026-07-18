using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Windows;
using PdvLocal.Core;
using static PdvLocal.App.PdvUiFormatting;

namespace PdvLocal.App;

public partial class TechnicalDetailsWindow : Window
{
    private readonly PdvAppConfiguration _configuration;
    private readonly PdvDatabaseStatusReader _databaseStatusReader;
    private readonly HttpClient _httpClient;

    public TechnicalDetailsWindow()
    {
        _configuration = PdvAppConfiguration.Load();
        _databaseStatusReader = new PdvDatabaseStatusReader(_configuration.PdvLocalConnectionString);
        _httpClient = new HttpClient
        {
            BaseAddress = _configuration.SyncAgentLocalApiBaseUrl,
            Timeout = TimeSpan.FromSeconds(5)
        };

        InitializeComponent();
        AppVersionValue.Text = typeof(TechnicalDetailsWindow).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "desconhecida";
        LocalApiText.Text = $"API local: {_configuration.SyncAgentLocalApiBaseUrl}";
    }

    protected override void OnClosed(EventArgs e)
    {
        _httpClient.Dispose();
        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenSetupButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(new Uri(_configuration.SyncAgentLocalApiBaseUrl, "/setup").ToString())
        {
            UseShellExecute = true
        });
    }

    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        RefreshButton.Content = "Atualizando...";
        try
        {
            try
            {
                var status = await _httpClient.GetFromJsonAsync<SyncAgentStatusResponse>("/status");
                if (status is null)
                {
                    ApplyOfflineState("Resposta vazia da API local do SyncAgent.");
                }
                else
                {
                    ApplyStatus(status);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException)
            {
                ApplyOfflineState("Nao foi possivel conectar ao SyncAgent local. Verifique se o servico Windows esta em execucao.");
            }

            var databaseStatus = await _databaseStatusReader.ReadAsync(CancellationToken.None);
            ApplyDatabaseStatus(databaseStatus);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RefreshButton.Content = "Atualizar";
        }
    }

    private void ApplyStatus(SyncAgentStatusResponse status)
    {
        ProvisioningValue.Text = status.Provisioned ? "Ativado" : "Nao ativado";
        RuntimeValue.Text = EmptyAsDash(status.RuntimeStatus);
        HeartbeatValue.Text = status.LastHeartbeatSucceeded == true
            ? $"Online / {status.LastHeartbeatConnectivity ?? "-"}"
            : "Sem heartbeat";
        PendingValue.Text = status.PendingOutboxEvents.ToString();
        DeadLetterValue.Text = status.DeadLetterEvents.ToString();
        InstanceValue.Text = EmptyAsDash(status.InstanceId);
        TenantValue.Text = EmptyAsDash(status.ErpTenantId);
        ErpApiValue.Text = EmptyAsDash(status.ErpApiBaseUrl);
        DatabaseValue.Text = EmptyAsDash(status.Database);
        LastErrorValue.Text = EmptyAsDash(status.LastError);
    }

    private void ApplyOfflineState(string message)
    {
        ProvisioningValue.Text = "-";
        RuntimeValue.Text = "offline";
        HeartbeatValue.Text = "-";
        PendingValue.Text = "-";
        DeadLetterValue.Text = "-";
        InstanceValue.Text = "-";
        TenantValue.Text = "-";
        ErpApiValue.Text = "-";
        DatabaseValue.Text = "-";
        LastErrorValue.Text = message;
    }

    private void ApplyDatabaseStatus(PdvDatabaseStatus status)
    {
        if (!status.IsAvailable)
        {
            PgVectorValue.Text = "-";
            PdvSchemaValue.Text = "indisponivel";
            OperatorsValue.Text = "-";
            ProductsValue.Text = "-";
            SalesValue.Text = "-";
            OpenCashSessionsValue.Text = "-";
            DatabaseValue.Text = "-";
            DatabaseErrorValue.Text = EmptyAsDash(status.Error);
            return;
        }

        PgVectorValue.Text = EmptyAsDash(status.PgVectorVersion);
        PdvSchemaValue.Text = status.PdvSchemaExists
            ? $"ok / {status.PdvTableCount} tabelas"
            : "nao criado";
        OperatorsValue.Text = status.OperatorCount.ToString();
        ProductsValue.Text = status.ProductCount.ToString();
        SalesValue.Text = status.SaleCount.ToString();
        OpenCashSessionsValue.Text = status.OpenCashSessionCount.ToString();
        DatabaseValue.Text = $"{EmptyAsDash(status.DatabaseName)} / {EmptyAsDash(status.UserName)} / {EmptyAsDash(status.TimeZone)}";
        DatabaseErrorValue.Text = "-";
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        CheckUpdateStatusText.Visibility = Visibility.Visible;
        CheckUpdateStatusText.BringIntoView();
        CheckUpdateStatusText.Text = "Solicitando verificacao de atualizacao...";
        CheckUpdateStatusText.SetResourceReference(ForegroundProperty, "StatusText");

        try
        {
            var response = await _httpClient.PostAsync("/check-update", null);
            if (response.IsSuccessStatusCode)
            {
                CheckUpdateStatusText.Text = "Solicitacao enviada. O agente verificara o ERP no proximo ciclo (ate 30s).";
                CheckUpdateStatusText.SetResourceReference(ForegroundProperty, "SuccessText");
            }
            else
            {
                CheckUpdateStatusText.Text = "Ja existe uma sincronizacao em andamento. Tente novamente em instantes.";
                CheckUpdateStatusText.SetResourceReference(ForegroundProperty, "WarningText");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            CheckUpdateStatusText.Text = "Nao foi possivel contatar o SyncAgent local. Verifique se o servico esta em execucao.";
            CheckUpdateStatusText.SetResourceReference(ForegroundProperty, "WarningText");
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }
}
