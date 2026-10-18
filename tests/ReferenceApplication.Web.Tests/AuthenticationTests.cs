using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Authentication.Options;
using ReferenceApplication.Web.Authentication.Providers.GitHub;
using ReferenceApplication.Web.Authentication.Providers.Google;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Provides integration tests for the application authentication configuration and baseline cookie authentication behavior.
/// </summary>
public sealed class AuthenticationTests
{
    /// <summary>
    /// Test data for enabled external authentication provider configurations. Each entry includes the provider key, display name, and a set of configuration values required to enable the provider. This data is used to verify that the application correctly binds and validates external authentication provider settings from configuration. The test cases cover various providers such as OpenIdConnect, SAML2, Google, and GitHub, ensuring that each provider's specific configuration requirements are met when enabled.
    /// </summary>
    public static TheoryData<string, string, string> EnabledExternalProviderConfigurations =>
        new()
        {
            { "OpenIdConnect", "OpenIdConnect", "OpenID Connect" },
            { "Saml2", "Saml2", "SAML2" },
            { "Google", "Google", "Google" },
            { "GitHub", "GitHub", "GitHub" }
        };

    /// <summary>
    /// Verifies that authentication options are bound from configuration into the options model.
    /// </summary>
    [Fact]
    public void AuthenticationOptions_AreBoundFromConfiguration()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Cookie:LoginPath"] = "/Account/Login",
            ["ReferenceApplication:Authentication:Cookie:LogoutPath"] = "/Account/Logout",
            ["ReferenceApplication:Authentication:Cookie:AccessDeniedPath"] = "/Account/AccessDenied",
            ["ReferenceApplication:Authentication:Cookie:ExpireMinutes"] = "90",
            ["ReferenceApplication:Authentication:Cookie:SlidingExpiration"] = "false",

            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scheme"] = "OpenIdConnect",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:DisplayName"] = "OpenID Connect",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Authority"] = "https://login.example.test",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ClientId"] = "test-oidc-client-id",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:CallbackPath"] = "/signin-oidc",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ResponseType"] = "code",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scopes:0"] = "openid",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scopes:1"] = "profile",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scopes:2"] = "email",

            ["ReferenceApplication:Authentication:Providers:Saml2:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:Saml2:Scheme"] = "Saml2",
            ["ReferenceApplication:Authentication:Providers:Saml2:DisplayName"] = "SAML2",
            ["ReferenceApplication:Authentication:Providers:Saml2:EntityId"] = "https://ReferenceApplication.example.test/saml2",
            ["ReferenceApplication:Authentication:Providers:Saml2:MetadataUrl"] = "https://idp.example.test/metadata",
            ["ReferenceApplication:Authentication:Providers:Saml2:ModulePath"] = "/custom-saml2-acs",

            ["ReferenceApplication:Authentication:Providers:Microsoft:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:Microsoft:Scheme"] = "Microsoft",
            ["ReferenceApplication:Authentication:Providers:Microsoft:DisplayName"] = "Microsoft",
            ["ReferenceApplication:Authentication:Providers:Microsoft:ClientId"] = "test-microsoft-client-id",
            ["ReferenceApplication:Authentication:Providers:Microsoft:ClientSecret"] = "test-microsoft-client-secret",
            ["ReferenceApplication:Authentication:Providers:Microsoft:CallbackPath"] = "/signin-microsoft",

            ["ReferenceApplication:Authentication:Providers:Google:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:Google:Scheme"] = "Google",
            ["ReferenceApplication:Authentication:Providers:Google:DisplayName"] = "Google",
            ["ReferenceApplication:Authentication:Providers:Google:ClientId"] = "test-google-client-id",
            ["ReferenceApplication:Authentication:Providers:Google:ClientSecret"] = "test-google-client-secret",
            ["ReferenceApplication:Authentication:Providers:Google:CallbackPath"] = "/signin-google",

            ["ReferenceApplication:Authentication:Providers:GitHub:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:GitHub:Scheme"] = "GitHub",
            ["ReferenceApplication:Authentication:Providers:GitHub:DisplayName"] = "GitHub",
            ["ReferenceApplication:Authentication:Providers:GitHub:ClientId"] = "test-github-client-id",
            ["ReferenceApplication:Authentication:Providers:GitHub:ClientSecret"] = "test-github-client-secret",
            ["ReferenceApplication:Authentication:Providers:GitHub:CallbackPath"] = "/signin-github"
        });

        ApplicationAuthenticationOptions options = factory.Services
            .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
            .Value;

        Assert.True(options.Enabled);
        Assert.Equal("Cookies", options.DefaultScheme);
        Assert.Equal("Cookies", options.DefaultChallengeScheme);
        Assert.Equal("Cookies", options.DefaultSignInScheme);

        Assert.True(options.Cookie.Enabled);
        Assert.Equal("Cookies", options.DefaultChallengeScheme);
        Assert.Equal("Cookies", options.Cookie.Scheme);
        Assert.Equal("/Account/Login", options.Cookie.LoginPath);
        Assert.Equal("/Account/Logout", options.Cookie.LogoutPath);
        Assert.Equal("/Account/AccessDenied", options.Cookie.AccessDeniedPath);
        Assert.Equal(90, options.Cookie.ExpireMinutes);
        Assert.False(options.Cookie.SlidingExpiration);

        Assert.True(options.Providers.OpenIdConnect.Enabled);
        Assert.True(options.Providers.Saml2.Enabled);
        Assert.Equal("/custom-saml2-acs", options.Providers.Saml2.ModulePath);
        Assert.True(options.Providers.Microsoft.Enabled);
        Assert.True(options.Providers.Google.Enabled);
        Assert.True(options.Providers.GitHub.Enabled);
    }

    /// <summary>
    /// Verifies that the authentication options match the expected values based on the provided configuration.
    /// </summary>
    [Fact]
    public void AuthenticationOptions_MatchGeneratedConfiguration()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>());

        IConfiguration configuration = factory.Services.GetRequiredService<IConfiguration>();

        ApplicationAuthenticationOptions options = factory.Services
            .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
            .Value;

        bool expectedAuthenticationEnabled = bool.TryParse(
            configuration["ReferenceApplication:Authentication:Enabled"],
            out bool authenticationEnabled) && authenticationEnabled;

        bool expectedCookieEnabled = bool.TryParse(
            configuration["ReferenceApplication:Authentication:Cookie:Enabled"],
            out bool cookieEnabled) && cookieEnabled;

        Assert.Equal(expectedAuthenticationEnabled, options.Enabled);
        Assert.Equal(expectedCookieEnabled, options.Cookie.Enabled);
        Assert.Equal("Cookies", options.DefaultScheme);
        Assert.Equal("Cookies", options.Cookie.Scheme);
    }

    /// <summary>
    /// Verifies that anonymous endpoints remain accessible when authentication is disabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task DisabledAuthentication_AllowsAnonymousEndpoint()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Enabled"] = "false"
        });

        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/test/authentication/anonymous",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Verifies that cookie authentication is registered when application authentication is enabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task EnabledAuthentication_RegistersCookieScheme()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies"
        });

        IAuthenticationSchemeProvider schemeProvider = factory.Services
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync("Cookies");

        Assert.NotNull(scheme);
    }

    /// <summary>
    /// Verifies that an enabled external authentication provider registers the expected authentication scheme with the correct display name. This test uses parameterized data to validate multiple providers, ensuring that when a provider is enabled in the configuration, it correctly adds its authentication scheme to the application's authentication system. The test checks both the presence of the scheme and that its properties match the expected values defined in the configuration.
    /// </summary>
    /// <param name="providerName">
    /// Name of the external authentication provider being tested (e.g., "OpenIdConnect", "Saml2", "Google", "GitHub"). This parameter is used to generate the appropriate configuration for the provider and to verify that the corresponding authentication scheme is registered correctly in the application's authentication system.
    /// </param>
    /// <param name="schemeName">
    /// Name of the authentication scheme that should be registered for the enabled provider. This value is derived from the provider's configuration and is used to retrieve the scheme from the authentication scheme provider during the test. The test asserts that a scheme with this name exists and has the expected properties when the provider is enabled.
    /// </param>
    /// <param name="displayName">
    /// Name of the display name that should be associated with the registered authentication scheme for the enabled provider. This value is defined in the provider's configuration and is used to verify that the scheme's display name matches the expected value when the provider is enabled. The test asserts that the display name of the registered scheme is correct, ensuring that the configuration is properly applied to the authentication system.
    /// </param>
    /// <returns></returns>
    [Theory]
    [MemberData(nameof(EnabledExternalProviderConfigurations))]
    public async Task EnabledExternalAuthenticationProvider_RegistersConfiguredScheme(
        string providerName,
        string schemeName,
        string displayName)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateEnabledAuthenticationConfiguration(providerName))
            .Build();

        ServiceCollection services = new();

        services.AddLogging();
        services.AddApplicationAuthentication(configuration);

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        IAuthenticationSchemeProvider schemeProvider = serviceProvider
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync(schemeName);

        Assert.NotNull(scheme);
        Assert.Equal(schemeName, scheme.Name);
        Assert.Equal(displayName, scheme.DisplayName);
    }

    /// <summary>
    /// Verifies that a protected API endpoint returns unauthorized for unauthenticated users when cookie authentication is enabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task ProtectedApiEndpoint_ReturnsUnauthorized_WhenCookieAuthenticationIsEnabled()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Cookie:LoginPath"] = "/Account/Login"
        });

        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage response = await client.GetAsync(
            "/test/authentication/protected",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Found, response.StatusCode);

        Assert.NotNull(response.Headers.Location);
        Assert.Equal("/Account/Login", response.Headers.Location.LocalPath);
        Assert.Contains("ReturnUrl=", response.Headers.Location.Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Verifies that the default cookie authentication pipeline establishes a principal that can reach a protected endpoint.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task ProtectedApiEndpoint_ReturnsAuthenticatedIdentity_AfterCookieSignIn()
    {
        const string expectedUserName = "cookie-positive-path-user";

        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Cookie:LoginPath"] = "/Account/Login"
        });

        using HttpClient client = factory.CreateHttpsClient();

        using HttpResponseMessage unauthenticatedResponse = await client.GetAsync(
            "/test/authentication/protected",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);

        using HttpResponseMessage antiforgeryResponse = await client.GetAsync(
            "/test/authentication/antiforgery-token",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, antiforgeryResponse.StatusCode);

        string antiforgeryToken = await antiforgeryResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(antiforgeryToken));

        using FormUrlEncodedContent signInContent = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgeryToken,
            ["userName"] = expectedUserName
        });

        using HttpResponseMessage signInResponse = await client.PostAsync(
            "/test/authentication/cookie-sign-in",
            signInContent,
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, signInResponse.StatusCode);

        using HttpResponseMessage authenticatedResponse = await client.GetAsync(
            "/test/authentication/protected",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, authenticatedResponse.StatusCode);

        string responseBody = await authenticatedResponse.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);

        Assert.Contains(
            $"\"userName\":\"{expectedUserName}\"",
            responseBody,
            StringComparison.Ordinal);
        Assert.Contains(
            $"\"subject\":\"{expectedUserName}\"",
            responseBody,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that startup validation fails when authentication is enabled and the cookie scheme is empty.
    /// </summary>
    [Fact]
    public void Authentication_EmptyCookieScheme_FailsStartup()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = ""
        });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
            factory.Services
                .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
                .Value);

        Assert.Contains(
            "ReferenceApplication:Authentication:Cookie:Scheme is required when application authentication is enabled",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that startup validation fails when authentication is enabled and cookie expiration is zero.
    /// </summary>
    [Fact]
    public void Authentication_ZeroCookieExpiration_FailsStartup()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:ExpireMinutes"] = "0"
        });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
            factory.Services
                .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
                .Value);

        Assert.Contains(
            "ReferenceApplication:Authentication:Cookie:ExpireMinutes must be greater than zero when application authentication is enabled",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the OpenIdConnect authentication provider does not require configuration when it is disabled.
    /// </summary>
    /// <remarks>This test ensures that when the OpenIdConnect provider is explicitly disabled in the
    /// configuration, missing or empty configuration values for authority and client ID do not cause errors. Use this
    /// test to validate that the application can start without OpenIdConnect settings when the provider is not
    /// enabled.</remarks>
    [Fact]
    public void DisabledOpenIdConnectProvider_DoesNotRequireConfiguration()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Enabled"] = "false",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Authority"] = "",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ClientId"] = ""
        });

        ApplicationAuthenticationOptions options = factory.Services
            .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
            .Value;

        Assert.False(options.Providers.OpenIdConnect.Enabled);
    }

    /// <summary>
    /// Verifies that the OpenIdConnect provider does not register a scheme when disabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task DisabledOpenIdConnectProvider_DoesNotRegisterScheme()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Enabled"] = "false",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scheme"] = "OpenIdConnect",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:DisplayName"] = "OpenId Connect",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Authority"] = "",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ClientId"] = "test-client-id",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:CallbackPath"] = "/signin-oidc",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ResponseType"] = "code",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scopes:0"] = "openid"
        });

        IAuthenticationSchemeProvider schemeProvider = factory.Services
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync("OpenIdConnect");

        Assert.Null(scheme);
    }

    /// <summary>
    /// Verifies that enabling the OpenIdConnect authentication provider without specifying an authority causes
    /// application startup to fail with an options validation exception.
    /// </summary>
    /// <remarks>This test ensures that the configuration is validated and that the authority setting is
    /// required when the OpenIdConnect provider is enabled. It helps prevent misconfiguration at startup by asserting
    /// that a missing authority results in a clear validation error.</remarks>
    [Fact]
    public void EnabledOpenIdConnectProvider_MissingAuthority_FailsStartup()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Authority"] = "",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ClientId"] = "test-client-id",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:CallbackPath"] = "/signin-oidc",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ResponseType"] = "code",
            ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scopes:0"] = "openid"
        });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
            factory.Services
                .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
                .Value);

        Assert.Contains(
            "ReferenceApplication:Authentication:Providers:OpenIdConnect:Authority is required when the provider is enabled",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the SAML2 provider does not register a scheme when disabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task DisabledSaml2Provider_DoesNotRegisterScheme()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Providers:Saml2:Enabled"] = "false",
            ["ReferenceApplication:Authentication:Providers:Saml2:Scheme"] = "Saml2",
            ["ReferenceApplication:Authentication:Providers:Saml2:DisplayName"] = "SAML2",
            ["ReferenceApplication:Authentication:Providers:Saml2:EntityId"] = "https://ReferenceApplication.example/saml2",
            ["ReferenceApplication:Authentication:Providers:Saml2:MetadataUrl"] = "",
            ["ReferenceApplication:Authentication:Providers:Saml2:ModulePath"] = "/Saml2/Acs"
        });

        IAuthenticationSchemeProvider schemeProvider = factory.Services
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync("Saml2");

        Assert.Null(scheme);
    }

    /// <summary>
    /// Verifies that application startup fails with an OptionsValidationException when the SAML2 provider is enabled
    /// but the MetadataUrl configuration is missing or empty.
    /// </summary>
    /// <remarks>This test ensures that the SAML2 authentication provider enforces the requirement for a
    /// non-empty MetadataUrl when enabled. It validates that misconfiguration is detected early during application
    /// startup, preventing the application from running with incomplete authentication settings.</remarks>
    [Fact]
    public void EnabledSaml2Provider_MissingMetadataUrl_FailsStartup()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Providers:Saml2:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:Saml2:EntityId"] = "https://ReferenceApplication.example/saml2",
            ["ReferenceApplication:Authentication:Providers:Saml2:MetadataUrl"] = "",
            ["ReferenceApplication:Authentication:Providers:Saml2:ModulePath"] = "/Saml2/Acs"
        });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
            factory.Services
                .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
                .Value);

        Assert.Contains(
            "ReferenceApplication:Authentication:Providers:Saml2:MetadataUrl is required when the provider is enabled",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the Microsoft authentication provider does not register a scheme when disabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task DisabledMicrosoftProvider_DoesNotRegisterScheme()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Providers:Microsoft:Enabled"] = "false",
            ["ReferenceApplication:Authentication:Providers:Microsoft:Scheme"] = "Microsoft",
            ["ReferenceApplication:Authentication:Providers:Microsoft:DisplayName"] = "Microsoft",
            ["ReferenceApplication:Authentication:Providers:Microsoft:ClientId"] = "",
            ["ReferenceApplication:Authentication:Providers:Microsoft:ClientSecret"] = "",
            ["ReferenceApplication:Authentication:Providers:Microsoft:CallbackPath"] = "/signin-microsoft"
        });

        IAuthenticationSchemeProvider schemeProvider = factory.Services
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync("Microsoft");

        Assert.Null(scheme);
    }

    /// <summary>
    /// Verifies that application startup fails with an options validation exception when the Microsoft authentication
    /// provider is enabled but the client secret is missing.
    /// </summary>
    /// <remarks>This test ensures that the configuration for the Microsoft authentication provider enforces
    /// the requirement for a client secret when the provider is enabled. It validates that an appropriate exception is
    /// thrown and that the error message clearly indicates the missing configuration.</remarks>
    [Fact]
    public void EnabledMicrosoftProvider_MissingClientSecret_FailsStartup()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Providers:Microsoft:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:Microsoft:Scheme"] = "Microsoft",
            ["ReferenceApplication:Authentication:Providers:Microsoft:DisplayName"] = "Microsoft",
            ["ReferenceApplication:Authentication:Providers:Microsoft:ClientId"] = "test-client-id",
            ["ReferenceApplication:Authentication:Providers:Microsoft:ClientSecret"] = "",
            ["ReferenceApplication:Authentication:Providers:Microsoft:CallbackPath"] = "/signin-microsoft"
        });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
            factory.Services
                .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
                .Value);

        Assert.Contains(
            "ReferenceApplication:Authentication:Providers:Microsoft:ClientSecret is required when the provider is enabled",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the Google authentication provider does not register a scheme when disabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task DisabledGoogleProvider_DoesNotRegisterGoogleScheme()
    {
        ServiceCollection services = new();

        AuthenticationBuilder builder = services.AddAuthentication();

        builder.AddGoogleAuthentication(new ApplicationExternalAuthenticationProviderOptions
        {
            Enabled = false,
            Scheme = "Google",
            DisplayName = "Google",
            ClientId = "",
            ClientSecret = "",
            CallbackPath = "/signin-google"
        });

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        IAuthenticationSchemeProvider schemeProvider = serviceProvider
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync("Google");

        Assert.Null(scheme);
    }

    /// <summary>
    /// Verifies that the Google authentication provider registers a scheme when enabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task EnabledGoogleProvider_RegistersGoogleScheme()
    {
        ServiceCollection services = new();

        AuthenticationBuilder builder = services.AddAuthentication();

        builder.AddGoogleAuthentication(new ApplicationExternalAuthenticationProviderOptions
        {
            Enabled = true,
            Scheme = "Google",
            DisplayName = "Google",
            ClientId = "test-google-client-id",
            ClientSecret = "test-google-client-secret",
            CallbackPath = "/signin-google",
            Scopes =
            [
                "profile",
            "email"
            ]
        });

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        IAuthenticationSchemeProvider schemeProvider = serviceProvider
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync("Google");

        Assert.NotNull(scheme);
        Assert.Equal("Google", scheme.Name);
        Assert.Equal("Google", scheme.DisplayName);
    }

    /// <summary>
    /// Verifies that application startup fails with an options validation exception when the Google authentication
    /// provider is enabled but the client secret is missing.
    /// </summary>
    [Fact]
    public void EnabledGoogleProvider_MissingClientSecret_FailsStartup()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Providers:Google:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:Google:Scheme"] = "Google",
            ["ReferenceApplication:Authentication:Providers:Google:DisplayName"] = "Google",
            ["ReferenceApplication:Authentication:Providers:Google:ClientId"] = "test-client-id",
            ["ReferenceApplication:Authentication:Providers:Google:ClientSecret"] = "",
            ["ReferenceApplication:Authentication:Providers:Google:CallbackPath"] = "/signin-google"
        });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
            factory.Services
                .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
                .Value);

        Assert.Contains(
            "ReferenceApplication:Authentication:Providers:Google:ClientSecret is required when the provider is enabled",
            exception.Message,
            StringComparison.Ordinal);
    }



    /// <summary>
    /// Verifies that the GitHub authentication provider does not register a scheme when disabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task DisabledGitHubProvider_DoesNotRegisterGitHubScheme()
    {
        ServiceCollection services = new();

        AuthenticationBuilder builder = services.AddAuthentication();

        builder.AddGitHubAuthentication(new ApplicationExternalAuthenticationProviderOptions
        {
            Enabled = false,
            Scheme = "GitHub",
            DisplayName = "GitHub",
            ClientId = "",
            ClientSecret = "",
            CallbackPath = "/signin-github"
        });

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        IAuthenticationSchemeProvider schemeProvider = serviceProvider
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync("GitHub");

        Assert.Null(scheme);
    }

    /// <summary>
    /// Verifies that the GitHub authentication provider registers a scheme when enabled.
    /// </summary>
    /// <returns>A task that represents the asynchronous test operation.</returns>
    [Fact]
    public async Task EnabledGitHubProvider_RegistersGitHubScheme()
    {
        ServiceCollection services = new();

        AuthenticationBuilder builder = services.AddAuthentication();

        builder.AddGitHubAuthentication(new ApplicationExternalAuthenticationProviderOptions
        {
            Enabled = true,
            Scheme = "GitHub",
            DisplayName = "GitHub",
            ClientId = "test-github-client-id",
            ClientSecret = "test-github-client-secret",
            CallbackPath = "/signin-github",
            Scopes =
            [
                "profile",
                "email"
            ]
        });

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        IAuthenticationSchemeProvider schemeProvider = serviceProvider
            .GetRequiredService<IAuthenticationSchemeProvider>();

        AuthenticationScheme? scheme = await schemeProvider.GetSchemeAsync("GitHub");

        Assert.NotNull(scheme);
        Assert.Equal("GitHub", scheme.Name);
        Assert.Equal("GitHub", scheme.DisplayName);
    }

    /// <summary>
    /// Verifies that application startup fails with an options validation exception when the GitHub authentication
    /// provider is enabled but the client secret is missing.
    /// </summary>
    [Fact]
    public void EnabledGitHubProvider_MissingClientSecret_FailsStartup()
    {
        using ApplicationWebApplicationFactory factory = CreateFactory(new Dictionary<string, string?>
        {
            ["ReferenceApplication:Authentication:Providers:GitHub:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Providers:GitHub:Scheme"] = "GitHub",
            ["ReferenceApplication:Authentication:Providers:GitHub:DisplayName"] = "GitHub",
            ["ReferenceApplication:Authentication:Providers:GitHub:ClientId"] = "test-client-id",
            ["ReferenceApplication:Authentication:Providers:GitHub:ClientSecret"] = "",
            ["ReferenceApplication:Authentication:Providers:GitHub:CallbackPath"] = "/signin-github"
        });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(() =>
            factory.Services
                .GetRequiredService<IOptions<ApplicationAuthenticationOptions>>()
                .Value);

        Assert.Contains(
            "ReferenceApplication:Authentication:Providers:GitHub:ClientSecret is required when the provider is enabled",
            exception.Message,
            StringComparison.Ordinal);
    }


    /// <summary>
    /// Creates a test application factory with the supplied in-memory configuration overrides.
    /// </summary>
    /// <param name="configurationValues">The configuration key/value pairs used to override application settings for a test.</param>
    /// <returns>A configured <see cref="ApplicationWebApplicationFactory"/> instance.</returns>
    private static ApplicationWebApplicationFactory CreateFactory(IReadOnlyDictionary<string, string?> configurationValues)
    {
        return ApplicationWebApplicationFactory.CreateAllowingAnonymousAccess(configurationValues);
    }

    private static Dictionary<string, string?> CreateEnabledAuthenticationConfiguration(string providerName)
    {
        Dictionary<string, string?> configuration = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies",
            ["ReferenceApplication:Authentication:Cookie:LoginPath"] = "/Account/Login",
            ["ReferenceApplication:Authentication:Cookie:LogoutPath"] = "/Account/Logout",
            ["ReferenceApplication:Authentication:Cookie:AccessDeniedPath"] = "/Account/AccessDenied",
            ["ReferenceApplication:Authentication:Cookie:ExpireMinutes"] = "60"
        };

        foreach (KeyValuePair<string, string?> item in CreateProviderConfiguration(providerName))
        {
            configuration[item.Key] = item.Value;
        }

        return configuration;
    }

    private static Dictionary<string, string?> CreateProviderConfiguration(string providerName)
    {
        return providerName switch
        {
            "OpenIdConnect" => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Enabled"] = "true",
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scheme"] = "OpenIdConnect",
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:DisplayName"] = "OpenID Connect",
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Authority"] = "https://login.example.test",
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ClientId"] = "test-oidc-client-id",
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ClientSecret"] = "test-oidc-client-secret",
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:CallbackPath"] = "/signin-oidc",
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:ResponseType"] = "code",
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scopes:0"] = "openid",
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scopes:1"] = "profile",
                ["ReferenceApplication:Authentication:Providers:OpenIdConnect:Scopes:2"] = "email"
            },

            "Saml2" => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ReferenceApplication:Authentication:Providers:Saml2:Enabled"] = "true",
                ["ReferenceApplication:Authentication:Providers:Saml2:Scheme"] = "Saml2",
                ["ReferenceApplication:Authentication:Providers:Saml2:DisplayName"] = "SAML2",
                ["ReferenceApplication:Authentication:Providers:Saml2:EntityId"] = "https://ReferenceApplication.example.test/saml2",
                ["ReferenceApplication:Authentication:Providers:Saml2:MetadataUrl"] = "https://idp.example.test/metadata",
                ["ReferenceApplication:Authentication:Providers:Saml2:ModulePath"] = "/Saml2/Acs",
                ["ReferenceApplication:Authentication:Providers:Saml2:LoadMetadata"] = "false"
            },

            "Google" => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ReferenceApplication:Authentication:Providers:Google:Enabled"] = "true",
                ["ReferenceApplication:Authentication:Providers:Google:Scheme"] = "Google",
                ["ReferenceApplication:Authentication:Providers:Google:DisplayName"] = "Google",
                ["ReferenceApplication:Authentication:Providers:Google:ClientId"] = "test-google-client-id",
                ["ReferenceApplication:Authentication:Providers:Google:ClientSecret"] = "test-google-client-secret",
                ["ReferenceApplication:Authentication:Providers:Google:CallbackPath"] = "/signin-google",
                ["ReferenceApplication:Authentication:Providers:Google:Scopes:0"] = "profile",
                ["ReferenceApplication:Authentication:Providers:Google:Scopes:1"] = "email"
            },

            "GitHub" => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["ReferenceApplication:Authentication:Providers:GitHub:Enabled"] = "true",
                ["ReferenceApplication:Authentication:Providers:GitHub:Scheme"] = "GitHub",
                ["ReferenceApplication:Authentication:Providers:GitHub:DisplayName"] = "GitHub",
                ["ReferenceApplication:Authentication:Providers:GitHub:ClientId"] = "test-github-client-id",
                ["ReferenceApplication:Authentication:Providers:GitHub:ClientSecret"] = "test-github-client-secret",
                ["ReferenceApplication:Authentication:Providers:GitHub:CallbackPath"] = "/signin-github",
                ["ReferenceApplication:Authentication:Providers:GitHub:Scopes:0"] = "user:email"
            },

            _ => throw new ArgumentOutOfRangeException(
                nameof(providerName),
                providerName,
                "Unsupported authentication provider.")
        };
    }
}
