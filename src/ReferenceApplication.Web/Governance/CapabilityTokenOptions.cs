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
/// never part of checked-in configuration. The verification keys are public, but they still decide which tokens are
/// trusted, so they come from configuration rather than source. Startup fails when tokens are enabled without a usable
/// signing key or without a trusted public key that matches it.
/// </para>
/// <para>
/// Every setting here is a host decision. <see cref="Issuer" />, <see cref="Audience" />,
/// <see cref="AllowedClockSkewSeconds" />, and <see cref="KeyId" /> are passed to AsiBackbone's validator options;
/// <see cref="VerificationKeys" /> are passed to AsiBackbone's public-key verifier; <see cref="LifetimeSeconds" />,
/// <see cref="KeyVersion" />, and <see cref="SigningKeyPem" /> apply at issuance through
/// <see cref="CapabilityTokenSigningKey" />; and <see cref="UseStorePurgeIntervalSeconds" /> schedules the purge of
/// AsiBackbone's EF Core use store.
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
    /// <remarks>
    /// The EF Core use store keeps each consumed grant until it expires plus this period, so a grant can never be
    /// purged while a validator could still accept it.
    /// </remarks>
    public const int MaximumAllowedClockSkewSeconds = 60;

    /// <summary>
    /// Gets the shortest use store purge interval accepted from configuration.
    /// </summary>
    public const int MinimumUseStorePurgeIntervalSeconds = 10;

    /// <summary>
    /// Gets the longest use store purge interval accepted from configuration.
    /// </summary>
    public const int MaximumUseStorePurgeIntervalSeconds = 86_400;

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
    /// Gets or sets the key identifier recorded in token proofs and required on them by the validator.
    /// </summary>
    public string KeyId { get; set; } = "referenceapplication-capability-token";

    /// <summary>
    /// Gets or sets the version of the current signing key. New tokens are signed with this version.
    /// </summary>
    public string KeyVersion { get; set; } = "1";

    /// <summary>
    /// Gets or sets the current PEM-encoded RSA private key, at least 2048 bits, used to sign tokens at issuance.
    /// </summary>
    /// <remarks>
    /// Verification never uses this key. Its public half must also be listed in <see cref="VerificationKeys" /> under
    /// <see cref="KeyVersion" />, so the tokens this application issues verify.
    /// </remarks>
    public string? SigningKeyPem { get; set; }

    /// <summary>
    /// Gets or sets the trusted public key versions that verify token proofs.
    /// </summary>
    /// <remarks>
    /// List the current version, and during a rotation the previous one, until every token it signed has expired plus
    /// <see cref="AllowedClockSkewSeconds" />.
    /// </remarks>
    public List<CapabilityTokenVerificationKeyOptions> VerificationKeys { get; set; } = [];

    /// <summary>
    /// Gets or sets how long an issued token remains valid, in seconds.
    /// </summary>
    public int LifetimeSeconds { get; set; } = 120;

    /// <summary>
    /// Gets or sets the clock skew tolerated when checking not-before and expiry times, in seconds.
    /// </summary>
    public int AllowedClockSkewSeconds { get; set; }

    /// <summary>
    /// Gets or sets how often consumed grants past their retention horizon are purged from the use store, in seconds.
    /// </summary>
    public int UseStorePurgeIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// Gets or sets a value indicating whether capability tokens may be enabled in the Production environment with the
    /// sample issuance signing key.
    /// </summary>
    /// <remarks>
    /// Verification and replay storage use AsiBackbone's production components, but issuance signs with AsiBackbone's
    /// configured-key local-development provider, which keeps the private key in process memory. Startup refuses it in
    /// Production unless this is set, so a deployment states the choice explicitly. A real production deployment signs
    /// through a managed key service, or issues tokens from a separate service, instead of setting this.
    /// </remarks>
    public bool AllowSampleSigningKeyInProduction { get; set; }
}
