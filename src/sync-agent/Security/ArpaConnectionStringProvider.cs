using Microsoft.Extensions.Options;
using Npgsql;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SyncAgent.Configuration;

namespace SyncAgent.Security;

public sealed class ArpaConnectionStringProvider
{
    private readonly IOptionsMonitor<ArpaCollectorOptions> _options;

    public ArpaConnectionStringProvider(IOptionsMonitor<ArpaCollectorOptions> options)
    {
        _options = options;
    }

    public string GetConnectionString()
    {
        var options = _options.CurrentValue;
        var connectionString = options.ConnectionString
            ?? throw new InvalidOperationException("ArpaCollector:ConnectionString is required.");

        var password = ResolvePassword(options);
        if (string.IsNullOrEmpty(password))
        {
            return connectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Password = password
        };

        return builder.ConnectionString;
    }

    private static string? ResolvePassword(ArpaCollectorOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PasswordEnvironmentVariable))
        {
            var value = Environment.GetEnvironmentVariable(options.PasswordEnvironmentVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        if (!string.IsNullOrWhiteSpace(options.PasswordFile))
        {
            if (!File.Exists(options.PasswordFile))
            {
                throw new InvalidOperationException($"ArpaCollector:PasswordFile not found: {options.PasswordFile}");
            }

            var value = File.ReadAllText(options.PasswordFile).Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        if (!string.IsNullOrWhiteSpace(options.PasswordProtectedFile))
        {
            if (!File.Exists(options.PasswordProtectedFile))
            {
                throw new InvalidOperationException($"ArpaCollector:PasswordProtectedFile not found: {options.PasswordProtectedFile}");
            }

            var protectedValue = File.ReadAllText(options.PasswordProtectedFile).Trim();
            if (string.IsNullOrWhiteSpace(protectedValue))
            {
                return null;
            }

            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("ArpaCollector:PasswordProtectedFile requires Windows DPAPI.");
            }

            return UnprotectWindowsDpapiSecret(protectedValue);
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string UnprotectWindowsDpapiSecret(string protectedValue)
    {
        var encryptedBytes = Convert.FromBase64String(protectedValue);
        var plainBytes = ProtectedData.Unprotect(
            encryptedBytes,
            optionalEntropy: null,
            scope: DataProtectionScope.LocalMachine);

        return Encoding.UTF8.GetString(plainBytes);
    }
}
