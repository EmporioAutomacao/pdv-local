using Microsoft.Extensions.Options;

namespace SyncAgent.Configuration;

public sealed class ErpReconciliationOptionsValidator : IValidateOptions<ErpReconciliationOptions>
{
    public ValidateOptionsResult Validate(string? name, ErpReconciliationOptions options)
    {
        var failures = new List<string>();

        if (options.TimeoutSeconds is < 5 or > 300)
        {
            failures.Add($"{ErpReconciliationOptions.SectionName}:TimeoutSeconds must be between 5 and 300.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
