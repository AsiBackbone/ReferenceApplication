using AsiBackbone.Core.Actors;
using AsiBackbone.Core.Audit;
using AsiBackbone.Core.Decisions;
using AsiBackbone.Core.Results;
using AsiBackbone.EntityFrameworkCore.Audit;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Extensions;
using ReferenceApplication.Web.Governance;

namespace ReferenceApplication.Benchmarks;

/// <summary>
/// Measures host-owned governance evidence persistence against an isolated, file-backed SQLite database.
/// </summary>
/// <remarks>
/// Schema creation and fixture preparation occur outside measured operations. Each measured persistence operation uses
/// the production host services and a fresh dependency-injection scope. SQLite uses WAL mode with connection pooling
/// disabled so cleanup is deterministic on every supported runner.
/// </remarks>
[MemoryDiagnoser]
[RankColumn]
[JsonExporterAttribute.Full]
[SimpleJob(
    RuntimeMoniker.Net10_0,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 10,
    invocationCount: 24)]
public class GovernancePersistenceBenchmarks
{
    private const int _invocationCount = 24;
    private const string _correlationId = "benchmark-correlation";
    private const string _traceId = "0af7651916cd43dd8448eb211c80319c";

    private ServiceProvider _serviceProvider = null!;
    private DecisionReceipt[] _decisionReceipts = null!;
    private ApplicationAuditCompletionMessage[] _completionMessages = null!;
    private string _databasePath = null!;
    private int _invocationIndex;
    private long _sequence;

    /// <summary>
    /// Creates and migrates the isolated SQLite benchmark database.
    /// </summary>
    [GlobalSetup]
    public void Setup()
    {
        _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"referenceapplication-governance-benchmarks-{Guid.NewGuid():N}.db");

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReferenceApplication:DataAccess:Provider"] = "Sqlite",
                ["ReferenceApplication:DataAccess:ConnectionStringName"] = "ApplicationDatabase",
                ["ReferenceApplication:DataAccess:Auditing:Enabled"] = "true",
                ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = "false",
                ["ConnectionStrings:ApplicationDatabase"] =
                    $"Data Source={_databasePath};Cache=Shared;Pooling=False"
            })
            .Build();

        ServiceCollection services = new();
        _ = services.AddSingleton(configuration);
        _ = services.AddLogging();
        _ = services.AddApplicationDataAccess(configuration);
        _ = services.AddApplicationAsiBackbone();
        _ = services.AddApplicationGovernanceEvidence(configuration);

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        using IServiceScope scope = _serviceProvider.CreateScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        dbContext.Database.Migrate();
        _ = dbContext.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    /// <summary>
    /// Disposes services and removes the isolated SQLite files.
    /// </summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _serviceProvider.Dispose();

        DeleteIfPresent(_databasePath);
        DeleteIfPresent(string.Concat(_databasePath, "-shm"));
        DeleteIfPresent(string.Concat(_databasePath, "-wal"));
    }

    /// <summary>
    /// Prepares a unique receipt outside the measured persistence operation.
    /// </summary>
    [IterationSetup(Target = nameof(PersistDecisionReceiptAsync))]
    public void SetupDecisionReceipt()
    {
        _invocationIndex = 0;
        _decisionReceipts = [.. Enumerable
            .Range(0, _invocationCount)
            .Select(_ => CreateDecisionReceipt(NextId("receipt")))];
    }

    /// <summary>
    /// Prepares an existing decision and a unique audit-completion delivery outside the measured operation.
    /// </summary>
    [IterationSetup(Target = nameof(PublishAuditCompletionDeliveredAsync))]
    public void SetupDeliveredCompletion()
    {
        _invocationIndex = 0;
        _completionMessages = [.. Enumerable
            .Range(0, _invocationCount)
            .Select(_ => CreateCompletionMessage(AppendDecision(NextId("delivered"))))];
    }

    /// <summary>
    /// Prepares a previously delivered event so the measured operation follows the duplicate-redelivery path.
    /// </summary>
    [IterationSetup(Target = nameof(PublishAuditCompletionDuplicateAsync))]
    public void SetupDuplicateCompletion()
    {
        _invocationIndex = 0;
        _completionMessages = [.. Enumerable
            .Range(0, _invocationCount)
            .Select(_ => CreateCompletionMessage(AppendDecision(NextId("duplicate"))))];

        foreach (ApplicationAuditCompletionMessage message in _completionMessages)
        {
            using IServiceScope scope = _serviceProvider.CreateScope();
            ApplicationAuditCompletionPublishResult initial = scope.ServiceProvider
                .GetRequiredService<IApplicationAuditCompletionPublisher>()
                .PublishAsync(message)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            if (initial.Disposition != ApplicationAuditCompletionPublishDisposition.Delivered)
            {
                throw new InvalidOperationException(
                    "The duplicate benchmark fixture could not create its initial delivery.");
            }
        }
    }

    /// <summary>
    /// Measures receipt mapping, hashing, and append to the SQLite-backed governance ledger.
    /// </summary>
    /// <returns>A task representing the persistence operation.</returns>
    [Benchmark(Description = "host_evidence.receipt_to_sqlite_ledger")]
    public async ValueTask PersistDecisionReceiptAsync()
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<IDecisionReceiptSink>()
            .WriteAsync(_decisionReceipts[_invocationIndex++])
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Measures first delivery of NCAT audit completion to the SQLite-backed lifecycle store.
    /// </summary>
    /// <returns>The publisher disposition.</returns>
    [Benchmark(Description = "host_evidence.audit_completion_delivered")]
    public async ValueTask<ApplicationAuditCompletionPublishResult> PublishAuditCompletionDeliveredAsync()
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IApplicationAuditCompletionPublisher>()
            .PublishAsync(_completionMessages[_invocationIndex++])
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Measures idempotent duplicate detection for an already delivered audit-completion event.
    /// </summary>
    /// <returns>The publisher disposition.</returns>
    [Benchmark(Description = "host_evidence.audit_completion_duplicate")]
    public async ValueTask<ApplicationAuditCompletionPublishResult> PublishAuditCompletionDuplicateAsync()
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IApplicationAuditCompletionPublisher>()
            .PublishAsync(_completionMessages[_invocationIndex++])
            .ConfigureAwait(false);
    }

    private string AppendDecision(string id)
    {
        DecisionReceipt receipt = CreateDecisionReceipt(id);
        var record = AuditLedgerRecord.FromDecisionReceipt(receipt);
        using IServiceScope scope = _serviceProvider.CreateScope();
        ApplicationDbContext dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        OperationResult<AuditLedgerRecord> result = new EfCoreAuditLedgerStore(dbContext)
            .AppendAsync(record)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        return result.Succeeded
            ? record.RecordId
            : throw new InvalidOperationException(
                "The audit-completion benchmark fixture could not append its decision.");
    }

    private static DecisionReceipt CreateDecisionReceipt(string id)
    {
        return DecisionReceipt.FromDecision(
            GovernanceActorContext.Human("benchmark-user", "Benchmark User"),
            "referenceapplication.benchmark.operation",
            GovernanceDecision.Allow(
                correlationId: string.Concat(_correlationId, '-', id),
                traceId: _traceId,
                policyVersion: ReferenceApplicationGovernancePolicySet.Version,
                policyHash: ReferenceApplicationGovernancePolicySet.PolicyHash),
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["benchmark.scenario"] = id
            });
    }

    private ApplicationAuditCompletionMessage CreateCompletionMessage(string decisionRecordId)
    {
        string id = NextId("completion");

        return new ApplicationAuditCompletionMessage(
            SchemaVersion: "1.0",
            Destination: AsiBackboneAuditCompletionPublisher.DestinationName,
            IdempotencyKey: id,
            MutationBatchId: string.Concat("batch-", id),
            AuditRecordCount: 2,
            PersistenceOutcome: "Committed",
            ReceiptCompletedUtc: DateTime.UtcNow,
            MutationManifestHash: "abcdef0123456789",
            MutationManifestAlgorithm: "SHA-256",
            MutationManifestSchemaVersion: "1.0",
            OperationExecutionId: string.Concat("execution-", id),
            ExecutionAttemptId: string.Concat("attempt-", id),
            DecisionAuditRecordId: decisionRecordId,
            CorrelationId: null,
            TraceId: _traceId);
    }

    private string NextId(string prefix)
    {
        return string.Concat(prefix, '-', Interlocked.Increment(ref _sequence).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
