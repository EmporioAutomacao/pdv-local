using Microsoft.Extensions.Options;

namespace SyncAgent.Configuration;

public sealed class ErpHeartbeatOptionsValidator : IValidateOptions<ErpHeartbeatOptions>
{
    public ValidateOptionsResult Validate(string? name, ErpHeartbeatOptions options)
    {
        var failures = new List<string>();

        if (options.TimeoutSeconds is < 5 or > 300)
        {
            failures.Add($"{ErpHeartbeatOptions.SectionName}:TimeoutSeconds must be between 5 and 300.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
