using Microsoft.Extensions.Options;

namespace SyncAgent.Configuration;

public sealed class ErpDispatcherOptionsValidator : IValidateOptions<ErpDispatcherOptions>
{
    public ValidateOptionsResult Validate(string? name, ErpDispatcherOptions options)
    {
        var failures = new List<string>();

        if (options.BatchSize is < 1 or > 500)
        {
            failures.Add($"{ErpDispatcherOptions.SectionName}:BatchSize must be between 1 and 500.");
        }

        if (options.TimeoutSeconds is < 5 or > 300)
        {
            failures.Add($"{ErpDispatcherOptions.SectionName}:TimeoutSeconds must be between 5 and 300.");
        }

        if (options.MaxAttempts is < 1 or > 50)
        {
            failures.Add($"{ErpDispatcherOptions.SectionName}:MaxAttempts must be between 1 and 50.");
        }

        if (options.InitialBackoffSeconds is < 1 or > 86400)
        {
            failures.Add($"{ErpDispatcherOptions.SectionName}:InitialBackoffSeconds must be between 1 and 86400.");
        }

        if (options.MaxBackoffSeconds < options.InitialBackoffSeconds)
        {
            failures.Add($"{ErpDispatcherOptions.SectionName}:MaxBackoffSeconds must be greater than or equal to InitialBackoffSeconds.");
        }

        if (options.InFlightRecoverySeconds is < 30 or > 86_400)
        {
            failures.Add($"{ErpDispatcherOptions.SectionName}:InFlightRecoverySeconds must be between 30 and 86400.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
