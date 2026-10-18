using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using ReferenceApplication.Web.Authentication.Demo;

namespace ReferenceApplication.Web.Authentication.Extensions;

/// <summary>
/// Registers and maps the explicitly enabled, Development-only demo identity control.
/// </summary>
public static class DemoIdentityServiceExtensions
{
    private const string _administratorFormField = "administrator";
    private const string _capabilityFormField = "consequentialCapability";
    private const string _returnUrlFormField = "returnUrl";

    /// <summary>
    /// Adds the demo identity service only when the host is running in Development and the explicit switch is enabled.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddApplicationDemoIdentity(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (IsDemoIdentityActive(configuration, environment))
        {
            services.AddSingleton<IDemoIdentityService, DemoIdentityService>();
        }

        return services;
    }

    /// <summary>
    /// Maps the demo sign-in endpoint when the demo identity service was registered.
    /// </summary>
    /// <param name="app">The application whose endpoint should be mapped.</param>
    /// <returns>The same application for chaining.</returns>
    public static WebApplication MapApplicationDemoIdentity(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!IsDemoIdentityActive(app.Configuration, app.Environment) ||
            app.Services.GetService<IDemoIdentityService>() is null)
        {
            return app;
        }

        app.MapPost(DemoIdentityOptions.SignInPath, SignInAsync)
            .AllowAnonymous()
            .WithDisplayName("Create Development demo identity");

        return app;
    }

    /// <summary>
    /// Determines whether the demo identity control is active for the supplied host configuration.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    /// <returns><see langword="true" /> only for Development with the explicit switch enabled.</returns>
    public static bool IsDemoIdentityActive(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment() && configuration.GetValue<bool>(
            $"{DemoIdentityOptions.SectionName}:{nameof(DemoIdentityOptions.Enabled)}");
    }

    private static async Task<IResult> SignInAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IDemoIdentityService demoIdentityService,
        CancellationToken cancellationToken)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(httpContext).ConfigureAwait(false);
        }
        catch (AntiforgeryValidationException)
        {
            return Results.BadRequest();
        }

        IFormCollection form = await httpContext.Request
            .ReadFormAsync(cancellationToken)
            .ConfigureAwait(false);
        string returnUrl = form[_returnUrlFormField].ToString();
        returnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;

        if (!IsLocalPath(returnUrl))
        {
            return Results.BadRequest();
        }

        bool includeAdministratorRole = IsSelected(form, _administratorFormField);
        bool includeConsequentialCapability = IsSelected(form, _capabilityFormField);
        await httpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            demoIdentityService.CreatePrincipal(
                includeAdministratorRole,
                includeConsequentialCapability),
            new AuthenticationProperties
            {
                AllowRefresh = false,
                IsPersistent = false
            }).ConfigureAwait(false);

        return Results.LocalRedirect(returnUrl);
    }

    private static bool IsSelected(IFormCollection form, string fieldName)
    {
        return string.Equals(form[fieldName].ToString(), bool.TrueString, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLocalPath(string path)
    {
        return path.Length > 0 && path[0] == '/' &&
            (path.Length == 1 || path[1] is not ('/' or '\\'));
    }
}
