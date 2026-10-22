using AsiBackbone.Core.Constraints;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Governance.FailureScenarios;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Registers and maps the explicitly enabled, Development-only failure scenarios and their fault-injection seams.
/// </summary>
public static class FailureScenarioServiceExtensions
{
    /// <summary>
    /// Gets the route prefix of the failure-scenario API.
    /// </summary>
    public const string RoutePrefix = "/api/v1/failure-scenarios";

    private const string _correlationIdFormField = "correlationId";

    /// <summary>
    /// Registers the scenarios and their seams only when their switch is enabled in Development and the disposable
    /// demonstration database is in use.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    /// <returns>The same service collection so calls can be chained.</returns>
    /// <remarks>
    /// <para>
    /// Call after <c>AddApplicationDataAccess</c>, <c>AddApplicationAsiBackbone</c>, <c>AddApplicationGovernanceEvidence</c>,
    /// and <c>AddApplicationCapabilityTokens</c>. Outside an active Development host nothing is registered: no demonstration
    /// constraint, no save interceptor, no middleware, and no route.
    /// </para>
    /// <para>
    /// The seams act only on a request that consumed an armed run, or, for delivery, on the audit completion of that
    /// request, so they leave every other request unchanged.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddApplicationFailureScenarios(
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

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<FailureScenarioRegistry>();
        services.AddSingleton<FailureScenarioSaveChangesInterceptor>();
        services.ConfigureDbContext<ApplicationDbContext>(static (serviceProvider, options) =>
            options.AddInterceptors(serviceProvider.GetRequiredService<FailureScenarioSaveChangesInterceptor>()));
        services.AddScoped<IGovernanceConstraint<GovernanceEvaluationContext>, FailureScenarioThrowingConstraint>();
        services.AddScoped<IFailureScenarioService, FailureScenarioService>();
        return services;
    }

    /// <summary>
    /// Adds the middleware that consumes armed runs, only when the scenarios are active. Call between authorization and
    /// endpoint governance, so the run's actor is known and governance sees the run.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application so calls can be chained.</returns>
    public static WebApplication UseApplicationFailureScenarios(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (IsRegistered(app))
        {
            _ = app.UseMiddleware<FailureScenarioMiddleware>();
        }

        return app;
    }

    /// <summary>
    /// Maps the administrator-only failure-scenario routes only when the scenarios are active.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application so calls can be chained.</returns>
    public static WebApplication MapApplicationFailureScenarios(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!IsRegistered(app))
        {
            return app;
        }

        RouteGroupBuilder routes = app
            .MapGroup(RoutePrefix)
            .RequireAuthorization(ApplicationAuthorizationPolicyNames.AdministratorRole);
        _ = routes.MapGet("/", ListScenarios).WithDisplayName("List failure scenarios");
        _ = routes.MapPost("/{scenarioId}", StartScenarioAsync).WithDisplayName("Arm or run a failure scenario");
        _ = routes.MapGet("/runs/{runId}", GetRunAsync).WithDisplayName("Read a failure-scenario run");
        _ = routes.MapPost("/runs/{runId}/clear-delivery-fault", ClearDeliveryFaultAsync)
            .WithDisplayName("Clear a failure-scenario delivery fault");
        return app;
    }

    /// <summary>
    /// Determines whether the scenarios are enabled in Development with the demonstration database in use.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    /// <returns><see langword="true" /> when the scenarios are active.</returns>
    public static bool IsActive(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        // The scenarios leave failure evidence behind on purpose, so they run only against the disposable database the
        // Development demo identity selects, which the explorer can reset.
        return environment.IsDevelopment() &&
            DemoIdentityServiceExtensions.IsDemoIdentityActive(configuration, environment) &&
            configuration.GetValue<bool>($"{FailureScenarioOptions.SectionName}:{nameof(FailureScenarioOptions.Enabled)}");
    }

    private static bool IsRegistered(WebApplication app)
    {
        return IsActive(app.Configuration, app.Environment) &&
            app.Services.GetService<IServiceProviderIsService>()?.IsService(typeof(IFailureScenarioService)) == true;
    }

    private static IResult ListScenarios(HttpContext httpContext, IFailureScenarioService scenarios)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(scenarios.GetScenarios(httpContext.User));
    }

    private static async Task<IResult> StartScenarioAsync(
        string scenarioId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IFailureScenarioService scenarios,
        CancellationToken cancellationToken)
    {
        if (!await IsAntiforgeryValidAsync(httpContext, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        IFormCollection form = await httpContext.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        string? correlationId = form[_correlationIdFormField].FirstOrDefault()?.Trim();
        if (correlationId is { Length: > 128 })
        {
            return Results.BadRequest(new { message = "The correlation identifier is too long." });
        }

        httpContext.Response.Headers.CacheControl = "no-store";
        FailureScenarioStartResult result = await scenarios
            .StartAsync(scenarioId, string.IsNullOrEmpty(correlationId) ? null : correlationId, httpContext, cancellationToken)
            .ConfigureAwait(false);

        return result.ErrorStatusCode is { } statusCode
            ? Results.Json(new { message = result.ErrorMessage }, statusCode: statusCode)
            : result.Tampering is { } tampering
            ? Results.Ok(new { scenarioId, tampering })
            : Results.Ok(new { scenarioId, run = result.Run });
    }

    private static async Task<IResult> GetRunAsync(
        string runId,
        HttpContext httpContext,
        IFailureScenarioService scenarios,
        CancellationToken cancellationToken)
    {
        if (runId.Length > FailureScenarioOptions.MaximumRunIdLength)
        {
            return Results.BadRequest(new { message = "The run identifier is too long." });
        }

        // A run report is polled while delivery retries, so it must never be served from a cache.
        httpContext.Response.Headers.CacheControl = "no-store";
        FailureScenarioRunReport? report = await scenarios.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        return report is null
            ? Results.NotFound(new { message = "The run is unknown or was forgotten." })
            : Results.Ok(report);
    }

    private static async Task<IResult> ClearDeliveryFaultAsync(
        string runId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IFailureScenarioService scenarios)
    {
        return !await IsAntiforgeryValidAsync(httpContext, antiforgery).ConfigureAwait(false)
            ? Results.BadRequest()
            : scenarios.ClearDeliveryFault(runId)
            ? Results.NoContent()
            : Results.Conflict(new { message = "The run holds no active delivery fault." });
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
