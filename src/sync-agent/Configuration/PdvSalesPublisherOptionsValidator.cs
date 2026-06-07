using Microsoft.Extensions.Options;

namespace SyncAgent.Configuration;

public sealed class PdvSalesPublisherOptionsValidator : IValidateOptions<PdvSalesPublisherOptions>
{
    public ValidateOptionsResult Validate(string? name, PdvSalesPublisherOptions options)
    {
        if (options.BatchSize is < 1 or > 500)
        {
            return ValidateOptionsResult.Fail($"{PdvSalesPublisherOptions.SectionName}:BatchSize must be between 1 and 500.");
        }

        return ValidateOptionsResult.Success;
    }
}
