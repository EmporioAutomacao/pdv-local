using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using SyncAgent.Configuration;
using SyncAgent.Provisioning;

namespace SyncAgent.Security;

public sealed class ErpCredentialProvider
{
    private readonly IOptionsMonitor<ErpSecurityOptions> _options;
    private readonly EffectiveSyncAgentConfigurationProvider _effectiveSyncAgentConfigurationProvider;

    public ErpCredentialProvider(
        IOptionsMonitor<ErpSecurityOptions> options,
        EffectiveSyncAgentConfigurationProvider effectiveSyncAgentConfigurationProvider)
    {
        _options = options;
        _effectiveSyncAgentConfigurationProvider = effectiveSyncAgentConfigurationProvider;
    }

    public AuthenticationHeaderValue? CreateAuthorizationHeader()
    {
        var options = _options.CurrentValue;
        var effectiveOptions = _effectiveSyncAgentConfigurationProvider.GetCurrent();
        if (!string.IsNullOrWhiteSpace(effectiveOptions.AccessToken))
        {
            return new AuthenticationHeaderValue("Bearer", effectiveOptions.AccessToken);
        }

        var token = ResolveSecret(options.AccessTokenEnvironmentVariable, options.AccessToken);

        return string.IsNullOrWhiteSpace(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    public X509Certificate2? ResolveClientCertificate()
    {
        var options = _options.CurrentValue;
        if (!string.IsNullOrWhiteSpace(options.ClientCertificateThumbprint))
        {
            return FindCertificateByThumbprint(options);
        }

        if (!string.IsNullOrWhiteSpace(options.ClientCertificatePath))
        {
            var password = ResolveSecret(
                options.ClientCertificatePasswordEnvironmentVariable,
                options.ClientCertificatePassword);

            return string.IsNullOrWhiteSpace(password)
                ? new X509Certificate2(options.ClientCertificatePath)
                : new X509Certificate2(options.ClientCertificatePath, password);
        }

        return null;
    }

    public void ValidateProvisionedForRemoteEndpoint(Uri erpApiBaseUri)
    {
        var options = _options.CurrentValue;
        if (IsLocalDevelopment(erpApiBaseUri))
        {
            return;
        }

        if (options.RequireBearerToken && CreateAuthorizationHeader() is null)
        {
            throw new AuthenticationException(
                $"Bearer token is required. Provision it through environment variable '{options.AccessTokenEnvironmentVariable}'.");
        }

        if (options.RequireMutualTls && ResolveClientCertificate() is null)
        {
            throw new AuthenticationException(
                "Client certificate is required. Provision ClientCertificateThumbprint or ClientCertificatePath in ErpSecurity.");
        }
    }

    public static void EnsureHttpsOutsideLocalDevelopment(Uri erpApiBaseUri)
    {
        if (erpApiBaseUri.Scheme == Uri.UriSchemeHttps || IsLocalDevelopment(erpApiBaseUri))
        {
            return;
        }

        throw new AuthenticationException("ERP API base URL must use HTTPS outside local development.");
    }

    private static bool IsLocalDevelopment(Uri erpApiBaseUri)
    {
        if (erpApiBaseUri.Host is "localhost" or "127.0.0.1")
        {
            return true;
        }

        return IPAddress.TryParse(erpApiBaseUri.Host, out var address)
            && IsPrivateAddress(address);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static string ResolveSecret(string environmentVariable, string configuredValue)
    {
        var environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
        return !string.IsNullOrWhiteSpace(environmentValue)
            ? environmentValue
            : configuredValue;
    }

    private static X509Certificate2? FindCertificateByThumbprint(ErpSecurityOptions options)
    {
        var thumbprint = NormalizeThumbprint(options.ClientCertificateThumbprint);
        var storeName = Enum.Parse<StoreName>(options.ClientCertificateStoreName, ignoreCase: true);
        var storeLocation = Enum.Parse<StoreLocation>(options.ClientCertificateStoreLocation, ignoreCase: true);

        using var store = new X509Store(storeName, storeLocation);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

        return store.Certificates
            .Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
            .OfType<X509Certificate2>()
            .FirstOrDefault();
    }

    private static string NormalizeThumbprint(string thumbprint)
    {
        return thumbprint
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace(":", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
    }
}

public static class ErpHttpClientHandlerFactory
{
    public static HttpClientHandler CreateHandler(IServiceProvider serviceProvider)
    {
        var credentialProvider = serviceProvider.GetRequiredService<ErpCredentialProvider>();
        var handler = new HttpClientHandler();
        var certificate = credentialProvider.ResolveClientCertificate();

        if (certificate is not null)
        {
            handler.ClientCertificates.Add(certificate);
        }

        return handler;
    }
}
