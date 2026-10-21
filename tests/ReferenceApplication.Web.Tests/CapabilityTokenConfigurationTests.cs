using System.Security.Cryptography;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.CapabilityGrants;
using AsiBackbone.Core.Signing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Extensions;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies the host-owned parts of signed capability tokens: key configuration and rotation, the Production guard,
/// and model selection.
/// </summary>
public sealed class CapabilityTokenConfigurationTests
{
    /// <summary>
    /// Verifies that during a rotation a token signed with the previous key still verifies, while the current key signs
    /// new tokens and an unknown key version is rejected.
    /// </summary>
    [Fact]
    public async Task KeyRing_AcceptsPreviousKeyDuringRotation()
    {
        using var previousKey = RSA.Create(2048);
        using var currentKey = RSA.Create(2048);
        using var before = new CapabilityTokenKeyRing(
            Microsoft.Extensions.Options.Options.Create(new CapabilityTokenOptions { Enabled = true, KeyVersion = "1", SigningKeyPem = previousKey.ExportPkcs8PrivateKeyPem() }),
            TimeProvider.System);
        using var during = new CapabilityTokenKeyRing(
            Microsoft.Extensions.Options.Options.Create(new CapabilityTokenOptions
            {
                Enabled = true,
                KeyVersion = "2",
                SigningKeyPem = currentKey.ExportPkcs8PrivateKeyPem(),
                PreviousKeyVersion = "1",
                PreviousVerificationKeyPem = previousKey.ExportSubjectPublicKeyInfoPem()
            }),
            TimeProvider.System);
        using var after = new CapabilityTokenKeyRing(
            Microsoft.Extensions.Options.Options.Create(new CapabilityTokenOptions { Enabled = true, KeyVersion = "2", SigningKeyPem = currentKey.ExportPkcs8PrivateKeyPem() }),
            TimeProvider.System);

        SignedGovernanceArtifact<CapabilityGrant> oldToken = await SignAsync(before);
        SignedGovernanceArtifact<CapabilityGrant> newToken = await SignAsync(during);

        Assert.True((await GovernanceArtifactVerifier.VerifyAsync(oldToken, during, cancellationToken: TestContext.Current.CancellationToken)).ShouldAllow);
        Assert.True((await GovernanceArtifactVerifier.VerifyAsync(newToken, during, cancellationToken: TestContext.Current.CancellationToken)).ShouldAllow);
        Assert.Equal("2", newToken.SigningMetadata.KeyVersion);
        Assert.False((await GovernanceArtifactVerifier.VerifyAsync(oldToken, after, cancellationToken: TestContext.Current.CancellationToken)).ShouldAllow);
    }

    /// <summary>
    /// Verifies that the key ring refuses to fall back to a generated key when none is configured.
    /// </summary>
    [Fact]
    public void KeyRing_WithoutConfiguredKey_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => new CapabilityTokenKeyRing(
            Microsoft.Extensions.Options.Options.Create(new CapabilityTokenOptions { Enabled = true }),
            TimeProvider.System));

        Assert.Contains("SigningKeyPem", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that option validation names each invalid setting without echoing key material.
    /// </summary>
    [Fact]
    public void OptionsValidator_ReportsInvalidSettingsWithoutEchoingKeys()
    {
        const string NotAKey = "-----BEGIN PRIVATE KEY-----\nbm90LWEta2V5\n-----END PRIVATE KEY-----";
        var validator = new CapabilityTokenOptionsValidator();

        ValidateOptionsResult disabled = validator.Validate(null, new CapabilityTokenOptions());
        ValidateOptionsResult invalid = validator.Validate(null, new CapabilityTokenOptions
        {
            Enabled = true,
            SigningKeyPem = NotAKey,
            LifetimeSeconds = 0,
            AllowedClockSkewSeconds = 61,
            PreviousKeyVersion = "1"
        });

        Assert.True(disabled.Succeeded);
        Assert.True(invalid.Failed);
        Assert.Contains("SigningKeyPem", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("LifetimeSeconds", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("AllowedClockSkewSeconds", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("must be supplied together", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("PreviousKeyVersion must differ", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("bm90LWEta2V5", invalid.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that Production refuses the sample key provider unless the deployment opts in.
    /// </summary>
    [Fact]
    public void AddApplicationCapabilityTokens_InProductionWithoutOptIn_Throws()
    {
        IConfiguration configuration = CreateConfiguration(enabled: true);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddApplicationCapabilityTokens(configuration, new TestHostEnvironment(Environments.Production)));
        IServiceCollection optedIn = new ServiceCollection()
            .AddApplicationAsiBackbone()
            .AddApplicationCapabilityTokens(
                CreateConfiguration(enabled: true, allowInProduction: true),
                new TestHostEnvironment(Environments.Production));

        Assert.Contains("AllowSampleSigningKeyInProduction", exception.Message, StringComparison.Ordinal);
        Assert.Contains(optedIn, static descriptor => descriptor.ImplementationType == typeof(SignedCapabilityGrantEndpointValidator));
    }

    /// <summary>
    /// Verifies that exactly one capability grant validator is registered for each model: the claim validator while
    /// tokens are disabled, and only the library signed validator while they are enabled.
    /// </summary>
    /// <param name="enabled">Whether signed tokens are enabled.</param>
    /// <param name="expected">The expected validator implementation.</param>
    [Theory]
    [InlineData(false, typeof(ClaimEndpointCapabilityGrantValidator))]
    [InlineData(true, typeof(SignedCapabilityGrantEndpointValidator))]
    public void AddApplicationCapabilityTokens_RegistersExactlyOneValidator(bool enabled, Type expected)
    {
        IServiceCollection services = new ServiceCollection()
            .AddApplicationAsiBackbone()
            .AddApplicationCapabilityTokens(CreateConfiguration(enabled), new TestHostEnvironment(Environments.Development));

        ServiceDescriptor validator = Assert.Single(
            services,
            static descriptor => descriptor.ServiceType == typeof(IEndpointCapabilityGrantValidator));

        Assert.Equal(expected, validator.ImplementationType);
        Assert.Equal(enabled, services.Any(static descriptor => descriptor.ServiceType == typeof(ICapabilityGrantRequestBindingProvider)));
    }

    private static ValueTask<SignedGovernanceArtifact<CapabilityGrant>> SignAsync(CapabilityTokenKeyRing ring)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var grant = CapabilityGrant.Create(
            tokenId: Guid.NewGuid().ToString("N"),
            issuer: "referenceapplication",
            audience: "referenceapplication.governed-operations",
            scopes: ["scope"],
            issuedUtc: now,
            expiresUtc: now.AddMinutes(2),
            maxUseCount: 1);

        return GovernanceArtifactSigner.SignCapabilityGrantAsync(
            grant,
            ring.Signer,
            keyId: ring.KeyId,
            keyVersion: ring.KeyVersion,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static IConfiguration CreateConfiguration(bool enabled, bool allowInProduction = false)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{CapabilityTokenOptions.SectionName}:Enabled"] = enabled ? "true" : "false",
                [$"{CapabilityTokenOptions.SectionName}:AllowSampleSigningKeyInProduction"] = allowInProduction ? "true" : "false"
            })
            .Build();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "ReferenceApplication.Web.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
