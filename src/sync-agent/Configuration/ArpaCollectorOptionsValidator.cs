using Microsoft.Extensions.Options;
using SyncAgent.Contracts;

namespace SyncAgent.Configuration;

public sealed class ArpaCollectorOptionsValidator : IValidateOptions<ArpaCollectorOptions>
{
    public ValidateOptionsResult Validate(string? name, ArpaCollectorOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (options.UseRemoteConfig)
        {
            if (string.IsNullOrWhiteSpace(options.RemoteConfigCacheProtectedFile))
            {
                failures.Add($"{ArpaCollectorOptions.SectionName}:RemoteConfigCacheProtectedFile is required when UseRemoteConfig is true.");
            }

            if (options.RemoteConfigRefreshMinutes < 1)
            {
                failures.Add($"{ArpaCollectorOptions.SectionName}:RemoteConfigRefreshMinutes must be at least 1.");
            }

            if (options.RemoteConfigTimeoutSeconds < 1)
            {
                failures.Add($"{ArpaCollectorOptions.SectionName}:RemoteConfigTimeoutSeconds must be at least 1.");
            }

            if (options.BatchSize is < 1 or > 10_000)
            {
                failures.Add($"{ArpaCollectorOptions.SectionName}:BatchSize must be between 1 and 10000.");
            }

            return failures.Count == 0
                ? ValidateOptionsResult.Success
                : ValidateOptionsResult.Fail(failures);
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add($"{ArpaCollectorOptions.SectionName}:ConnectionString is required when collector is enabled.");
        }

        var passwordSourceCount = new[]
        {
            options.PasswordEnvironmentVariable,
            options.PasswordFile,
            options.PasswordProtectedFile
        }.Count(item => !string.IsNullOrWhiteSpace(item));

        if (passwordSourceCount > 1)
        {
            failures.Add($"{ArpaCollectorOptions.SectionName}:PasswordEnvironmentVariable, PasswordFile and PasswordProtectedFile are mutually exclusive.");
        }

        if (options.BatchSize is < 1 or > 10_000)
        {
            failures.Add($"{ArpaCollectorOptions.SectionName}:BatchSize must be between 1 and 10000.");
        }

        if (options.Entities.Count == 0)
        {
            failures.Add($"{ArpaCollectorOptions.SectionName}:Entities must contain at least one entity when collector is enabled.");
        }

        foreach (var entity in options.Entities)
        {
            if (string.IsNullOrWhiteSpace(entity.Name))
            {
                failures.Add($"{ArpaCollectorOptions.SectionName}:Entities:Name is required.");
            }

            if (!SyncContractValues.EntityTypes.Contains(entity.EntityType, StringComparer.Ordinal))
            {
                failures.Add($"{ArpaCollectorOptions.SectionName}:Entities:{entity.Name}:EntityType is not allowed by sync contract v1.");
            }

            if (string.IsNullOrWhiteSpace(entity.Query))
            {
                failures.Add($"{ArpaCollectorOptions.SectionName}:Entities:{entity.Name}:Query is required.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
