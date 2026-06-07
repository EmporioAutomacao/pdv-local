using System.IO;
using System.Text.Json;

namespace PdvLocal.App;

internal sealed record PdvAppConfiguration(
    string PdvLocalConnectionString,
    Uri SyncAgentLocalApiBaseUrl,
    TefAppConfiguration Tef)
{
    private const string DefaultConnectionString = "Host=localhost;Port=5432;Database=pdv_sync;Username=pdv_sync;Password=pdv_sync";
    private static readonly Uri DefaultSyncAgentLocalApiBaseUrl = new("http://127.0.0.1:47891");
    private static readonly TefAppConfiguration DefaultTefConfiguration = new("Simulated", "simulated");

    public static PdvAppConfiguration Load()
    {
        var connectionString = DefaultConnectionString;
        var syncAgentLocalApiBaseUrl = DefaultSyncAgentLocalApiBaseUrl.ToString();
        var tefMode = DefaultTefConfiguration.Mode;
        var tefProvider = DefaultTefConfiguration.Provider;

        var appsettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (File.Exists(appsettingsPath))
        {
            using var stream = File.OpenRead(appsettingsPath);
            using var document = JsonDocument.Parse(stream);

            if (TryReadString(document.RootElement, "ConnectionStrings", "PdvLocalDb") is { Length: > 0 } configuredConnectionString)
            {
                connectionString = configuredConnectionString;
            }

            if (TryReadString(document.RootElement, "SyncAgent", "LocalApiBaseUrl") is { Length: > 0 } configuredLocalApiBaseUrl)
            {
                syncAgentLocalApiBaseUrl = configuredLocalApiBaseUrl;
            }

            if (TryReadString(document.RootElement, "Tef", "Mode") is { Length: > 0 } configuredTefMode)
            {
                tefMode = configuredTefMode;
            }

            if (TryReadString(document.RootElement, "Tef", "Provider") is { Length: > 0 } configuredTefProvider)
            {
                tefProvider = configuredTefProvider;
            }
        }

        var environmentConnectionString = Environment.GetEnvironmentVariable("PDV_LOCAL_CONNECTION_STRING");
        if (!string.IsNullOrWhiteSpace(environmentConnectionString))
        {
            connectionString = environmentConnectionString;
        }

        var environmentLocalApiBaseUrl = Environment.GetEnvironmentVariable("PDV_LOCAL_SYNC_AGENT_URL");
        if (!string.IsNullOrWhiteSpace(environmentLocalApiBaseUrl))
        {
            syncAgentLocalApiBaseUrl = environmentLocalApiBaseUrl;
        }

        var environmentTefMode = Environment.GetEnvironmentVariable("PDV_LOCAL_TEF_MODE");
        if (!string.IsNullOrWhiteSpace(environmentTefMode))
        {
            tefMode = environmentTefMode;
        }

        var environmentTefProvider = Environment.GetEnvironmentVariable("PDV_LOCAL_TEF_PROVIDER");
        if (!string.IsNullOrWhiteSpace(environmentTefProvider))
        {
            tefProvider = environmentTefProvider;
        }

        if (!Uri.TryCreate(syncAgentLocalApiBaseUrl, UriKind.Absolute, out var localApiBaseUrl))
        {
            localApiBaseUrl = DefaultSyncAgentLocalApiBaseUrl;
        }

        return new PdvAppConfiguration(
            connectionString,
            localApiBaseUrl,
            new TefAppConfiguration(tefMode, tefProvider));
    }

    private static string? TryReadString(JsonElement root, string section, string key)
    {
        if (!root.TryGetProperty(section, out var sectionElement)
            || !sectionElement.TryGetProperty(key, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}

internal sealed record TefAppConfiguration(
    string Mode,
    string Provider);
