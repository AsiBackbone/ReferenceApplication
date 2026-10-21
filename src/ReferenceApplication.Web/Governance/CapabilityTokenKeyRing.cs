using AsiBackbone.Core.Signing;
using AsiBackbone.Signing.LocalDevelopment;
using Microsoft.Extensions.Options;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Holds the configured capability token signing key and, during a rotation, the previous verification key.
/// </summary>
/// <remarks>
/// <para>
/// Signing and verification use AsiBackbone's configured-key <see cref="LocalDevelopmentSigningService" />, so every
/// instance of the application configured with the same key verifies tokens the others issued, and tokens survive a
/// restart. The ring only routes: verification is delegated to the provider whose key version matches the token's
/// proof, so a token signed with the previous key keeps verifying until it expires.
/// </para>
/// <para>
/// This is a sample. The key lives in process memory, with no custody, revocation, or hardware protection. A production
/// deployment supplies an <see cref="IGovernanceSignatureVerificationService" /> backed by a managed key service instead.
/// The ring is registered only after the Production guard in
/// <see cref="Extensions.CapabilityTokenServiceExtensions.AddApplicationCapabilityTokens" />.
/// </para>
/// </remarks>
[AsiBackboneProductionConfigurationReviewed]
public sealed class CapabilityTokenKeyRing : IGovernanceSignatureVerificationService, IDisposable
{
    private readonly LocalDevelopmentSigningService _current;
    private readonly LocalDevelopmentSigningService? _previous;
    private readonly string? _previousKeyVersion;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapabilityTokenKeyRing" /> class.
    /// </summary>
    /// <param name="optionsAccessor">The capability token options.</param>
    /// <param name="timeProvider">The clock recorded as the signing time.</param>
    /// <exception cref="InvalidOperationException">A configured key cannot be imported.</exception>
    public CapabilityTokenKeyRing(IOptions<CapabilityTokenOptions> optionsAccessor, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(optionsAccessor);
        ArgumentNullException.ThrowIfNull(timeProvider);

        CapabilityTokenOptions options = optionsAccessor.Value;

        // Without a configured key the provider would generate one per process, which signs tokens no other instance,
        // and no restart, can verify. Refuse instead of falling back.
        if (string.IsNullOrWhiteSpace(options.SigningKeyPem))
        {
            throw new InvalidOperationException(
                $"{CapabilityTokenOptions.SectionName}:SigningKeyPem is required when capability tokens are enabled.");
        }

        KeyId = options.KeyId.Trim();
        KeyVersion = options.KeyVersion.Trim();
        _current = CreateProvider(KeyId, KeyVersion, options.SigningKeyPem, timeProvider);

        if (!string.IsNullOrWhiteSpace(options.PreviousVerificationKeyPem)
            && !string.IsNullOrWhiteSpace(options.PreviousKeyVersion))
        {
            _previousKeyVersion = options.PreviousKeyVersion.Trim();
            _previous = CreateProvider(KeyId, _previousKeyVersion, options.PreviousVerificationKeyPem, timeProvider);
        }
    }

    /// <summary>
    /// Gets the signing key identifier.
    /// </summary>
    public string KeyId { get; }

    /// <summary>
    /// Gets the version of the key new tokens are signed with.
    /// </summary>
    public string KeyVersion { get; }

    /// <summary>
    /// Gets the signing service for new tokens.
    /// </summary>
    public IGovernanceSigningService Signer => _current;

    /// <inheritdoc />
    public ValueTask<SignatureVerificationResult> VerifyAsync(
        SignatureVerificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The current provider rejects any other key version, so a token signed with the previous key is routed to
        // the provider configured with it. An unknown version reaches the current provider and is rejected there.
        return _previous is not null
            && string.Equals(request.SigningMetadata.KeyVersion, _previousKeyVersion, StringComparison.Ordinal)
            ? _previous.VerifyAsync(request, cancellationToken)
            : _current.VerifyAsync(request, cancellationToken);
    }

    /// <summary>
    /// Checks that a configured key can be imported, without retaining it.
    /// </summary>
    /// <param name="keyPem">The PEM-encoded key.</param>
    /// <returns><see langword="true" /> when the key imports and meets the minimum size.</returns>
    internal static bool CanImport(string? keyPem)
    {
        if (string.IsNullOrWhiteSpace(keyPem))
        {
            return false;
        }

        try
        {
            using LocalDevelopmentSigningService provider = CreateProvider("validation", "validation", keyPem, TimeProvider.System);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _current.Dispose();
        _previous?.Dispose();
    }

    private static LocalDevelopmentSigningService CreateProvider(
        string keyId,
        string keyVersion,
        string? keyPem,
        TimeProvider timeProvider)
    {
        var providerOptions = LocalDevelopmentSigningOptions.Create(
            keyId: keyId,
            keyVersion: keyVersion);
        providerOptions.RsaKeyPem = keyPem;

        // Constructing the service directly, rather than through UseLocalDevelopmentSigning, keeps the provider out of
        // the general signing registrations: it signs capability tokens and nothing else.
        return new LocalDevelopmentSigningService(providerOptions, timeProvider);
    }
}
