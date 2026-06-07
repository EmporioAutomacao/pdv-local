using Microsoft.Extensions.Options;

namespace SyncAgent.Configuration;

public sealed class SyncAgentOptionsValidator : IValidateOptions<SyncAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, SyncAgentOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.InstanceId))
        {
            failures.Add($"{SyncAgentOptions.SectionName}:InstanceId is required.");
        }

        if (string.IsNullOrWhiteSpace(options.ErpTenantId))
        {
            failures.Add($"{SyncAgentOptions.SectionName}:ErpTenantId is required.");
        }

        if (!Uri.TryCreate(options.ErpApiBaseUrl, UriKind.Absolute, out var erpApiBaseUri)
            || (erpApiBaseUri.Scheme != Uri.UriSchemeHttp && erpApiBaseUri.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"{SyncAgentOptions.SectionName}:ErpApiBaseUrl must be an absolute HTTP or HTTPS URL.");
        }

        if (options.PollingIntervalSeconds < 5)
        {
            failures.Add($"{SyncAgentOptions.SectionName}:PollingIntervalSeconds must be at least 5.");
        }

        if (options.LocalStatusPort is < 1024 or > 65535)
        {
            failures.Add($"{SyncAgentOptions.SectionName}:LocalStatusPort must be between 1024 and 65535.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
