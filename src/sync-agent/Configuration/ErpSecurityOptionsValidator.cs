using Microsoft.Extensions.Options;

namespace SyncAgent.Configuration;

public sealed class ErpSecurityOptionsValidator : IValidateOptions<ErpSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, ErpSecurityOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.AccessTokenEnvironmentVariable))
        {
            failures.Add($"{ErpSecurityOptions.SectionName}:AccessTokenEnvironmentVariable is required.");
        }

        if (!Enum.TryParse<System.Security.Cryptography.X509Certificates.StoreName>(
                options.ClientCertificateStoreName,
                ignoreCase: true,
                out _))
        {
            failures.Add($"{ErpSecurityOptions.SectionName}:ClientCertificateStoreName is invalid.");
        }

        if (!Enum.TryParse<System.Security.Cryptography.X509Certificates.StoreLocation>(
                options.ClientCertificateStoreLocation,
                ignoreCase: true,
                out _))
        {
            failures.Add($"{ErpSecurityOptions.SectionName}:ClientCertificateStoreLocation is invalid.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
