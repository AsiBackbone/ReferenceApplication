using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Governance.Replay;
using ReferenceApplication.Web.Governance.Simulation;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Registers and maps the explicitly enabled, Development-only decision replay.
/// </summary>
public static class DecisionReplayServiceExtensions
{
    /// <summary>
    /// Gets the route that lists persisted decisions and, with a receipt identifier, finds one.
    /// </summary>
    public const string DecisionsPath = "/api/v1/decision-replay/decisions";

    /// <summary>
    /// Registers decision replay only when its switch is enabled in Development.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    /// <returns>The same service collection so calls can be chained.</returns>
    /// <remarks>
    /// Call after <c>AddApplicationAsiBackbone</c>, <c>AddApplicationGovernanceEvidence</c>, and
    /// <c>AddApplicationCapabilityTokens</c>: replay reads the ledger and composes the constraints, decision policy,
    /// and capability validator they register.
    /// </remarks>
    public static IServiceCollection AddApplicationDecisionReplay(
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
        services.TryAddScoped<GovernancePipelineHarness>();
        services.AddScoped<IDecisionReplayService, DecisionReplayService>();
        return services;
    }

    /// <summary>
    /// Maps the administrator-only replay routes only when decision replay is active in Development.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <returns>The same application so calls can be chained.</returns>
    public static WebApplication MapApplicationDecisionReplay(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!IsActive(app.Configuration, app.Environment) ||
            app.Services.GetService<IServiceProviderIsService>()?.IsService(typeof(IDecisionReplayService)) != true)
        {
            return app;
        }

        RouteGroupBuilder decisions = app.MapGroup(DecisionsPath)
            .RequireAuthorization(ApplicationAuthorizationPolicyNames.AdministratorRole);
        _ = decisions.MapGet(string.Empty, ListAsync).WithDisplayName("List decisions for replay");
        _ = decisions.MapGet("/{decisionReceiptId}", FindAsync).WithDisplayName("Find a decision for replay");
        _ = decisions.MapPost("/{decisionReceiptId}/replay", ReplayAsync).WithDisplayName("Replay a persisted decision");
        return app;
    }

    /// <summary>
    /// Determines whether the feature switch is enabled in the Development environment.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="environment">The host environment.</param>
    /// <returns><see langword="true" /> when decision replay is active.</returns>
    public static bool IsActive(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment() && configuration.GetValue<bool>(
            $"{DecisionReplayOptions.SectionName}:{nameof(DecisionReplayOptions.Enabled)}");
    }

    private static async Task<IResult> ListAsync(
        string? before,
        IDecisionReplayService replay,
        CancellationToken cancellationToken)
    {
        if (before is { Length: > DecisionReplayOptions.MaximumReceiptIdLength })
        {
            return Results.BadRequest(new { message = "The cursor is too long." });
        }

        DecisionReplayPage? page = await replay.ListAsync(before, cancellationToken).ConfigureAwait(false);
        return page is null
            ? Results.BadRequest(new { message = "The cursor is not a persisted decision." })
            : Results.Ok(page);
    }

    private static async Task<IResult> FindAsync(
        string decisionReceiptId,
        IDecisionReplayService replay,
        CancellationToken cancellationToken)
    {
        if (!IsValidReceiptId(decisionReceiptId))
        {
            return Results.NotFound();
        }

        DecisionReplayDetails? details = await replay
            .FindAsync(decisionReceiptId, cancellationToken)
            .ConfigureAwait(false);
        return details is null ? Results.NotFound() : Results.Ok(details);
    }

    private static async Task<IResult> ReplayAsync(
        string decisionReceiptId,
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IDecisionReplayService replay,
        CancellationToken cancellationToken)
    {
        if (!await IsAntiforgeryValidAsync(httpContext, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        if (!IsValidReceiptId(decisionReceiptId))
        {
            return Results.NotFound();
        }

        IFormCollection form = httpContext.Request.HasFormContentType
            ? await httpContext.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false)
            : FormCollection.Empty;
        var request = new DecisionReplayRequest(
            DisabledConstraints: [.. form["disabledConstraints"]
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Select(static name => name!.Trim())],
            UseCandidatePolicy: IsSet(form["candidatePolicy"]));

        DecisionReplayOutcome outcome = await replay
            .ReplayAsync(httpContext, decisionReceiptId, request, cancellationToken)
            .ConfigureAwait(false);

        return outcome.NotFound
            ? Results.NotFound()
            : outcome.Result is null
            ? Results.BadRequest(new { message = outcome.Error })
            : Results.Ok(outcome.Result);
    }

    private static bool IsValidReceiptId(string decisionReceiptId)
    {
        return !string.IsNullOrWhiteSpace(decisionReceiptId) &&
            decisionReceiptId.Length <= DecisionReplayOptions.MaximumReceiptIdLength;
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
