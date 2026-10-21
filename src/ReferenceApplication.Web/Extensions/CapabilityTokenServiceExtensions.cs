using AsiBackbone.AspNetCore.DependencyInjection;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.CapabilityGrants;
using AsiBackbone.Core.Signing;
using AsiBackbone.Storage.InMemory.CapabilityGrants;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Provides the Reference Application's signed capability token composition.
/// </summary>
public static class CapabilityTokenServiceExtensions
{
    /// <summary>
    /// Selects the capability grant model from <see cref="CapabilityTokenOptions.Enabled" /> and, when signed tokens
    /// are enabled, registers AsiBackbone's signed capability grant validator with the host-owned key, use store, and
    /// issuance services.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment, used to refuse the sample key provider in Production by default.</param>
    /// <returns>The same service collection so calls can be chained.</returns>
    /// <remarks>
    /// <para>
    /// Call after <c>AddApplicationAsiBackbone</c>, which registers the claim-backed
    /// <see cref="ClaimEndpointCapabilityGrantValidator" />. While tokens are disabled that registration stands. While
    /// they are enabled, it is removed and <c>AddAsiBackboneSignedCapabilityGrants</c> registers the library validator
    /// in its place, because the library refuses to register beside another validator. The choice is made here, from
    /// configuration, so exactly one validator is ever registered.
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
    /// <term>4. Verification key</term>
    /// <description>
    /// Host: <see cref="CapabilityTokenKeyRing" /> over AsiBackbone's configured-key local-development provider.
    /// Production replaces it with an <see cref="IGovernanceSignatureVerificationService" /> backed by a managed key
    /// service.
    /// </description>
    /// </item>
    /// <item>
    /// <term>5. Use store</term>
    /// <description>
    /// Host: AsiBackbone's process-local <see cref="InMemoryCapabilityGrantUseStore" />. Production with more than one
    /// replica replaces it with a shared <see cref="ICapabilityGrantUseStore" /> whose consume is atomic.
    /// </description>
    /// </item>
    /// <item>
    /// <term>6. Issuance</term>
    /// <description>
    /// Host: entitlement, lifetime, and where the issuer runs. <see cref="CapabilityTokenIssuer" /> composes the
    /// AsiBackbone signing helper and token encoding; production may move it to a separate service.
    /// </description>
    /// </item>
    /// </list>
    /// <para>
    /// Options are validated at startup, so enabling tokens without a signing key stops the application before it serves
    /// a request.
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

        // The sample key provider is AsiBackbone's local-development signing service. Production must opt in
        // explicitly rather than reach it by default (analyzer ASIB003 asks for this guard).
        if (environment.IsProduction()
            && !section.GetValue<bool>(nameof(CapabilityTokenOptions.AllowSampleSigningKeyInProduction)))
        {
            throw new InvalidOperationException(
                $"{CapabilityTokenOptions.SectionName}:Enabled is true in the Production environment, but the sample configured-key provider keeps the signing key in process memory. Register a managed key provider, or set {CapabilityTokenOptions.SectionName}:{nameof(CapabilityTokenOptions.AllowSampleSigningKeyInProduction)} to true to accept the sample provider deliberately.");
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

        // 4. HOST: the key that verifies proofs, and signs at issuance. The sample wraps AsiBackbone's configured-key
        //    local-development provider in a key ring that adds one previous key for rotation. PRODUCTION REPLACES THIS
        //    STEP: register an IGovernanceSignatureVerificationService backed by a managed key service, and sign issued
        //    tokens with the matching IGovernanceSigningService.
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CapabilityTokenKeyRing>();
        services.AddSingleton<IGovernanceSignatureVerificationService>(static serviceProvider =>
            serviceProvider.GetRequiredService<CapabilityTokenKeyRing>());

        // 5. HOST: replay protection. AsiBackbone's in-memory store is process-local, so several replicas would each
        //    accept a single-use token once. PRODUCTION WITH MORE THAN ONE REPLICA REPLACES THIS STEP: register a shared
        //    ICapabilityGrantUseStore whose consume is atomic across replicas and that keeps a consumed grant until it
        //    expires plus the allowed clock skew.
        services.TryAddSingleton<ICapabilityGrantUseStore, InMemoryCapabilityGrantUseStore>();

        // 6. HOST: issuance. Entitlement, lifetime, and where the issuer runs are host decisions; the issuer composes the
        //    library's signing helper and token encoding. Production keeps the decisions and may run the issuer as a
        //    separate service, sharing only the public verification key with this one.
        services.AddSingleton<CapabilityTokenIssuer>();

        return services;
    }
}
