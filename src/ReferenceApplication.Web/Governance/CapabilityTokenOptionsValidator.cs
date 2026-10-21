using AsiBackbone.Signing.ManagedKey;
using Microsoft.Extensions.Options;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Validates capability token configuration during application startup.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is required while tokens are disabled. Failure messages name the setting but never echo a key value.
/// </para>
/// <para>
/// Verification keys are checked the way AsiBackbone's public-key verifier reads them, and the current signing key must
/// match a trusted, unrevoked verification key under <see cref="CapabilityTokenOptions.KeyVersion" />. A configuration
/// that would issue tokens nothing here can verify fails at startup instead of denying every request.
/// </para>
/// </remarks>
public sealed class CapabilityTokenOptionsValidator : IValidateOptions<CapabilityTokenOptions>
{
    private const string _section = CapabilityTokenOptions.SectionName;
    private const string _verificationKeys = $"{_section}:{nameof(CapabilityTokenOptions.VerificationKeys)}";

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, CapabilityTokenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        List<string> failures = [];

        bool hasSigningKey = CapabilityTokenSigningKey.TryReadRsaKey(
            options.SigningKeyPem,
            requirePrivateKey: true,
            out byte[] signingPublicKey);
        Require(
            hasSigningKey,
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
        Require(
            options.UseStorePurgeIntervalSeconds is >= CapabilityTokenOptions.MinimumUseStorePurgeIntervalSeconds
                and <= CapabilityTokenOptions.MaximumUseStorePurgeIntervalSeconds,
            $"{_section}:UseStorePurgeIntervalSeconds must be between {CapabilityTokenOptions.MinimumUseStorePurgeIntervalSeconds} and {CapabilityTokenOptions.MaximumUseStorePurgeIntervalSeconds}.",
            failures);

        ValidateVerificationKeys(options, hasSigningKey ? signingPublicKey : null, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateVerificationKeys(
        CapabilityTokenOptions options,
        byte[]? signingPublicKey,
        List<string> failures)
    {
        List<CapabilityTokenVerificationKeyOptions> keys = options.VerificationKeys;

        if (keys.Count == 0)
        {
            failures.Add(
                $"{_verificationKeys} must list at least one trusted public key when capability tokens are enabled, including the public key of SigningKeyPem under KeyVersion.");
            return;
        }

        HashSet<string> versions = new(StringComparer.Ordinal);

        for (int index = 0; index < keys.Count; index++)
        {
            CapabilityTokenVerificationKeyOptions key = keys[index];
            string setting = $"{_verificationKeys}:{index}";
            string version = Normalize(key.KeyVersion);

            if (version.Length == 0)
            {
                failures.Add($"{setting}:KeyVersion is required.");
            }
            else if (!versions.Add(version))
            {
                failures.Add($"{_verificationKeys} lists key version '{version}' more than once.");
            }

            if (!IsTrustedPublicKey(options.KeyId, version, key.PublicKeyPem))
            {
                failures.Add(
                    $"{setting}:PublicKeyPem must be a PEM-encoded RSA public key of at least 2048 bits, with no private key.");
            }
        }

        string currentVersion = Normalize(options.KeyVersion);
        if (currentVersion.Length == 0 || signingPublicKey is null)
        {
            // The missing KeyVersion or signing key is already reported; there is nothing to match.
            return;
        }

        CapabilityTokenVerificationKeyOptions? current = keys.Find(
            key => string.Equals(Normalize(key.KeyVersion), currentVersion, StringComparison.Ordinal));

        if (current is null)
        {
            failures.Add(
                $"{_verificationKeys} must include the public key of SigningKeyPem under KeyVersion '{currentVersion}', or the tokens this application issues will not verify.");
        }
        else if (current.Revoked)
        {
            failures.Add(
                $"{_verificationKeys} marks the current KeyVersion '{currentVersion}' revoked. Rotate to a new signing key before revoking its version.");
        }
        else if (!CapabilityTokenSigningKey.TryReadRsaKey(current.PublicKeyPem, requirePrivateKey: false, out byte[] trusted)
            || !trusted.AsSpan().SequenceEqual(signingPublicKey))
        {
            failures.Add(
                $"{_verificationKeys} entry for KeyVersion '{currentVersion}' does not match the public key of SigningKeyPem, so the tokens this application issues will not verify.");
        }
    }

    private static bool IsTrustedPublicKey(string? keyId, string version, string? publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem))
        {
            return false;
        }

        try
        {
            // The same parsing AsiBackbone's verifier applies when the key is registered, including its refusal of
            // private material and of RSA keys below 2048 bits. Placeholder identity values keep a missing KeyId or
            // KeyVersion, reported separately, from masking the key check.
            _ = GovernanceVerificationKey.FromPem(
                string.IsNullOrWhiteSpace(keyId) ? "validation" : keyId,
                version.Length == 0 ? "validation" : version,
                CapabilityTokenSigningKey.SignatureAlgorithm,
                publicKeyPem);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
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
