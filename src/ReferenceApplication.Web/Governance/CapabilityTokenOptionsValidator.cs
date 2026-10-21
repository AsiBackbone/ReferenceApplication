using Microsoft.Extensions.Options;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Validates capability token configuration during application startup.
/// </summary>
/// <remarks>
/// Nothing is required while tokens are disabled. Failure messages name the setting but never echo a key value.
/// </remarks>
public sealed class CapabilityTokenOptionsValidator : IValidateOptions<CapabilityTokenOptions>
{
    private const string _section = CapabilityTokenOptions.SectionName;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CapabilityTokenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        Require(
            CapabilityTokenKeyRing.CanImport(options.SigningKeyPem),
            $"{_section}:SigningKeyPem is required when capability tokens are enabled. Supply an unencrypted PEM-encoded RSA private key of at least 2048 bits through user secrets, an environment variable, or a secret store; never commit it.",
            failures);
        Require(
            !string.IsNullOrWhiteSpace(options.Issuer),
            $"{_section}:Issuer is required when capability tokens are enabled.",
            failures);
        Require(
            !string.IsNullOrWhiteSpace(options.Audience),
            $"{_section}:Audience is required when capability tokens are enabled.",
            failures);
        Require(
            !string.IsNullOrWhiteSpace(options.KeyId),
            $"{_section}:KeyId is required when capability tokens are enabled.",
            failures);
        Require(
            !string.IsNullOrWhiteSpace(options.KeyVersion),
            $"{_section}:KeyVersion is required when capability tokens are enabled.",
            failures);
        Require(
            options.LifetimeSeconds is >= 1 and <= CapabilityTokenOptions.MaximumLifetimeSeconds,
            $"{_section}:LifetimeSeconds must be between 1 and {CapabilityTokenOptions.MaximumLifetimeSeconds}.",
            failures);
        Require(
            options.AllowedClockSkewSeconds is >= 0 and <= CapabilityTokenOptions.MaximumAllowedClockSkewSeconds,
            $"{_section}:AllowedClockSkewSeconds must be between 0 and {CapabilityTokenOptions.MaximumAllowedClockSkewSeconds}.",
            failures);

        bool hasPreviousKey = !string.IsNullOrWhiteSpace(options.PreviousVerificationKeyPem);
        bool hasPreviousVersion = !string.IsNullOrWhiteSpace(options.PreviousKeyVersion);
        Require(
            hasPreviousKey == hasPreviousVersion,
            $"{_section}:PreviousVerificationKeyPem and {_section}:PreviousKeyVersion must be supplied together.",
            failures);
        Require(
            !hasPreviousKey || CapabilityTokenKeyRing.CanImport(options.PreviousVerificationKeyPem),
            $"{_section}:PreviousVerificationKeyPem must be an unencrypted PEM-encoded RSA key of at least 2048 bits.",
            failures);
        Require(
            !hasPreviousVersion
                || !string.Equals(Normalize(options.PreviousKeyVersion), Normalize(options.KeyVersion), StringComparison.Ordinal),
            $"{_section}:PreviousKeyVersion must differ from {_section}:KeyVersion.",
            failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static string Normalize(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static void Require(bool condition, string failure, List<string> failures)
    {
        if (!condition)
        {
            failures.Add(failure);
        }
    }
}
