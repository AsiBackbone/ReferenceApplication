using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Governance.Simulation;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Registers and maps the explicitly enabled, Development-only governance policy simulator.
/// </summary>
public static class PolicySimulatorServiceExtensions
{
    /// <summary>
    /// Gets the route that evaluates a simulation.
    /// </summary>
    public const string EvaluatePath = "/api/v1/policy-simulator/evaluate";

    /// <summary>
    /// Registers the simulator only when its switch is enabled in Development.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    /// <returns>The same service collection so calls can be chained.</returns>
    /// <remarks>
    /// Call after <c>AddApplicationAsiBackbone</c> and <c>AddApplicationCapabilityTokens</c>: the simulator composes
    /// the constraints, decision policy, and capability validator they register.
    /// </remarks>
    public static IServiceCollection AddApplicationPolicySimulator(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        if (!IsActive(configuration, environment))
        {
            return services;
        }

        services.AddHttpContextAccessor();
        services.TryAddScoped<ClaimEndpointCapabilityGrantValidator>();
        services.AddScoped<IPolicySimulator, PolicySimulator>();
        return services;
    }

    /// <summary>
    /// Maps the administrator-only evaluation route only for an active Development simulator.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application so calls can be chained.</returns>
    public static WebApplication MapApplicationPolicySimulator(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!IsActive(app.Configuration, app.Environment) ||
            app.Services.GetService<IServiceProviderIsService>()?.IsService(typeof(IPolicySimulator)) != true)
        {
            return app;
        }

        app.MapPost(EvaluatePath, EvaluateAsync)
            .RequireAuthorization(ApplicationAuthorizationPolicyNames.AdministratorRole)
            .WithDisplayName("Evaluate governance policy simulation");
        return app;
    }

    /// <summary>
    /// Determines whether the feature switch is enabled in the Development environment.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    /// <returns><see langword="true" /> when the simulator is active.</returns>
    public static bool IsActive(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment() && configuration.GetValue<bool>(
            $"{PolicySimulatorOptions.SectionName}:{nameof(PolicySimulatorOptions.Enabled)}");
    }

    private static async Task<IResult> EvaluateAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IPolicySimulator simulator,
        CancellationToken cancellationToken)
    {
        if (!await IsAntiforgeryValidAsync(httpContext, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        if (!httpContext.Request.HasFormContentType)
        {
            return Results.BadRequest(new { message = "Post the simulation inputs as a form." });
        }

        IFormCollection form = await httpContext.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        var request = new PolicySimulationRequest(
            Operation: form["operation"].ToString(),
            Administrator: IsSet(form["administrator"]),
            ConsequentialCapability: IsSet(form["consequentialCapability"]),
            Acknowledged: IsSet(form["acknowledged"]),
            Payload: form["payload"].ToString(),
            DisabledConstraints: [.. form["disabledConstraints"]
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!.Trim())]);

        PolicySimulationOutcome outcome = await simulator
            .SimulateAsync(httpContext, request, cancellationToken)
            .ConfigureAwait(false);

        return outcome.Result is null
            ? Results.BadRequest(new { message = outcome.Error })
            : Results.Ok(outcome.Result);
    }

    private static bool IsSet(StringValues values)
    {
        return values.Any(static value => string.Equals(value, bool.TrueString, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> IsAntiforgeryValidAsync(HttpContext context, IAntiforgery antiforgery)
    {
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
