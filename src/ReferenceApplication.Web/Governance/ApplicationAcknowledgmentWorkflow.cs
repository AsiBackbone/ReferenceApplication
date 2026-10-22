using System.Globalization;
using AsiBackbone.AspNetCore.Acknowledgments;
using AsiBackbone.AspNetCore.Actors;
using AsiBackbone.AspNetCore.Endpoints;
using AsiBackbone.Core.Acknowledgments;
using AsiBackbone.Core.Actors;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Infrastructure.Data;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Configures the host-owned completion step for acknowledgment-required operations.
/// </summary>
public sealed class ApplicationAcknowledgmentOptions
{
    /// <summary>
    /// The application configuration section.
    /// </summary>
    public const string SectionName = "ReferenceApplication:Acknowledgment";

    /// <summary>
    /// The default request header that carries an accepted acknowledgment identifier on the retry.
    /// </summary>
    public const string DefaultHeaderName = "X-ReferenceApplication-Acknowledgment";

    /// <summary>
    /// Gets or sets how long a challenge and its accepted acknowledgment remain valid.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the request header that carries the accepted acknowledgment identifier.
    /// </summary>
    public string HeaderName { get; set; } = DefaultHeaderName;
}

/// <summary>
/// Stable host-owned reason codes for the acknowledgment workflow.
/// </summary>
public static class ApplicationAcknowledgmentReasonCodes
{
    public const string Malformed = "referenceapplication.acknowledgment.malformed";
    public const string NotFound = "referenceapplication.acknowledgment.not_found";
    public const string Expired = "referenceapplication.acknowledgment.expired";
    public const string Replayed = "referenceapplication.acknowledgment.replayed";
    public const string OperationMismatch = "referenceapplication.acknowledgment.operation_mismatch";
    public const string Rejected = "referenceapplication.acknowledgment.rejected";
    public const string ActorMismatch = "acknowledgment.challenge.actor_mismatch";
}

internal interface IApplicationAcknowledgmentFeature
{
    string HandshakeId { get; }

    string AcknowledgmentId { get; }

    string OperationName { get; }

    DateTimeOffset ExpiresUtc { get; }
}

internal sealed record ApplicationAcknowledgmentFeature(
    string HandshakeId,
    string AcknowledgmentId,
    string OperationName,
    DateTimeOffset ExpiresUtc) : IApplicationAcknowledgmentFeature;

internal sealed record ApplicationAcknowledgmentValidationResult(
    bool Succeeded,
    string? ReasonCode = null,
    string? Message = null,
    IApplicationAcknowledgmentFeature? Feature = null);

internal sealed record ApplicationAcknowledgmentSubmissionResult(
    bool Succeeded,
    string? ReasonCode = null,
    string? Message = null,
    IApplicationAcknowledgmentFeature? Feature = null,
    string? HeaderName = null);

internal sealed record ApplicationAcknowledgmentConsumptionResult(
    bool Succeeded,
    string? ReasonCode = null,
    string? Message = null);

/// <summary>
/// Persists every framework-created acknowledgment challenge before it is returned to the caller.
/// </summary>
internal sealed class ApplicationAcknowledgmentChallengeService : IAcknowledgmentChallengeService
{
    internal const string IssuedUtcMetadataKey = "referenceapplication.acknowledgment.issued_utc";
    internal const string ExpiresUtcMetadataKey = "referenceapplication.acknowledgment.expires_utc";
    internal const string ConsumedUtcMetadataKey = "referenceapplication.acknowledgment.consumed_utc";

    private readonly DefaultAcknowledgmentChallengeService _inner;
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly ApplicationAcknowledgmentOptions _options;
    private readonly TimeProvider _timeProvider;

    public ApplicationAcknowledgmentChallengeService(
        IOptions<AcknowledgmentChallengeOptions> challengeOptions,
        IOptions<ApplicationAcknowledgmentOptions> options,
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(challengeOptions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dbContextFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _inner = new DefaultAcknowledgmentChallengeService(challengeOptions);
        _options = options.Value;
        _dbContextFactory = dbContextFactory;
        _timeProvider = timeProvider;
    }

    public AcknowledgmentChallenge CreateChallenge(
        IGovernanceActorContext actor,
        string operationName,
        AsiBackbone.Core.Decisions.GovernanceDecision decision,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        AcknowledgmentChallenge challenge = _inner.CreateChallenge(actor, operationName, decision, metadata);
        PersistChallenge(challenge.AcknowledgmentRequest);
        return challenge;
    }

    public AcknowledgmentChallengeResult HandleResponse(
        AcknowledgmentChallenge challenge,
        IGovernanceActorContext actor,
        AcknowledgmentChallengeRequest response,
        DateTimeOffset? occurredUtc = null)
    {
        return _inner.HandleResponse(challenge, actor, response, occurredUtc);
    }

    private void PersistChallenge(AcknowledgmentRequest request)
    {
        DateTimeOffset issuedUtc = _timeProvider.GetUtcNow();
        DateTimeOffset expiresUtc = issuedUtc.Add(_options.Lifetime);

        using ApplicationDbContext dbContext = _dbContextFactory.CreateDbContext();
        var entity = new AcknowledgmentRequestEntity
        {
            HandshakeId = request.HandshakeId,
            SchemaVersion = request.SchemaVersion,
            ActorId = request.ActorId,
            ActorType = request.ActorType,
            ActorDisplayName = request.ActorDisplayName,
            OperationName = request.OperationName,
            ReasonCode = request.ReasonCode,
            Message = request.Message,
            RequiredAcknowledgmentCode = request.RequiredAcknowledgmentCode,
            RequiredAcknowledgmentText = request.RequiredAcknowledgmentText,
            RiskLevel = request.RiskLevel,
            RiskCategory = request.RiskCategory,
            CorrelationId = request.CorrelationId,
            TraceId = request.TraceId,
            PolicyVersion = request.PolicyVersion,
            PolicyHash = request.PolicyHash
        };

        _ = dbContext.Set<AcknowledgmentRequestEntity>().Add(entity);

        Dictionary<string, string> persistedMetadata = new(request.Metadata, StringComparer.Ordinal)
        {
            [IssuedUtcMetadataKey] = issuedUtc.ToString("O", CultureInfo.InvariantCulture),
            [ExpiresUtcMetadataKey] = expiresUtc.ToString("O", CultureInfo.InvariantCulture)
        };

        foreach (KeyValuePair<string, string> item in persistedMetadata)
        {
            _ = dbContext.Set<AcknowledgmentRequestMetadataEntity>().Add(new AcknowledgmentRequestMetadataEntity
            {
                AcknowledgmentRequestId = entity.Id,
                MetadataKey = item.Key,
                MetadataValue = item.Value
            });
        }

        _ = dbContext.SaveChanges();
    }
}

/// <summary>
/// Completes and validates persisted acknowledgment handshakes for consequential retries.
/// </summary>
public sealed class ApplicationAcknowledgmentWorkflow
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly IAcknowledgmentChallengeService _challengeService;
    private readonly IHttpGovernanceActorContextResolver _actorContextResolver;
    private readonly ApplicationAcknowledgmentOptions _options;
    private readonly TimeProvider _timeProvider;

    public ApplicationAcknowledgmentWorkflow(
        IDbContextFactory<ApplicationDbContext> dbContextFactory,
        IAcknowledgmentChallengeService challengeService,
        IHttpGovernanceActorContextResolver actorContextResolver,
        IOptions<ApplicationAcknowledgmentOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContextFactory);
        ArgumentNullException.ThrowIfNull(challengeService);
        ArgumentNullException.ThrowIfNull(actorContextResolver);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _dbContextFactory = dbContextFactory;
        _challengeService = challengeService;
        _actorContextResolver = actorContextResolver;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    internal async ValueTask<ApplicationAcknowledgmentSubmissionResult> SubmitAsync(
        AcknowledgmentChallengeRequest response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        string? handshakeId = NormalizeIdentifier(response.HandshakeId);
        if (handshakeId is null)
        {
            return SubmissionFailure(
                ApplicationAcknowledgmentReasonCodes.Malformed,
                "The acknowledgment response did not identify a handshake.");
        }

        await using ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        AcknowledgmentRequestEntity? requestEntity = await dbContext.Set<AcknowledgmentRequestEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                request => request.HandshakeId == handshakeId,
                cancellationToken)
            .ConfigureAwait(false);

        if (requestEntity is null)
        {
            return SubmissionFailure(
                ApplicationAcknowledgmentReasonCodes.NotFound,
                "The acknowledgment challenge is unavailable.");
        }

        if (await dbContext.Set<AcknowledgmentResponseEntity>()
            .AsNoTracking()
            .AnyAsync(item => item.HandshakeId == handshakeId, cancellationToken)
            .ConfigureAwait(false))
        {
            return SubmissionFailure(
                ApplicationAcknowledgmentReasonCodes.Replayed,
                "The acknowledgment challenge has already been answered.");
        }

        DateTimeOffset? expiresUtc = await ReadExpiresUtcAsync(dbContext, requestEntity.Id, cancellationToken)
            .ConfigureAwait(false);
        if (expiresUtc is null)
        {
            return SubmissionFailure(
                ApplicationAcknowledgmentReasonCodes.Malformed,
                "The acknowledgment challenge has no valid expiration.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (now > expiresUtc.Value)
        {
            return SubmissionFailure(
                ApplicationAcknowledgmentReasonCodes.Expired,
                "The acknowledgment challenge has expired.");
        }

        AcknowledgmentRequest request = ToRequest(requestEntity);
        var challenge = AcknowledgmentChallenge.FromAcknowledgmentRequest(request);
        IGovernanceActorContext actor = _actorContextResolver.ResolveActorContext();

        AcknowledgmentChallengeResult handled = _challengeService.HandleResponse(
            challenge,
            actor,
            response,
            now);

        if (!handled.Succeeded || handled.Acknowledgment is null)
        {
            string reasonCode = handled.Result.Reasons.Count > 0
                ? handled.Result.Reasons[0].Code
                : ApplicationAcknowledgmentReasonCodes.Malformed;
            string message = handled.Result.Reasons.Count > 0
                ? handled.Result.Reasons[0].Message
                : "The acknowledgment response was rejected.";

            return SubmissionFailure(reasonCode, message);
        }

        AcknowledgmentResponse acknowledgment = handled.Acknowledgment;
        await PersistResponseAsync(dbContext, acknowledgment, cancellationToken).ConfigureAwait(false);

        if (!handled.CanProceed)
        {
            return SubmissionFailure(
                ApplicationAcknowledgmentReasonCodes.Rejected,
                "The actor rejected the acknowledgment challenge.");
        }

        IApplicationAcknowledgmentFeature feature = new ApplicationAcknowledgmentFeature(
            acknowledgment.HandshakeId,
            acknowledgment.AcknowledgmentId,
            request.OperationName,
            expiresUtc.Value);

        return new ApplicationAcknowledgmentSubmissionResult(
            Succeeded: true,
            Feature: feature,
            HeaderName: _options.HeaderName);
    }

    internal async ValueTask<ApplicationAcknowledgmentValidationResult> AttachRetryAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (!httpContext.Request.Headers.TryGetValue(_options.HeaderName, out StringValues values))
        {
            return new ApplicationAcknowledgmentValidationResult(Succeeded: true);
        }

        if (values.Count != 1 || NormalizeIdentifier(values[0]) is not { } acknowledgmentId)
        {
            return ValidationFailure(
                ApplicationAcknowledgmentReasonCodes.Malformed,
                "The acknowledgment retry header is malformed.");
        }

        await using ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        AcknowledgmentResponseEntity? acknowledgment = await dbContext.Set<AcknowledgmentResponseEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.AcknowledgmentId == acknowledgmentId,
                cancellationToken)
            .ConfigureAwait(false);

        if (acknowledgment is null || !acknowledgment.Acknowledged)
        {
            return ValidationFailure(
                ApplicationAcknowledgmentReasonCodes.NotFound,
                "The accepted acknowledgment is unavailable.");
        }

        if (await IsConsumedAsync(dbContext, acknowledgment.Id, cancellationToken).ConfigureAwait(false))
        {
            return ValidationFailure(
                ApplicationAcknowledgmentReasonCodes.Replayed,
                "The acknowledgment has already been used.");
        }

        AcknowledgmentRequestEntity? request = await dbContext.Set<AcknowledgmentRequestEntity>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.HandshakeId == acknowledgment.HandshakeId,
                cancellationToken)
            .ConfigureAwait(false);

        if (request is null)
        {
            return ValidationFailure(
                ApplicationAcknowledgmentReasonCodes.NotFound,
                "The acknowledgment challenge is unavailable.");
        }

        DateTimeOffset? expiresUtc = await ReadExpiresUtcAsync(dbContext, request.Id, cancellationToken)
            .ConfigureAwait(false);
        if (expiresUtc is null || _timeProvider.GetUtcNow() > expiresUtc.Value)
        {
            return ValidationFailure(
                ApplicationAcknowledgmentReasonCodes.Expired,
                "The acknowledgment has expired.");
        }

        IGovernanceActorContext actor = _actorContextResolver.ResolveActorContext();
        if (!actor.IsAuthenticated
            || !string.Equals(actor.ActorId, acknowledgment.ActorId, StringComparison.Ordinal)
            || actor.ActorType != acknowledgment.ActorType)
        {
            return ValidationFailure(
                ApplicationAcknowledgmentReasonCodes.ActorMismatch,
                "The acknowledgment belongs to a different actor.");
        }

        Endpoint? endpoint = httpContext.GetEndpoint();
        if (endpoint is null)
        {
            return ValidationFailure(
                ApplicationAcknowledgmentReasonCodes.OperationMismatch,
                "The acknowledgment retry did not resolve a governed operation.");
        }

        var descriptor = EndpointGovernanceDescriptor.FromEndpoint(endpoint);
        if (!string.Equals(request.OperationName, descriptor.OperationName, StringComparison.Ordinal))
        {
            return ValidationFailure(
                ApplicationAcknowledgmentReasonCodes.OperationMismatch,
                "The acknowledgment was issued for a different operation.");
        }

        var feature = new ApplicationAcknowledgmentFeature(
            request.HandshakeId,
            acknowledgment.AcknowledgmentId,
            request.OperationName,
            expiresUtc.Value);

        httpContext.Features.Set<IApplicationAcknowledgmentFeature>(feature);
        return new ApplicationAcknowledgmentValidationResult(Succeeded: true, Feature: feature);
    }

    internal async ValueTask<ApplicationAcknowledgmentConsumptionResult> ConsumeAsync(
        IApplicationAcknowledgmentFeature feature,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feature);

        await using ApplicationDbContext dbContext = await _dbContextFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);

        AcknowledgmentResponseEntity? acknowledgment = await dbContext.Set<AcknowledgmentResponseEntity>()
            .SingleOrDefaultAsync(
                item => item.AcknowledgmentId == feature.AcknowledgmentId
                    && item.HandshakeId == feature.HandshakeId
                    && item.Acknowledged,
                cancellationToken)
            .ConfigureAwait(false);

        if (acknowledgment is null)
        {
            return ConsumptionFailure(
                ApplicationAcknowledgmentReasonCodes.NotFound,
                "The accepted acknowledgment is unavailable.");
        }

        if (_timeProvider.GetUtcNow() > feature.ExpiresUtc)
        {
            return ConsumptionFailure(
                ApplicationAcknowledgmentReasonCodes.Expired,
                "The acknowledgment has expired.");
        }

        if (await IsConsumedAsync(dbContext, acknowledgment.Id, cancellationToken).ConfigureAwait(false))
        {
            return ConsumptionFailure(
                ApplicationAcknowledgmentReasonCodes.Replayed,
                "The acknowledgment has already been used.");
        }

        _ = dbContext.Set<AcknowledgmentResponseMetadataEntity>().Add(new AcknowledgmentResponseMetadataEntity
        {
            AcknowledgmentResponseId = acknowledgment.Id,
            MetadataKey = ApplicationAcknowledgmentChallengeService.ConsumedUtcMetadataKey,
            MetadataValue = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)
        });

        try
        {
            _ = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // The metadata key is unique per response. Concurrent retries race on that key, so only one can consume it.
            return ConsumptionFailure(
                ApplicationAcknowledgmentReasonCodes.Replayed,
                "The acknowledgment has already been used.");
        }

        return new ApplicationAcknowledgmentConsumptionResult(Succeeded: true);
    }

    private static async ValueTask PersistResponseAsync(
        ApplicationDbContext dbContext,
        AcknowledgmentResponse acknowledgment,
        CancellationToken cancellationToken)
    {
        var entity = new AcknowledgmentResponseEntity
        {
            AcknowledgmentId = acknowledgment.AcknowledgmentId,
            SchemaVersion = acknowledgment.SchemaVersion,
            HandshakeId = acknowledgment.HandshakeId,
            ActorId = acknowledgment.ActorId,
            ActorType = acknowledgment.ActorType,
            ActorDisplayName = acknowledgment.ActorDisplayName,
            AcknowledgmentCode = acknowledgment.AcknowledgmentCode,
            Acknowledged = acknowledgment.Acknowledged,
            OccurredUtc = acknowledgment.OccurredUtc,
            CorrelationId = acknowledgment.CorrelationId,
            TraceId = acknowledgment.TraceId
        };

        _ = dbContext.Set<AcknowledgmentResponseEntity>().Add(entity);

        foreach (KeyValuePair<string, string> item in acknowledgment.Metadata)
        {
            _ = dbContext.Set<AcknowledgmentResponseMetadataEntity>().Add(new AcknowledgmentResponseMetadataEntity
            {
                AcknowledgmentResponseId = entity.Id,
                MetadataKey = item.Key,
                MetadataValue = item.Value
            });
        }

        _ = await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AcknowledgmentRequest ToRequest(AcknowledgmentRequestEntity entity)
    {
        var actor = new PersistedActorContext(
            entity.ActorId,
            entity.ActorType,
            entity.ActorDisplayName);

        return AcknowledgmentRequest.Create(
            actor,
            entity.OperationName,
            entity.ReasonCode,
            entity.Message,
            entity.RequiredAcknowledgmentCode,
            entity.RequiredAcknowledgmentText,
            entity.RiskLevel,
            entity.RiskCategory,
            entity.HandshakeId,
            entity.CorrelationId,
            entity.TraceId,
            entity.PolicyVersion,
            entity.PolicyHash,
            metadata: null,
            entity.SchemaVersion);
    }

    private static async ValueTask<DateTimeOffset?> ReadExpiresUtcAsync(
        ApplicationDbContext dbContext,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        string? value = await dbContext.Set<AcknowledgmentRequestMetadataEntity>()
            .AsNoTracking()
            .Where(item => item.AcknowledgmentRequestId == requestId
                && item.MetadataKey == ApplicationAcknowledgmentChallengeService.ExpiresUtcMetadataKey)
            .Select(item => item.MetadataValue)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset expiresUtc)
            ? expiresUtc
            : null;
    }

    private static ValueTask<bool> IsConsumedAsync(
        ApplicationDbContext dbContext,
        Guid responseId,
        CancellationToken cancellationToken)
    {
        return new ValueTask<bool>(dbContext.Set<AcknowledgmentResponseMetadataEntity>()
            .AsNoTracking()
            .AnyAsync(
                item => item.AcknowledgmentResponseId == responseId
                    && item.MetadataKey == ApplicationAcknowledgmentChallengeService.ConsumedUtcMetadataKey,
                cancellationToken));
    }

    private static string? NormalizeIdentifier(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static ApplicationAcknowledgmentSubmissionResult SubmissionFailure(string reasonCode, string message)
    {
        return new ApplicationAcknowledgmentSubmissionResult(false, reasonCode, message);
    }

    private static ApplicationAcknowledgmentValidationResult ValidationFailure(string reasonCode, string message)
    {
        return new ApplicationAcknowledgmentValidationResult(false, reasonCode, message);
    }

    private static ApplicationAcknowledgmentConsumptionResult ConsumptionFailure(string reasonCode, string message)
    {
        return new ApplicationAcknowledgmentConsumptionResult(false, reasonCode, message);
    }

    private sealed record PersistedActorContext(
        string ActorId,
        GovernanceActorType ActorType,
        string? DisplayName) : IGovernanceActorContext
    {
        public bool IsKnown => true;

        public bool IsAuthenticated => true;
    }
}

internal sealed class ApplicationAcknowledgmentMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next ?? throw new ArgumentNullException(nameof(next));

    public async Task InvokeAsync(
        HttpContext httpContext,
        ApplicationAcknowledgmentWorkflow workflow)
    {
        ApplicationAcknowledgmentValidationResult validation = await workflow
            .AttachRetryAsync(httpContext, httpContext.RequestAborted)
            .ConfigureAwait(false);

        if (!validation.Succeeded)
        {
            await Results.Problem(
                title: "Acknowledgment denied.",
                detail: validation.Message,
                statusCode: StatusCodes.Status403Forbidden,
                instance: httpContext.Request.Path,
                extensions: new Dictionary<string, object?>
                {
                    ["reasonCodes"] = new[] { validation.ReasonCode }
                })
                .ExecuteAsync(httpContext)
                .ConfigureAwait(false);
            return;
        }

        await _next(httpContext).ConfigureAwait(false);
    }
}
