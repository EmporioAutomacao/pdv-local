using Microsoft.Extensions.Options;

namespace SyncAgent.Configuration;

public sealed class ErpPdvSnapshotOptionsValidator : IValidateOptions<ErpPdvSnapshotOptions>
{
    public ValidateOptionsResult Validate(string? name, ErpPdvSnapshotOptions options)
    {
        var failures = new List<string>();

        if (options.TimeoutSeconds is < 5 or > 300)
        {
            failures.Add($"{ErpPdvSnapshotOptions.SectionName}:TimeoutSeconds must be between 5 and 300.");
        }

        if (options.Limit is < 1 or > 5000)
        {
            failures.Add($"{ErpPdvSnapshotOptions.SectionName}:Limit must be between 1 and 5000.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
