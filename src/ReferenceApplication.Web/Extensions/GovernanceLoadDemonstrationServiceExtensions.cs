using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.Options;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.Governance.Demonstration;

namespace ReferenceApplication.Web.Extensions;

/// <summary>
/// Registers and maps the explicitly enabled, Development-only governance load demonstration.
/// </summary>
public static class GovernanceLoadDemonstrationServiceExtensions
{
    private const string _iterationsFormField = "iterations";
    private const string _durationFormField = "durationMilliseconds";
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Registers the bounded demonstration only when its switch is enabled in Development.
    /// </summary>
    public static IServiceCollection AddApplicationGovernanceLoadDemonstration(
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

        services
            .AddOptions<GovernanceLoadDemonstrationOptions>()
            .Bind(configuration.GetSection(GovernanceLoadDemonstrationOptions.SectionName))
            .Validate(
                options => options.MaximumIterations is >= 1 and <= GovernanceLoadDemonstrationOptions.AbsoluteMaximumIterations,
                $"MaximumIterations must be between 1 and {GovernanceLoadDemonstrationOptions.AbsoluteMaximumIterations}.")
            .Validate(
                options => options.DefaultIterations >= 1 && options.DefaultIterations <= options.MaximumIterations,
                "DefaultIterations must be between 1 and MaximumIterations.")
            .Validate(
                options => options.MaximumDurationMilliseconds is >= 1 and <= GovernanceLoadDemonstrationOptions.AbsoluteMaximumDurationMilliseconds,
                $"MaximumDurationMilliseconds must be between 1 and {GovernanceLoadDemonstrationOptions.AbsoluteMaximumDurationMilliseconds}.")
            .Validate(
                options => options.DefaultDurationMilliseconds >= 1 &&
                    options.DefaultDurationMilliseconds <= options.MaximumDurationMilliseconds,
                "DefaultDurationMilliseconds must be between 1 and MaximumDurationMilliseconds.")
            .Validate(
                options => options.ProgressIntervalMilliseconds is >= 10 and <= 1_000,
                "ProgressIntervalMilliseconds must be between 10 and 1000.")
            .ValidateOnStart();

        services.AddScoped<IGovernanceLoadDemonstrationExecutor, GovernanceLoadDemonstrationExecutor>();
        services.AddSingleton<GovernanceLoadDemonstrationService>();
        services.AddSingleton<IGovernanceLoadDemonstrationService>(serviceProvider =>
            serviceProvider.GetRequiredService<GovernanceLoadDemonstrationService>());
        return services;
    }

    /// <summary>
    /// Maps administrator-only start, cancel, and SSE routes only for an active Development demonstration.
    /// </summary>
    public static WebApplication MapApplicationGovernanceLoadDemonstration(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (!IsActive(app.Configuration, app.Environment) ||
            app.Services.GetService<IGovernanceLoadDemonstrationService>() is null)
        {
            return app;
        }

        RouteGroupBuilder routes = app
            .MapGroup("/api/v1/governance-load-demonstration")
            .RequireAuthorization(ApplicationAuthorizationPolicyNames.AdministratorRole);
        routes.MapPost("/start", StartAsync)
            .WithDisplayName("Start bounded governance load demonstration");
        routes.MapPost("/cancel", CancelAsync)
            .WithDisplayName("Cancel bounded governance load demonstration");
        routes.MapGet("/notifications", StreamNotificationsAsync)
            .WithDisplayName("Stream governance load demonstration metrics");
        return app;
    }

    /// <summary>
    /// Determines whether the feature switch is enabled in the Development environment.
    /// </summary>
    public static bool IsActive(IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment() && configuration.GetValue<bool>(
            $"{GovernanceLoadDemonstrationOptions.SectionName}:{nameof(GovernanceLoadDemonstrationOptions.Enabled)}");
    }

    private static async Task<IResult> StartAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IGovernanceLoadDemonstrationService demonstration,
        IOptions<GovernanceLoadDemonstrationOptions> optionsAccessor,
        CancellationToken cancellationToken)
    {
        if (!await IsAntiforgeryValidAsync(httpContext, antiforgery).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        IFormCollection form = await httpContext.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        GovernanceLoadDemonstrationOptions options = optionsAccessor.Value;
        if (!TryReadBound(form, _iterationsFormField, options.DefaultIterations, out int iterations) ||
            !TryReadBound(form, _durationFormField, options.DefaultDurationMilliseconds, out int durationMilliseconds))
        {
            return Results.BadRequest(new { message = "Iteration and duration bounds must be whole numbers." });
        }

        GovernanceLoadDemonstrationStartResult result = demonstration.TryStart(iterations, durationMilliseconds);
        return result.Status switch
        {
            GovernanceLoadDemonstrationStartStatus.Started => Results.Accepted(value: result.Snapshot),
            GovernanceLoadDemonstrationStartStatus.AlreadyRunning => Results.Conflict(new
            {
                message = "A governance load demonstration is already running.",
                current = result.Snapshot
            }),
            GovernanceLoadDemonstrationStartStatus.InvalidBounds => Results.BadRequest(new
            {
                message = $"Bounds must not exceed {options.MaximumIterations} iterations or {options.MaximumDurationMilliseconds} milliseconds.",
                current = result.Snapshot
            }),
            _ => throw new InvalidOperationException("The demonstration returned an unknown start status.")
        };
    }

    private static async Task<IResult> CancelAsync(
        HttpContext httpContext,
        IAntiforgery antiforgery,
        IGovernanceLoadDemonstrationService demonstration)
    {
        return !await IsAntiforgeryValidAsync(httpContext, antiforgery).ConfigureAwait(false)
            ? Results.BadRequest()
            : demonstration.TryCancel()
            ? Results.Accepted(value: demonstration.Current)
            : Results.Conflict(new
            {
                message = "No governance load demonstration is running.",
                current = demonstration.Current
            });
    }

    private static async Task StreamNotificationsAsync(
        HttpContext httpContext,
        IGovernanceLoadDemonstrationService demonstration,
        CancellationToken cancellationToken)
    {
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.CacheControl = "no-cache, no-store";
        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");
        await using IGovernanceLoadDemonstrationSubscription subscription = demonstration.Subscribe();
        await httpContext.Response.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await foreach (GovernanceLoadDemonstrationSnapshot snapshot in subscription
                .ReadAllAsync(cancellationToken)
                .ConfigureAwait(false))
            {
                await httpContext.Response.WriteAsync("event: load-demonstration\n", cancellationToken).ConfigureAwait(false);
                await httpContext.Response.WriteAsync(
                    $"data: {JsonSerializer.Serialize(snapshot, _jsonOptions)}\n\n",
                    cancellationToken).ConfigureAwait(false);
                await httpContext.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Client disconnects are the normal way an SSE subscription ends.
        }
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

    private static bool TryReadBound(IFormCollection form, string name, int defaultValue, out int value)
    {
        string submitted = form[name].ToString();
        if (string.IsNullOrWhiteSpace(submitted))
        {
            value = defaultValue;
            return true;
        }

        return int.TryParse(submitted, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
