using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.Controllers;
using ReferenceApplication.Web.Controllers.Api;

namespace ReferenceApplication.Web.Governance.Sandbox;

/// <summary>
/// Validates antiforgery for the Development-only sandbox minimal APIs.
/// </summary>
internal static class SandboxAntiforgeryValidation
{
    /// <summary>
    /// Validates the request and reports validation failures without allowing an exception to escape the endpoint.
    /// </summary>
    public static async Task<bool> IsValidAsync(HttpContext context, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(antiforgery);

        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }
}

/// <summary>
/// Resolves the real endpoints exposed by <see cref="GovernedOperationsController" /> for sandbox evaluation.
/// </summary>
internal static class GovernedEndpointResolver
{
    /// <summary>
    /// Finds the endpoint for one governed controller action.
    /// </summary>
    public static Endpoint? Find(EndpointDataSource endpointDataSource, string actionName)
    {
        ArgumentNullException.ThrowIfNull(endpointDataSource);

        return endpointDataSource.Endpoints.FirstOrDefault(endpoint =>
            endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action &&
            action.ControllerTypeInfo.AsType() == typeof(GovernedOperationsController) &&
            string.Equals(action.ActionName, actionName, StringComparison.Ordinal));
    }
}

/// <summary>
/// Applies the common Development-only activation rules used by the governance sandbox features.
/// </summary>
internal static class SandboxFeatureActivation
{
    /// <summary>
    /// Determines whether a feature switch is enabled in Development.
    /// </summary>
    public static bool IsEnabled(
        IConfiguration configuration,
        IHostEnvironment environment,
        string sectionName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        return environment.IsDevelopment() &&
            configuration.GetValue<bool>($"{sectionName}:Enabled");
    }

    /// <summary>
    /// Determines whether a feature is active and its service is registered for endpoint or middleware mapping.
    /// </summary>
    public static bool IsRegistered<TService>(
        WebApplication app,
        Func<IConfiguration, IHostEnvironment, bool> isActive)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(isActive);

        return isActive(app.Configuration, app.Environment) &&
            app.Services.GetService<IServiceProviderIsService>()?.IsService(typeof(TService)) == true;
    }
}
