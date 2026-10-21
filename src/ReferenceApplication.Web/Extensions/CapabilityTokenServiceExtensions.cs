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
    /// The host supplies what the library leaves to it: issuer and audience, clock skew, the signing key through
    /// <see cref="CapabilityTokenKeyRing" />, the process-local <see cref="InMemoryCapabilityGrantUseStore" />, and the
    /// form-field request binding. Options are validated at startup, so enabling tokens without a signing key stops the
    /// application before it serves a request.
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

        services.RemoveAll<IEndpointCapabilityGrantValidator>();
        services.AddAsiBackboneSignedCapabilityGrants(static _ => { });
        services.AddOptions<AsiBackbone.AspNetCore.CapabilityGrants.SignedCapabilityGrantOptions>()
            .Configure<IOptions<CapabilityTokenOptions>>(static (grantOptions, tokenOptionsAccessor) =>
            {
                CapabilityTokenOptions tokenOptions = tokenOptionsAccessor.Value;
                grantOptions.Issuer = tokenOptions.Issuer;
                grantOptions.Audience = tokenOptions.Audience;
                grantOptions.AllowedClockSkew = TimeSpan.FromSeconds(tokenOptions.AllowedClockSkewSeconds);
                grantOptions.ExpectedProofKeyId = tokenOptions.KeyId;
            });
        services.AddAsiBackboneFormFieldCapabilityGrantBinding();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<CapabilityTokenKeyRing>();
        services.AddSingleton<IGovernanceSignatureVerificationService>(static serviceProvider =>
            serviceProvider.GetRequiredService<CapabilityTokenKeyRing>());

        // Process-local replay protection. Several replicas each hold their own store, so a single-use token could be
        // accepted once per replica; a multi-replica deployment registers a shared store instead.
        services.TryAddSingleton<ICapabilityGrantUseStore, InMemoryCapabilityGrantUseStore>();
        services.AddSingleton<CapabilityTokenIssuer>();

        return services;
    }
}
