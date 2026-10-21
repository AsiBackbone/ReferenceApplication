namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Describes one trusted public key version that verifies capability token proofs.
/// </summary>
/// <remarks>
/// <para>
/// Every entry shares <see cref="CapabilityTokenOptions.KeyId" />. AsiBackbone's public-key verifier selects the entry
/// whose version matches the proof's key version, so a token signed under any listed version verifies until that entry
/// is removed or marked <see cref="Revoked" />.
/// </para>
/// <para>
/// Only public material belongs here. AsiBackbone refuses a PEM that contains a private key, and refuses RSA keys smaller
/// than 2048 bits.
/// </para>
/// </remarks>
public sealed class CapabilityTokenVerificationKeyOptions
{
    /// <summary>
    /// Gets or sets the key version this public key verifies.
    /// </summary>
    public string KeyVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the SPKI PEM-encoded RSA public key (<c>-----BEGIN PUBLIC KEY-----</c>).
    /// </summary>
    public string? PublicKeyPem { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this version is revoked.
    /// </summary>
    /// <remarks>
    /// A revoked version stays listed so that tokens signed under it are denied immediately with AsiBackbone's
    /// <c>signature.key-revoked</c> failure, instead of waiting for them to expire.
    /// </remarks>
    public bool Revoked { get; set; }
}
