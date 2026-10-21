using AsiBackbone.AspNetCore.DependencyInjection;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.CapabilityGrants;
using AsiBackbone.Core.Signing;
using AsiBackbone.EntityFrameworkCore.CapabilityGrants;
using AsiBackbone.Signing.ManagedKey;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Provides the Reference Application's signed capability token composition.
/// </summary>
public static class CapabilityTokenServiceExtensions
{
    /// <summary>
    /// Selects the capability grant model from <see cref="CapabilityTokenOptions.Enabled" /> and, when signed tokens
    /// are enabled, registers AsiBackbone's signed capability grant validator, public-key verifier, and EF Core use
    /// store, with the host-owned key material, issuance, and purge schedule.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment, used to refuse the sample issuance signing key in Production
    /// by default.</param>
    /// <returns>The same service collection so calls can be chained.</returns>
    /// <remarks>
    /// <para>
    /// Call after <c>AddApplicationAsiBackbone</c>, which registers the claim-backed
    /// <see cref="ClaimEndpointCapabilityGrantValidator" />, and after <c>AddApplicationDataAccess</c>, which registers
    /// the <see cref="ApplicationDbContext" /> the use store writes to. While tokens are disabled the claim validator
    /// stands. While they are enabled, it is removed and <c>AddAsiBackboneSignedCapabilityGrants</c> registers the
    /// library validator in its place, because the library refuses to register beside another validator. The choice is
    /// made here, from configuration, so exactly one validator is ever registered.
    /// </para>
    /// <para>
    /// The method body is laid out in the order a host wires signed grants, and each step is marked with who owns it.
    /// </para>
    /// <list type="table">
    /// <listheader>
    /// <term>Step</term>
    /// <description>Owner, and what this sample registers</description>
    /// </listheader>
    /// <item>
    /// <term>1. Validator</term>
    /// <description>
    /// AsiBackbone, enabled by <c>AddAsiBackboneSignedCapabilityGrants</c>: token reading and decoding, proof
    /// verification, subject, operation, scope, policy, time, and binding checks, use consumption, fail-closed denials
    /// with documented reason codes, and the grant identifier on the decision receipt. Do not reimplement any of it.
    /// </description>
    /// </item>
    /// <item>
    /// <term>2. Validator options</term>
    /// <description>
    /// Host: issuer, audience, clock skew, and the pinned key identifier, mapped here from
    /// <see cref="CapabilityTokenOptions" />.
    /// </description>
    /// </item>
    /// <item>
    /// <term>3. Request binding</term>
    /// <description>
    /// Host choice of an AsiBackbone provider: <c>AddAsiBackboneFormFieldCapabilityGrantBinding</c>. Keep it for form
    /// posts; register another <c>ICapabilityGrantRequestBindingProvider</c> for JSON bodies or route-identified
    /// resources.
    /// </description>
    /// </item>
    /// <item>
    /// <term>4. Verification keys</term>
    /// <description>
    /// AsiBackbone, enabled by <c>AddAsiBackboneKeySignatureVerification</c>: the public-key
    /// <see cref="IGovernanceSignatureVerificationService" />, with version selection for rotation and revocation. Host:
    /// the trusted public keys, from <see cref="CapabilityTokenOptions.VerificationKeys" />.
    /// </description>
    /// </item>
    /// <item>
    /// <term>5. Use store</term>
    /// <description>
    /// AsiBackbone, enabled by <c>AddAsiBackboneEfCoreCapabilityGrantUseStore</c>: the
    /// <see cref="ICapabilityGrantUseStore" /> whose consume is atomic across replicas sharing the database. Host: the
    /// <see cref="ApplicationDbContext" />, its migration, the retention grace period, and the purge schedule
    /// (<see cref="CapabilityGrantUsePurgeService" />).
    /// </description>
    /// </item>
    /// <item>
    /// <term>6. Issuance</term>
    /// <description>
    /// Host: entitlement, lifetime, the signing key, and where the issuer runs. <see cref="CapabilityTokenIssuer" />
    /// composes the AsiBackbone signing helper and token encoding with <see cref="CapabilityTokenSigningKey" />;
    /// production signs through a managed key service and may move issuance to a separate service.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// Options are validated at startup, so enabling tokens without a usable signing key, or without a trusted public
    /// key that matches it, stops the application before it serves a request.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddApplicationCapabilityTokens(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        services
            .AddOptions<CapabilityTokenOptions>()
            .BindConfiguration(CapabilityTokenOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<CapabilityTokenOptions>, CapabilityTokenOptionsValidator>();

        // The issuance endpoint checks entitlement with the claim validator in both models.
        services.TryAddScoped<ClaimEndpointCapabilityGrantValidator>();

        IConfigurationSection section = configuration.GetSection(CapabilityTokenOptions.SectionName);
        if (!section.GetValue<bool>(nameof(CapabilityTokenOptions.Enabled)))
        {
            return services;
        }

        // Issuance signs with AsiBackbone's configured-key local-development provider. Production must opt in
        // explicitly rather than reach it by default (analyzer ASIB003 asks for this guard). Verification and replay
        // storage below are AsiBackbone's production components and need no guard.
        if (environment.IsProduction()
            && !section.GetValue<bool>(nameof(CapabilityTokenOptions.AllowSampleSigningKeyInProduction)))
        {
            throw new InvalidOperationException(
                $"{CapabilityTokenOptions.SectionName}:Enabled is true in the Production environment, but the sample issuance signing key is held in process memory by the configured-key local-development provider. Sign through a managed key service, or set {CapabilityTokenOptions.SectionName}:{nameof(CapabilityTokenOptions.AllowSampleSigningKeyInProduction)} to true to accept the sample signing key deliberately.");
        }

        // 1. LIBRARY: the signed capability grant validator. AsiBackbone owns reading and decoding the token, verifying
        //    the proof, checking subject, operation, scopes, policy, validity window, and request binding, consuming the
        //    use, denying every failure with a documented reason code, and putting the grant identifier on the decision
        //    receipt. The library refuses to register beside another validator, so the claim validator is removed first.
        services.RemoveAll<IEndpointCapabilityGrantValidator>();
        services.AddAsiBackboneSignedCapabilityGrants(static _ => { });

        // 2. HOST: what the validator checks the grant against. Issuer and audience are required by the library; clock
        //    skew and key pinning are the host's policy. Production keeps this step and sets its own values. Pin only the
        //    key identifier: pinning a key version would deny outstanding tokens during a rotation.
        services.AddOptions<AsiBackbone.AspNetCore.CapabilityGrants.SignedCapabilityGrantOptions>()
            .Configure<IOptions<CapabilityTokenOptions>>(static (grantOptions, tokenOptionsAccessor) =>
            {
                CapabilityTokenOptions tokenOptions = tokenOptionsAccessor.Value;
                grantOptions.Issuer = tokenOptions.Issuer;
                grantOptions.Audience = tokenOptions.Audience;
                grantOptions.AllowedClockSkew = TimeSpan.FromSeconds(tokenOptions.AllowedClockSkewSeconds);
                grantOptions.ExpectedProofKeyId = tokenOptions.KeyId;
            });

        // 3. HOST CHOICE, LIBRARY IMPLEMENTATION: what a grant's resource binding covers. The form-field provider fits
        //    endpoints that post forms with antiforgery, as these do. Production keeps it for forms, or registers its own
        //    ICapabilityGrantRequestBindingProvider for JSON bodies or resources identified by route values.
        services.AddAsiBackboneFormFieldCapabilityGrantBinding();

        // 4. LIBRARY VERIFIER, HOST KEYS: AsiBackbone's public-key verifier checks every proof against the trusted
        //    public keys, selected by the proof's key version, so the previous version keeps verifying during a rotation
        //    and a revoked version is denied at once. Only public material is configured here; the private key never
        //    reaches verification. Production keeps this step and lists the managed key service's public keys, or
        //    registers an IGovernanceVerificationKeyResolver for a JWKS endpoint or key service.
        CapabilityTokenOptions configuredKeys = section.Get<CapabilityTokenOptions>() ?? new CapabilityTokenOptions();
        services.AddAsiBackboneKeySignatureVerification(verification => TrustConfiguredKeys(verification, configuredKeys));

        // 5. LIBRARY STORE, HOST DATABASE: replay protection in the application database, so every instance sharing it
        //    accepts a single-use grant exactly once. Records are kept until the grant expires plus the largest clock
        //    skew this configuration accepts, which is never less than the validator's. The table comes from the
        //    application's own migration, applied with the others; the store never migrates. Production keeps this step.
        services.AddAsiBackboneEfCoreCapabilityGrantUseStore<ApplicationDbContext>(static store =>
            store.RetentionGracePeriod = TimeSpan.FromSeconds(CapabilityTokenOptions.MaximumAllowedClockSkewSeconds));
        services.AddHostedService<CapabilityGrantUsePurgeService>();

        // 6. HOST: issuance. Entitlement, lifetime, the signing key, and where the issuer runs are host decisions; the
        //    issuer composes the library's signing helper and token encoding. PRODUCTION REPLACES THE SIGNING KEY: sign
        //    through a managed key service with AddAsiBackboneManagedKeySigning, or run the issuer as a separate service
        //    and keep only its public keys in step 4.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CapabilityTokenSigningKey>();
        services.AddSingleton<CapabilityTokenIssuer>();

        return services;
    }

    private static void TrustConfiguredKeys(
        KeySignatureVerificationOptions verification,
        CapabilityTokenOptions tokenOptions)
    {
        // Trust only the descriptor this application's issuer signs with.
        verification.AllowedAlgorithms.Clear();
        verification.AllowedAlgorithms.Add(CapabilityTokenSigningKey.SignatureAlgorithm);

        HashSet<string> versions = new(StringComparer.Ordinal);

        foreach (CapabilityTokenVerificationKeyOptions key in tokenOptions.VerificationKeys)
        {
            string version = key.KeyVersion?.Trim() ?? string.Empty;
            if (version.Length == 0 || !versions.Add(version))
            {
                continue;
            }

            try
            {
                verification.AddPemKey(
                    tokenOptions.KeyId,
                    version,
                    CapabilityTokenSigningKey.SignatureAlgorithm,
                    key.PublicKeyPem!,
                    isRevoked: key.Revoked);
            }
            catch (ArgumentException)
            {
                // CapabilityTokenOptionsValidator reports this entry by setting name and stops startup. Skipping it here
                // keeps an unusable key out of the trusted set and the key material out of the exception.
            }
        }
    }
}
