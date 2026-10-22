using ReferenceApplication.Web.Authentication.Demo;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Governance.Demonstration;
using ReferenceApplication.Web.Governance.FailureScenarios;
using ReferenceApplication.Web.Governance.Pipeline;
using ReferenceApplication.Web.Governance.Replay;
using ReferenceApplication.Web.Governance.Simulation;
using ReferenceApplication.Web.Options;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Provides startup-only diagnostics for security-relevant supported deployment postures.
/// </summary>
public static class StartupSecurityPostureExtensions
{
    private const string _authenticationEnabledConfigurationKey =
        ApplicationAuthenticationOptions.SectionName + ":Enabled";

    private const string _anonymousHealthEndpoints =
        "/health, /health/ready, /health/live, /health/audit-integrity";

    private const string _dataProtectionKeyRingPathConfigurationKey =
        ApplicationDataProtectionOptions.SectionName + ":" + nameof(ApplicationDataProtectionOptions.KeyRingPath);

    private const string _dataProtectionKeyEncryptionCertificatePathConfigurationKey =
        ApplicationDataProtectionOptions.SectionName + ":" + nameof(ApplicationDataProtectionOptions.KeyEncryptionCertificatePath);

    private const string _allowedHostsConfigurationKey = "AllowedHosts";

    private static readonly Action<ILogger, string, Exception?> _logPermissiveAllowedHosts =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1005, "PermissiveAllowedHosts"),
            "Security posture: {ConfigurationKey} allows every host, so ASP.NET Core host filtering accepts any Host " +
            "header. Set it to the public host names this deployment serves, such as \"app.example.com;www.example.com\".");

    private static readonly Action<ILogger, string, string, Exception?> _logDataProtectionKeyRingUnderContentRoot =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1003, "DataProtectionKeyRingRelativePath"),
            "Security posture: {ConfigurationKey} is the relative path '{KeyRingPath}', so the Data Protection key ring " +
            "is stored under the application content root. In containers and orchestrated deployments that location is " +
            "usually replaced with the application, which invalidates authentication cookies and antiforgery tokens. " +
            "Configure an absolute path on durable, access-restricted storage shared by every replica.");

    private static readonly Action<ILogger, string, Exception?> _logDataProtectionKeysNotEncrypted =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1004, "DataProtectionKeysNotEncryptedAtRest"),
            "Security posture: {ConfigurationKey} is not set, so Data Protection key-ring files are not encrypted by the " +
            "application. On Linux and macOS they are written in plain text; on Windows they are protected with DPAPI for " +
            "the current user and cannot be shared across machines. Configure a key-encryption certificate or confirm " +
            "that storage-level encryption and access controls protect the key ring.");

    private static readonly Action<ILogger, string, Exception?> _logAuthenticationDisabled =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1001, "AuthenticationDisabled"),
            "Security posture: authentication is intentionally disabled by {ConfigurationKey}. " +
            "This is a supported configuration, not an authentication framework failure. " +
            "Review deployment exposure and authorization expectations before production use.");

    private static readonly Action<ILogger, string, string, Exception?> _logDemoIdentityActive =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(1006, "DevelopmentDemoIdentityActive"),
            "Security posture: Development-only demo identity controls are active at {SignInPath} because " +
            "{ConfigurationKey} is enabled. These controls issue a local cookie with selectable walkthrough claims; " +
            "do not expose this host beyond a trusted development environment.");

    private static readonly Action<ILogger, string, Exception?> _logAnonymousProductionHealthEndpoints =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1002, "AnonymousProductionHealthEndpoints"),
            "Security posture: health endpoints {HealthEndpoints} are intentionally mapped with anonymous access in Production. " +
            "Anonymous probes are supported for infrastructure health checks; confirm reverse-proxy, ingress, firewall, " +
            "or service-mesh routing limits external reachability as intended.");

    private static readonly Action<ILogger, Exception?> _logNoDevelopmentOnlyComponents =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1007, "NoDevelopmentOnlyComponents"),
            "Security posture: no Development-only components are registered.");

    private static readonly Action<ILogger, string, Exception?> _logDevelopmentOnlyComponents =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1008, "DevelopmentOnlyComponentsRegistered"),
            "Security posture: Development-only components are registered: {Components}. They exist for local " +
            "walkthroughs; do not expose this host beyond a trusted development environment.");

    private static readonly (string Name, Type ServiceType)[] _developmentOnlyComponents =
    [
        ("demo identity", typeof(IDemoIdentityService)),
        ("load demonstration", typeof(IGovernanceLoadDemonstrationService)),
        ("policy simulator", typeof(IPolicySimulator)),
        ("decision replay", typeof(IDecisionReplayService)),
        ("pipeline inspector", typeof(IPipelineInspector)),
        ("failure scenarios", typeof(IFailureScenarioService))
    ];

    /// <summary>
    /// Emits structured startup diagnostics for supported security postures that require
    /// operator awareness.
    /// </summary>
    /// <param name="app">The application whose startup posture is being reported.</param>
    /// <returns>The original <see cref="WebApplication"/> for chaining.</returns>
    public static WebApplication LogApplicationSecurityPosture(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        LogApplicationSecurityPosture(app.Logger, app.Configuration, app.Environment);
        LogDevelopmentOnlyComponents(app.Logger, app.Services);

        return app;
    }

    /// <summary>
    /// Lists the Development-only components the host's service provider has registered.
    /// </summary>
    /// <param name="services">The application's root service provider.</param>
    /// <returns>The display names of the registered components, in a fixed order; empty when none is registered.</returns>
    /// <remarks>
    /// The answer comes from the running host's own registrations, not from configuration, so a deployment can prove
    /// from its startup log that none of the demo identity or governance sandbox components is present.
    /// </remarks>
    public static IReadOnlyList<string> GetRegisteredDevelopmentOnlyComponents(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        IServiceProviderIsService? isService = services.GetService<IServiceProviderIsService>();
        return isService is null
            ? []
            : [.. _developmentOnlyComponents
                .Where(component => isService.IsService(component.ServiceType))
                .Select(static component => component.Name)];
    }

    /// <summary>
    /// Reports which Development-only components the host's service provider has registered.
    /// </summary>
    /// <param name="logger">The startup logger.</param>
    /// <param name="services">The application's root service provider.</param>
    public static void LogDevelopmentOnlyComponents(ILogger logger, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(services);

        IReadOnlyList<string> registered = GetRegisteredDevelopmentOnlyComponents(services);
        if (registered.Count == 0)
        {
            _logNoDevelopmentOnlyComponents(logger, null);
        }
        else
        {
            _logDevelopmentOnlyComponents(logger, string.Join(", ", registered), null);
        }
    }

    /// <summary>
    /// Emits structured startup diagnostics using the supplied application services.
    /// This overload keeps the posture rules independently testable.
    /// </summary>
    /// <param name="logger">The startup logger.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    public static void LogApplicationSecurityPosture(
        ILogger logger,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        bool authenticationEnabled = configuration.GetValue<bool>(
            _authenticationEnabledConfigurationKey);

        if (!authenticationEnabled)
        {
            _logAuthenticationDisabled(
                logger,
                _authenticationEnabledConfigurationKey,
                null);
        }

        if (DemoIdentityServiceExtensions.IsDemoIdentityActive(configuration, environment))
        {
            _logDemoIdentityActive(
                logger,
                DemoIdentityOptions.SignInPath,
                $"{DemoIdentityOptions.SectionName}:{nameof(DemoIdentityOptions.Enabled)}",
                null);
        }

        if (environment.IsProduction())
        {
            _logAnonymousProductionHealthEndpoints(
                logger,
                _anonymousHealthEndpoints,
                null);
        }

        if (!environment.IsDevelopment())
        {
            LogHostFilteringPosture(logger, configuration);
            LogDataProtectionPosture(logger, configuration);
        }
    }

    private static void LogHostFilteringPosture(ILogger logger, IConfiguration configuration)
    {
        string? allowedHosts = configuration[_allowedHostsConfigurationKey]?.Trim();

        // An absent value and "*" both allow every Host header, because host filtering defaults to allowing all hosts.
        bool allowsEveryHost = string.IsNullOrEmpty(allowedHosts) ||
            allowedHosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(host => string.Equals(host, "*", StringComparison.Ordinal));

        if (allowsEveryHost)
        {
            _logPermissiveAllowedHosts(logger, _allowedHostsConfigurationKey, null);
        }
    }

    private static void LogDataProtectionPosture(ILogger logger, IConfiguration configuration)
    {
        string keyRingPath = configuration[_dataProtectionKeyRingPathConfigurationKey]?.Trim() is { Length: > 0 } configuredPath
            ? configuredPath
            : new ApplicationDataProtectionOptions().KeyRingPath;

        if (!Path.IsPathFullyQualified(keyRingPath))
        {
            _logDataProtectionKeyRingUnderContentRoot(
                logger,
                _dataProtectionKeyRingPathConfigurationKey,
                keyRingPath,
                null);
        }

        if (string.IsNullOrWhiteSpace(configuration[_dataProtectionKeyEncryptionCertificatePathConfigurationKey]))
        {
            _logDataProtectionKeysNotEncrypted(
                logger,
                _dataProtectionKeyEncryptionCertificatePathConfigurationKey,
                null);
        }
    }
}
