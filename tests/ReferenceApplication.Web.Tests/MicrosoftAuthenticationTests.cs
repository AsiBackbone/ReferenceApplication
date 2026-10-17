using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.MicrosoftAccount;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Authentication.Options;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Provides tests for Microsoft external authentication provider registration.
/// </summary>
public sealed class MicrosoftAuthenticationTests
{
    /// <summary>
    /// Verifies that the Microsoft authentication provider is not registered when disabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task DisabledMicrosoftProvider_DoesNotRegisterMicrosoftScheme()
    {
        using ServiceProvider services = CreateServiceProvider(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Providers:Microsoft:Enabled"] = "false",
            ["ReferenceApplication:Authentication:Providers:Microsoft:ClientId"] = "",
            ["ReferenceApplication:Authentication:Providers:Microsoft:ClientSecret"] = ""
        });

        IAuthenticationSchemeProvider schemeProvider = services
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync("Microsoft");

        Assert.Null(scheme);
    }

    /// <summary>
    /// Verifies that the Microsoft authentication provider is registered when enabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task EnabledMicrosoftProvider_RegistersMicrosoftScheme()
    {
        using ServiceProvider services = CreateServiceProvider(CreateEnabledMicrosoftConfiguration());

        IAuthenticationSchemeProvider schemeProvider = services
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync("Microsoft");

        Assert.NotNull(scheme);
        Assert.Equal("Microsoft", scheme.Name);
        Assert.Equal("Microsoft", scheme.DisplayName);
    }

    /// <summary>
    /// Verifies that Microsoft provider options are applied to the registered Microsoft handler.
    /// </summary>
    [Fact]
    public void EnabledMicrosoftProvider_ConfiguresMicrosoftHandlerOptions()
    {
        using ServiceProvider services = CreateServiceProvider(CreateEnabledMicrosoftConfiguration());

        IOptionsMonitor<MicrosoftAccountOptions> optionsMonitor = services
            .GetRequiredService<IOptionsMonitor<MicrosoftAccountOptions>>();

        MicrosoftAccountOptions options = optionsMonitor.Get("Microsoft");

        Assert.Equal(CookieAuthenticationDefaults.AuthenticationScheme, options.SignInScheme);
        Assert.Equal("test-microsoft-client-id", options.ClientId);
        Assert.Equal("test-microsoft-client-secret", options.ClientSecret);
        Assert.Equal("/signin-microsoft", options.CallbackPath);
        Assert.Contains("User.Read", options.Scope);
    }

    /// <summary>
    /// Verifies that Microsoft provider scopes are bound from application authentication configuration.
    /// </summary>
    [Fact]
    public void MicrosoftProviderScopes_AreBoundFromConfiguration()
    {
        using ServiceProvider services = CreateServiceProvider(CreateEnabledMicrosoftConfiguration());

        ApplicationAuthenticationOptions options = services
            .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
            .Value;

        Assert.Equal(["User.Read"], options.Providers.Microsoft.Scopes);
    }

    private static Dictionary<string, string?> CreateEnabledMicrosoftConfiguration()
    {
        return new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = "Microsoft",
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Providers:Microsoft:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:Microsoft:Scheme"] = "Microsoft",
            ["ReferenceApplication:Authentication:Providers:Microsoft:DisplayName"] = "Microsoft",
            ["ReferenceApplication:Authentication:Providers:Microsoft:ClientId"] = "test-microsoft-client-id",
            ["ReferenceApplication:Authentication:Providers:Microsoft:ClientSecret"] = "test-microsoft-client-secret",
            ["ReferenceApplication:Authentication:Providers:Microsoft:CallbackPath"] = "/signin-microsoft",
            ["ReferenceApplication:Authentication:Providers:Microsoft:Scopes:0"] = "User.Read"
        };
    }

    private static ServiceProvider CreateServiceProvider(IReadOnlyDictionary<string, string?> configurationValues)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        ServiceCollection services = [];
        services.AddLogging();
        services.AddApplicationAuthentication(configuration);

        return services.BuildServiceProvider(validateScopes: true);
    }
}
