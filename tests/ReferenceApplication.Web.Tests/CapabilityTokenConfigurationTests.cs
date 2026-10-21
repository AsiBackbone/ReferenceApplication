using System.Security.Cryptography;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.CapabilityGrants;
using AsiBackbone.Core.Signing;
using AsiBackbone.EntityFrameworkCore.CapabilityGrants;
using AsiBackbone.Signing.ManagedKey;
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
/// and model selection, and that verification and replay storage are AsiBackbone's components.
/// </summary>
public sealed class CapabilityTokenConfigurationTests
{
    /// <summary>
    /// Verifies that AsiBackbone's public-key verifier, configured only with public keys, accepts a token signed with
    /// the previous key during a rotation, rejects it once that version is removed, and rejects it at once when the
    /// version is revoked.
    /// </summary>
    [Fact]
    public async Task Verification_UsesConfiguredPublicKeysThroughRotation()
    {
        using var previousKey = RSA.Create(2048);
        using var currentKey = RSA.Create(2048);
        string previousPublicKey = previousKey.ExportSubjectPublicKeyInfoPem();
        string currentPublicKey = currentKey.ExportSubjectPublicKeyInfoPem();

        SignedGovernanceArtifact<CapabilityGrant> oldToken = await SignAsync(previousKey, "1");
        SignedGovernanceArtifact<CapabilityGrant> newToken = await SignAsync(currentKey, "2");

        await using ServiceProvider during = CreateServices(("2", currentPublicKey, false), ("1", previousPublicKey, false));
        await using ServiceProvider after = CreateServices(("2", currentPublicKey, false));
        await using ServiceProvider revoked = CreateServices(("2", currentPublicKey, false), ("1", previousPublicKey, true));

        IGovernanceSignatureVerificationService verifier = during.GetRequiredService<IGovernanceSignatureVerificationService>();
        Assert.IsType<PublicKeySignatureVerificationService>(verifier);
        Assert.True((await VerifyAsync(oldToken, during)).ShouldAllow);
        Assert.True((await VerifyAsync(newToken, during)).ShouldAllow);
        Assert.Equal(CapabilityTokenSigningKey.SignatureAlgorithm, newToken.SigningMetadata.SignatureAlgorithm);
        Assert.Equal("2", newToken.SigningMetadata.KeyVersion);

        VerificationPolicyOutcome removed = await VerifyAsync(oldToken, after);
        Assert.False(removed.ShouldAllow);
        Assert.Equal(SignatureVerificationCategory.UnknownKeyVersion, removed.Category);

        VerificationPolicyOutcome revokedOutcome = await VerifyAsync(oldToken, revoked);
        Assert.False(revokedOutcome.ShouldAllow);
        Assert.Equal(SignatureVerificationCategory.RevokedKey, revokedOutcome.Category);
        Assert.True((await VerifyAsync(newToken, revoked)).ShouldAllow);
    }

    /// <summary>
    /// Verifies that the issuance signing key refuses to fall back to a generated key when none is configured.
    /// </summary>
    [Fact]
    public void SigningKey_WithoutConfiguredKey_Throws()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => new CapabilityTokenSigningKey(
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
        using var privateKey = RSA.Create(2048);
        var validator = new CapabilityTokenOptionsValidator();

        ValidateOptionsResult disabled = validator.Validate(null, new CapabilityTokenOptions());
        ValidateOptionsResult invalid = validator.Validate(null, new CapabilityTokenOptions
        {
            Enabled = true,
            SigningKeyPem = NotAKey,
            LifetimeSeconds = 0,
            AllowedClockSkewSeconds = 61,
            UseStorePurgeIntervalSeconds = 1,
            VerificationKeys =
            [
                new CapabilityTokenVerificationKeyOptions { KeyVersion = "1", PublicKeyPem = NotAKey },
                new CapabilityTokenVerificationKeyOptions { KeyVersion = "1", PublicKeyPem = privateKey.ExportPkcs8PrivateKeyPem() },
                new CapabilityTokenVerificationKeyOptions { PublicKeyPem = privateKey.ExportSubjectPublicKeyInfoPem() }
            ]
        });

        Assert.True(disabled.Succeeded);
        Assert.True(invalid.Failed);
        Assert.Contains("SigningKeyPem", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("LifetimeSeconds", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("AllowedClockSkewSeconds", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("UseStorePurgeIntervalSeconds", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("VerificationKeys:0:PublicKeyPem", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("lists key version '1' more than once", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("VerificationKeys:1:PublicKeyPem", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("VerificationKeys:2:KeyVersion is required", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("bm90LWEta2V5", invalid.FailureMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN", invalid.FailureMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that startup validation requires the current signing key to be trusted, unrevoked, under its own
    /// version, so the application never issues tokens it cannot verify.
    /// </summary>
    [Fact]
    public void OptionsValidator_RequiresTrustedPublicKeyForCurrentSigningKey()
    {
        using var signingKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        var validator = new CapabilityTokenOptionsValidator();

        ValidateOptionsResult none = validator.Validate(null, CreateOptions(signingKey));
        ValidateOptionsResult missing = validator.Validate(
            null,
            CreateOptions(signingKey, ("0", otherKey.ExportSubjectPublicKeyInfoPem(), false)));
        ValidateOptionsResult mismatched = validator.Validate(
            null,
            CreateOptions(signingKey, ("1", otherKey.ExportSubjectPublicKeyInfoPem(), false)));
        ValidateOptionsResult revoked = validator.Validate(
            null,
            CreateOptions(signingKey, ("1", signingKey.ExportSubjectPublicKeyInfoPem(), true)));
        ValidateOptionsResult valid = validator.Validate(
            null,
            CreateOptions(
                signingKey,
                ("1", signingKey.ExportSubjectPublicKeyInfoPem(), false),
                ("0", otherKey.ExportSubjectPublicKeyInfoPem(), false)));

        Assert.Contains("must list at least one trusted public key", none.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("must include the public key of SigningKeyPem under KeyVersion '1'", missing.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("does not match the public key of SigningKeyPem", mismatched.FailureMessage, StringComparison.Ordinal);
        Assert.Contains("marks the current KeyVersion '1' revoked", revoked.FailureMessage, StringComparison.Ordinal);
        Assert.True(valid.Succeeded, valid.FailureMessage);
    }

    /// <summary>
    /// Verifies that Production refuses the sample issuance signing key unless the deployment opts in.
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

    /// <summary>
    /// Verifies that, while tokens are enabled, verification and replay storage are registered only through
    /// AsiBackbone's extensions, with the purge scheduled, and that nothing is registered while they are disabled.
    /// </summary>
    /// <param name="enabled">Whether signed tokens are enabled.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddApplicationCapabilityTokens_UsesLibraryVerifierAndUseStore(bool enabled)
    {
        IServiceCollection services = new ServiceCollection()
            .AddApplicationAsiBackbone()
            .AddApplicationCapabilityTokens(CreateConfiguration(enabled), new TestHostEnvironment(Environments.Development));

        List<ServiceDescriptor> verifiers = [.. services.Where(static descriptor =>
            descriptor.ServiceType == typeof(IGovernanceSignatureVerificationService))];
        List<ServiceDescriptor> useStores = [.. services.Where(static descriptor =>
            descriptor.ServiceType == typeof(ICapabilityGrantUseStore))];

        if (!enabled)
        {
            Assert.Empty(verifiers);
            Assert.Empty(useStores);
            Assert.DoesNotContain(services, static descriptor => descriptor.ImplementationType == typeof(CapabilityGrantUsePurgeService));
            return;
        }

        Assert.Single(verifiers);
        Assert.Single(useStores);
        Assert.Contains(services, static descriptor => descriptor.ServiceType == typeof(PublicKeySignatureVerificationService));
        Assert.Contains(services, static descriptor => descriptor.ServiceType == typeof(EfCoreCapabilityGrantUseStore));
        Assert.Contains(services, static descriptor => descriptor.ImplementationType == typeof(CapabilityGrantUsePurgeService));
    }

    private static ServiceProvider CreateServices(params (string Version, string PublicKeyPem, bool Revoked)[] keys)
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal)
        {
            [$"{CapabilityTokenOptions.SectionName}:Enabled"] = "true"
        };

        for (int index = 0; index < keys.Length; index++)
        {
            string prefix = $"{CapabilityTokenOptions.SectionName}:VerificationKeys:{index}";
            settings[$"{prefix}:KeyVersion"] = keys[index].Version;
            settings[$"{prefix}:PublicKeyPem"] = keys[index].PublicKeyPem;
            settings[$"{prefix}:Revoked"] = keys[index].Revoked ? "true" : "false";
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new ServiceCollection()
            .AddApplicationAsiBackbone()
            .AddApplicationCapabilityTokens(configuration, new TestHostEnvironment(Environments.Development))
            .BuildServiceProvider();
    }

    private static ValueTask<VerificationPolicyOutcome> VerifyAsync(
        SignedGovernanceArtifact<CapabilityGrant> token,
        IServiceProvider services)
    {
        return GovernanceArtifactVerifier.VerifyAsync(
            token,
            services.GetRequiredService<IGovernanceSignatureVerificationService>(),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<SignedGovernanceArtifact<CapabilityGrant>> SignAsync(RSA key, string keyVersion)
    {
        using var signingKey = new CapabilityTokenSigningKey(
            Microsoft.Extensions.Options.Options.Create(new CapabilityTokenOptions
            {
                Enabled = true,
                KeyVersion = keyVersion,
                SigningKeyPem = key.ExportPkcs8PrivateKeyPem()
            }),
            TimeProvider.System);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var grant = CapabilityGrant.Create(
            tokenId: Guid.NewGuid().ToString("N"),
            issuer: "referenceapplication",
            audience: "referenceapplication.governed-operations",
            scopes: ["scope"],
            issuedUtc: now,
            expiresUtc: now.AddMinutes(2),
            maxUseCount: 1);

        return await GovernanceArtifactSigner.SignCapabilityGrantAsync(
            grant,
            signingKey.Signer,
            keyId: signingKey.KeyId,
            keyVersion: signingKey.KeyVersion,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static CapabilityTokenOptions CreateOptions(
        RSA signingKey,
        params (string Version, string PublicKeyPem, bool Revoked)[] keys)
    {
        return new CapabilityTokenOptions
        {
            Enabled = true,
            KeyVersion = "1",
            SigningKeyPem = signingKey.ExportPkcs8PrivateKeyPem(),
            VerificationKeys =
            [
                .. keys.Select(static key => new CapabilityTokenVerificationKeyOptions
                {
                    KeyVersion = key.Version,
                    PublicKeyPem = key.PublicKeyPem,
                    Revoked = key.Revoked
                })
            ]
        };
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
