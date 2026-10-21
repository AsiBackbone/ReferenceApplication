namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Configures signed, short-lived, request-bound capability tokens for capability-protected endpoints.
/// </summary>
/// <remarks>
/// <para>
/// Disabled by default. While disabled, capability-protected endpoints use the claim-backed
/// <see cref="ClaimEndpointCapabilityGrantValidator" />. While enabled, the application registers AsiBackbone's signed
/// capability grant validator instead, so those endpoints require a token issued by <see cref="CapabilityTokenIssuer" />.
/// </para>
/// <para>
/// The signing key is a secret. Supply it through user secrets, an environment variable, or a secret store; it is
/// never part of checked-in configuration. Startup fails when tokens are enabled without a usable key.
/// </para>
/// <para>
/// Every setting here is a host decision. <see cref="Issuer" />, <see cref="Audience" />,
/// <see cref="AllowedClockSkewSeconds" />, and <see cref="KeyId" /> are passed to AsiBackbone's validator options;
/// <see cref="LifetimeSeconds" /> applies at issuance; the key settings feed <see cref="CapabilityTokenKeyRing" />, which a
/// production deployment replaces with a managed key service, so the key settings then no longer apply.
/// </para>
/// </remarks>
public sealed class CapabilityTokenOptions
{
    /// <summary>
    /// Gets the configuration section for capability tokens.
    /// </summary>
    public const string SectionName = "ReferenceApplication:CapabilityTokens";

    /// <summary>
    /// Gets the longest token lifetime accepted from configuration.
    /// </summary>
    public const int MaximumLifetimeSeconds = 900;

    /// <summary>
    /// Gets the largest clock skew accepted from configuration.
    /// </summary>
    public const int MaximumAllowedClockSkewSeconds = 60;

    /// <summary>
    /// Gets or sets a value indicating whether capability-protected endpoints require signed capability tokens.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the issuer written to, and required on, every token.
    /// </summary>
    public string Issuer { get; set; } = "referenceapplication";

    /// <summary>
    /// Gets or sets the audience written to, and required on, every token.
    /// </summary>
    public string Audience { get; set; } = "referenceapplication.governed-operations";

    /// <summary>
    /// Gets or sets the signing key identifier recorded in token proofs.
    /// </summary>
    public string KeyId { get; set; } = "referenceapplication-capability-token";

    /// <summary>
    /// Gets or sets the version of the current signing key. New tokens are signed with this version.
    /// </summary>
    public string KeyVersion { get; set; } = "1";

    /// <summary>
    /// Gets or sets the current PEM-encoded RSA private key, at least 2048 bits, used to sign and verify tokens.
    /// </summary>
    public string? SigningKeyPem { get; set; }

    /// <summary>
    /// Gets or sets the version of the previous signing key, accepted for verification only during a rotation.
    /// </summary>
    public string? PreviousKeyVersion { get; set; }

    /// <summary>
    /// Gets or sets the previous PEM-encoded RSA key, accepted for verification only during a rotation.
    /// </summary>
    /// <remarks>
    /// The public key is enough, because the previous key never signs.
    /// </remarks>
    public string? PreviousVerificationKeyPem { get; set; }

    /// <summary>
    /// Gets or sets how long an issued token remains valid, in seconds.
    /// </summary>
    public int LifetimeSeconds { get; set; } = 120;

    /// <summary>
    /// Gets or sets the clock skew tolerated when checking not-before and expiry times, in seconds.
    /// </summary>
    public int AllowedClockSkewSeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether capability tokens may be enabled in the Production environment with the
    /// sample configured-key provider.
    /// </summary>
    /// <remarks>
    /// The sample key provider is AsiBackbone's local-development signing service, which keeps the key in process
    /// memory. Startup refuses it in Production unless this is set, so a deployment states the choice explicitly. A
    /// real production deployment replaces the provider with a managed key service instead of setting this.
    /// </remarks>
    public bool AllowSampleSigningKeyInProduction { get; set; }
}
