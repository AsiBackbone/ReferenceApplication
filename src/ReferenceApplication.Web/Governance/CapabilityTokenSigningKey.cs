using System.Security.Cryptography;
using AsiBackbone.Core.Signing;
using AsiBackbone.Signing.LocalDevelopment;
using AsiBackbone.Signing.ManagedKey;
using Microsoft.Extensions.Options;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Holds the configured private key that signs capability tokens at issuance.
/// </summary>
/// <remarks>
/// <para>
/// Issuance signing is a host decision; verification is not. Relying endpoints verify proofs with AsiBackbone's
/// <c>PublicKeySignatureVerificationService</c> over the public keys in
/// <see cref="CapabilityTokenOptions.VerificationKeys" />, and never with this type. This type only signs.
/// </para>
/// <para>
/// Signing uses AsiBackbone's configured-key <see cref="LocalDevelopmentSigningService" />, which signs RSA-PSS with
/// SHA-256. It records the provider-neutral descriptor <see cref="ManagedKeySignatureAlgorithms.RsaPssSha256" />, the
/// descriptor the public-key verifier trusts for RSA, so every instance trusting the matching public key verifies the
/// tokens this one issues, across restarts. Signing failures throw rather than produce an unsigned token.
/// </para>
/// <para>
/// This is a sample. The private key lives in process memory, with no custody or hardware protection. A production
/// deployment signs through a managed key service with <c>AddAsiBackboneManagedKeySigning</c>, or issues tokens from a
/// separate service, and keeps only the public keys here. The type is registered only after the Production guard in
/// <see cref="Extensions.CapabilityTokenServiceExtensions.AddApplicationCapabilityTokens" />.
/// </para>
/// </remarks>
[AsiBackboneProductionConfigurationReviewed]
public sealed class CapabilityTokenSigningKey : IDisposable
{
    /// <summary>
    /// Gets the signature algorithm descriptor recorded in token proofs and trusted by the verifier.
    /// </summary>
    public const string SignatureAlgorithm = ManagedKeySignatureAlgorithms.RsaPssSha256;

    /// <summary>
    /// Gets the provider descriptor recorded in token proofs.
    /// </summary>
    public const string ProviderName = "referenceapplication-configured-key";

    private readonly LocalDevelopmentSigningService _signer;

    /// <summary>
    /// Initializes a new instance of the <see cref="CapabilityTokenSigningKey" /> class.
    /// </summary>
    /// <param name="optionsAccessor">The capability token options.</param>
    /// <param name="timeProvider">The clock recorded as the signing time.</param>
    /// <exception cref="InvalidOperationException">No signing key is configured, or it cannot be imported.</exception>
    public CapabilityTokenSigningKey(IOptions<CapabilityTokenOptions> optionsAccessor, TimeProvider timeProvider)
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

        var providerOptions = LocalDevelopmentSigningOptions.Create(
            providerName: ProviderName,
            keyId: KeyId,
            keyVersion: KeyVersion,
            signatureAlgorithm: SignatureAlgorithm,
            returnUnsignedOnFailure: false);
        providerOptions.RsaKeyPem = options.SigningKeyPem;

        // Constructing the service directly, rather than through UseLocalDevelopmentSigning, keeps the provider out of
        // the general signing registrations: it signs capability tokens and nothing else.
        _signer = new LocalDevelopmentSigningService(providerOptions, timeProvider);
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
    public IGovernanceSigningService Signer => _signer;

    /// <inheritdoc />
    public void Dispose()
    {
        _signer.Dispose();
    }

    /// <summary>
    /// Reads an unencrypted PEM-encoded RSA key of at least 2048 bits and returns its public key, without retaining it.
    /// </summary>
    /// <param name="keyPem">The PEM-encoded key.</param>
    /// <param name="requirePrivateKey">Whether the PEM must carry the private key.</param>
    /// <param name="subjectPublicKeyInfo">The DER-encoded SubjectPublicKeyInfo of the key, when it can be read.</param>
    /// <returns><see langword="true" /> when the key can be read and meets the requirements.</returns>
    internal static bool TryReadRsaKey(string? keyPem, bool requirePrivateKey, out byte[] subjectPublicKeyInfo)
    {
        subjectPublicKeyInfo = [];

        if (string.IsNullOrWhiteSpace(keyPem))
        {
            return false;
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(keyPem);

            if (rsa.KeySize < LocalDevelopmentSigningOptions.MinimumKeySizeBits)
            {
                return false;
            }

            if (requirePrivateKey)
            {
                // Throws when only public material was imported.
                _ = rsa.ExportParameters(includePrivateParameters: true);
            }

            subjectPublicKeyInfo = rsa.ExportSubjectPublicKeyInfo();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            return false;
        }
    }
}
