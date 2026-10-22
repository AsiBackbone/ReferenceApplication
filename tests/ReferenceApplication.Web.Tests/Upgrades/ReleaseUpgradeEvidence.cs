using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AsiBackbone.AspNetCore.CapabilityGrants;
using AsiBackbone.EntityFrameworkCore.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using ReferenceApplication.Infrastructure.Data;
using ReferenceApplication.Infrastructure.Data.Auditing;
using ReferenceApplication.Web.Authentication.Claims;
using ReferenceApplication.Web.Controllers.Api;
using ReferenceApplication.Web.Governance;
using ReferenceApplication.Web.Tests.Extensions;
using ReferenceApplication.Web.Tests.Infrastructure;

namespace ReferenceApplication.Web.Tests.Upgrades;

/// <summary>
/// Creates, captures, and checks the representative evidence a release-to-release upgrade must preserve.
/// </summary>
/// <remarks>
/// <para>
/// The evidence is produced through the application's own paths, never written by hand: governed requests through the
/// real endpoints, outbox delivery through the dispatcher, reconciliation through the reconciler, and a signed
/// capability token issued and used through the real issuance and governed endpoints. It covers an allowed and a denied
/// decision, governed records with NCAT mutation audit and manifests, a delivered audit completion with its lifecycle
/// event, a reconciliation finding, and a capability-grant use.
/// </para>
/// <para>
/// A database holding that evidence is captured as SQL insert statements, so a later release can recreate it exactly on
/// the earlier release's schema, upgrade it, and check that nothing was lost or changed.
/// </para>
/// </remarks>
internal static class ReleaseUpgradeEvidence
{
    /// <summary>The release the fixture was captured from: the oldest release an upgrade is supported from.</summary>
    public const string BoundaryRelease = "v0.1.0-alpha.11";

    /// <summary>The last migration the boundary release shipped.</summary>
    public const string BoundaryMigration = "20261021151500_AddCapabilityGrantUses";

    /// <summary>The repository-relative directory holding the boundary release's fixture and migration manifest.</summary>
    public const string BoundaryDirectory = "tests/ReferenceApplication.Web.Tests/Upgrades/" + BoundaryRelease;

    /// <summary>The fixture captured at the boundary release.</summary>
    public const string FixturePath = BoundaryDirectory + "/evidence.sql";

    /// <summary>The hashes of the migrations the boundary release shipped.</summary>
    public const string MigrationManifestPath = BoundaryDirectory + "/migrations.sha256";

    private const string _authenticationScheme = "ReleaseUpgradeTestAuthentication";
    private const string _userHeader = "X-Test-User";
    private const string _roleHeader = "X-Test-Role";
    private const string _permissionHeader = "X-Test-Permission";
    private const string _administratorRole = "administrator";
    private const string _antiforgeryField = "__RequestVerificationToken";
    private const string _migrationHistoryTable = "__EFMigrationsHistory";

    /// <summary>
    /// Creates a host on a database file, in Development with reconciliation registered and every background worker held
    /// off, so the caller drives each step.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="outboxEnabled">Whether audit completions are staged and delivered.</param>
    /// <param name="signingKey">The capability token signing key, or <see langword="null" /> to leave tokens disabled.</param>
    /// <param name="migrate">Whether to apply every migration first.</param>
    /// <returns>The host factory.</returns>
    public static Task<WebApplicationFactory<Program>> CreateHostAsync(
        TestSqliteDatabase database,
        bool outboxEnabled = true,
        RSA? signingKey = null,
        bool migrate = false)
    {
        ArgumentNullException.ThrowIfNull(database);

        Dictionary<string, string?> configuration = new()
        {
            ["ReferenceApplication:Authentication:Enabled"] = "true",
            ["ReferenceApplication:Authentication:DefaultScheme"] = _authenticationScheme,
            ["ReferenceApplication:Authentication:DefaultChallengeScheme"] = _authenticationScheme,
            ["ReferenceApplication:Authentication:DefaultSignInScheme"] = _authenticationScheme,
            ["ReferenceApplication:Authentication:Cookie:Enabled"] = "true",
            ["ReferenceApplication:Authentication:Cookie:Scheme"] = "Cookies",
            ["ReferenceApplication:Authorization:RoleClaimType"] = ApplicationClaimTypes.Role,
            ["ReferenceApplication:Authorization:PermissionClaimType"] = ApplicationClaimTypes.Permission,
            ["ReferenceApplication:Authorization:AdministratorRoles:0"] = _administratorRole,
            ["ReferenceApplication:AuditCompletionOutbox:Enabled"] = outboxEnabled ? "true" : "false",
            ["ReferenceApplication:AuditReconciliation:RunWorker"] = "false",
            ["ReferenceApplication:AuditReconciliation:CompletionGracePeriod"] = "00:00:00",
            ["ReferenceApplication:HealthChecks:DatabaseReadinessCheckEnabled"] = "true"
        };

        return database.ApplyAsync(
            ApplicationWebApplicationFactory
                .CreateAllowingAnonymousAccess(configuration, Environments.Development)
                .WithWebHostBuilder(builder =>
                {
                    // Feature switches are read during service registration, so they are pinned as host settings. The
                    // pipeline inspector is the component that registers NCAT reconciliation in this host.
                    _ = builder
                        .UseSetting("ReferenceApplication:PipelineInspector:Enabled", "true")
                        .UseSetting("ReferenceApplication:DemoIdentity:Enabled", "false")
                        .UseSetting("ReferenceApplication:DecisionReplay:Enabled", "false")
                        .UseSetting("ReferenceApplication:PolicySimulator:Enabled", "false")
                        .UseSetting("ReferenceApplication:GovernanceLoadDemonstration:Enabled", "false")
                        .UseSetting("ReferenceApplication:FailureScenarios:Enabled", "false")
                        .UseSetting("ReferenceApplication:CapabilityTokens:Enabled", signingKey is null ? "false" : "true");
                    if (signingKey is not null)
                    {
                        _ = builder
                            .UseSetting("ReferenceApplication:CapabilityTokens:SigningKeyPem", signingKey.ExportPkcs8PrivateKeyPem())
                            .UseSetting("ReferenceApplication:CapabilityTokens:VerificationKeys:0:KeyVersion", "1")
                            .UseSetting(
                                "ReferenceApplication:CapabilityTokens:VerificationKeys:0:PublicKeyPem",
                                signingKey.ExportSubjectPublicKeyInfoPem());
                    }

                    _ = builder.ConfigureServices(static services => services
                        .AddAuthentication()
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(_authenticationScheme, static _ => { }));
                }),
            migrate);
    }

    /// <summary>
    /// Produces the representative evidence in a migrated database, through the application's own paths.
    /// </summary>
    /// <param name="database">The database, already migrated to the boundary.</param>
    /// <param name="cancellationToken">A token that cancels the work.</param>
    /// <returns>A task that completes when the evidence is written.</returns>
    public static async Task ProduceAsync(TestSqliteDatabase database, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        // A record whose audit completion is never staged: reconciliation reports it as a missing completion.
        using (WebApplicationFactory<Program> withoutOutbox = await CreateHostAsync(database, outboxEnabled: false))
        {
            using HttpClient client = withoutOutbox.CreateHttpsClient();
            await PostAsync(client, "/api/v1/governed-operations/records", "upgrade-administrator", _administratorRole, null,
                new() { ["Title"] = "Upgrade fixture: record without a completion" }, HttpStatusCode.Created, cancellationToken);

            // A denied decision: the actor lacks the administrator role.
            await PostAsync(client, "/api/v1/governed-operations/administrator", "upgrade-reader", null, null,
                [], HttpStatusCode.Forbidden, cancellationToken);
        }

        using var signingKey = RSA.Create(2048);
        using WebApplicationFactory<Program> host = await CreateHostAsync(database, outboxEnabled: true, signingKey);
        using (HttpClient client = host.CreateHttpsClient())
        {
            // A record whose completion is delivered, appending its lifecycle event.
            await PostAsync(client, "/api/v1/governed-operations/records", "upgrade-administrator", _administratorRole, null,
                new() { ["Title"] = "Upgrade fixture: delivered record" }, HttpStatusCode.Created, cancellationToken);

            // A signed capability token, issued and used once: an allowed decision carrying the token identifier, and a
            // capability-grant use record.
            const string capabilityScope = ReferenceApplicationGovernancePolicySet.ConsequentialExecutionScope;
            string issued = await PostAsync(client, "/api/v1/capability-tokens", "upgrade-operator", null, capabilityScope,
                new()
                {
                    ["operation"] = "capability",
                    [CapabilityTokensController.PayloadFieldPrefix + "Note"] = "upgrade fixture"
                },
                HttpStatusCode.OK,
                cancellationToken);
            using var token = JsonDocument.Parse(issued);
            await PostAsync(client, "/api/v1/governed-operations/capability", "upgrade-operator", null, capabilityScope,
                new() { ["Note"] = "upgrade fixture" },
                HttpStatusCode.OK,
                cancellationToken,
                (SignedCapabilityGrantOptions.DefaultTokenHeaderName, token.RootElement.GetProperty("token").GetString()!));
        }

        await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
        ApplicationAuditCompletionDispatchSummary dispatched = await scope.ServiceProvider
            .GetRequiredService<IApplicationAuditCompletionOutboxDispatcher>()
            .DispatchReadyAsync(cancellationToken);
        Assert.Equal(1, dispatched.DeliveredCount);

        ApplicationAuditReconciliationSummary reconciled = await scope.ServiceProvider
            .GetRequiredService<IApplicationAuditReconciler>()
            .ReconcileAsync(cancellationToken);
        Assert.True(reconciled.MissingCompletionCount >= 1, "The record without a completion was not reported.");
    }

    /// <summary>
    /// Captures every application row of a database as SQL insert statements, in a stable order.
    /// </summary>
    /// <param name="databasePath">The database file.</param>
    /// <returns>The SQL script.</returns>
    public static string Capture(string databasePath)
    {
        StringBuilder script = new();
        _ = script
            .Append("-- Representative evidence captured from ReferenceApplication ").Append(BoundaryRelease)
            .Append(" through its own application paths.\n")
            .Append("-- Load it into a database migrated to ").Append(BoundaryMigration)
            .Append(". Regenerate it only for a new boundary; see README.md.\n")
            .Append("PRAGMA foreign_keys = OFF;\nBEGIN TRANSACTION;\n");

        using SqliteConnection connection = Open(databasePath);
        foreach (string table in ReadTableNames(connection))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
            using SqliteDataReader reader = command.ExecuteReader();
            string columns = string.Join(", ", Enumerable.Range(0, reader.FieldCount).Select(index => $"\"{reader.GetName(index)}\""));
            while (reader.Read())
            {
                string values = string.Join(", ", Enumerable.Range(0, reader.FieldCount).Select(index => ToLiteral(reader.GetValue(index))));
                _ = script.Append("INSERT INTO \"").Append(table).Append("\" (").Append(columns).Append(") VALUES (")
                    .Append(values).Append(");\n");
            }
        }

        return script.Append("COMMIT;\n").ToString();
    }

    /// <summary>
    /// Runs a SQL script against a database file.
    /// </summary>
    /// <param name="databasePath">The database file.</param>
    /// <param name="sql">The script.</param>
    public static void Execute(string databasePath, string sql)
    {
        using SqliteConnection connection = Open(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads every application row of a database, keyed by table and primary key.
    /// </summary>
    /// <param name="databasePath">The database file.</param>
    /// <returns>The rows, by table and then by primary key; each row maps column names to values.</returns>
    public static Dictionary<string, Dictionary<string, Dictionary<string, object?>>> Snapshot(string databasePath)
    {
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> tables = new(StringComparer.Ordinal);
        using SqliteConnection connection = Open(databasePath);
        foreach (string table in ReadTableNames(connection))
        {
            string[] keyColumns = ReadPrimaryKey(connection, table);
            Dictionary<string, Dictionary<string, object?>> rows = new(StringComparer.Ordinal);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                Dictionary<string, object?> row = new(StringComparer.Ordinal);
                for (int index = 0; index < reader.FieldCount; index++)
                {
                    row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                }

                string key = keyColumns.Length == 0
                    ? $"rowid:{rows.Count}"
                    : string.Join("|", keyColumns.Select(column => Convert.ToString(row[column], CultureInfo.InvariantCulture)));
                rows[key] = row;
            }

            tables[table] = rows;
        }

        return tables;
    }

    /// <summary>
    /// Reports every way an upgraded database lost or changed evidence the earlier database held. New tables, new
    /// columns, and new rows are allowed; a missing table, column, or row, or a changed value, is not.
    /// </summary>
    /// <param name="before">The snapshot before the upgrade.</param>
    /// <param name="after">The snapshot after the upgrade.</param>
    /// <returns>The problems found; empty when every earlier row survived unchanged.</returns>
    public static List<string> CompareSnapshots(
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> before,
        Dictionary<string, Dictionary<string, Dictionary<string, object?>>> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        List<string> problems = [];
        foreach ((string table, Dictionary<string, Dictionary<string, object?>> rows) in before)
        {
            if (!after.TryGetValue(table, out Dictionary<string, Dictionary<string, object?>>? upgradedRows))
            {
                problems.Add($"Table {table} is missing after the upgrade.");
                continue;
            }

            foreach ((string key, Dictionary<string, object?> row) in rows)
            {
                if (!upgradedRows.TryGetValue(key, out Dictionary<string, object?>? upgradedRow))
                {
                    problems.Add($"Row {table}[{key}] is missing after the upgrade.");
                    continue;
                }

                foreach ((string column, object? value) in row)
                {
                    if (!upgradedRow.TryGetValue(column, out object? upgradedValue))
                    {
                        problems.Add($"Column {table}.{column} is missing after the upgrade.");
                    }
                    else if (!Equals(value, upgradedValue))
                    {
                        problems.Add($"Value {table}[{key}].{column} changed during the upgrade.");
                    }
                }
            }
        }

        // One report per missing column is enough.
        return [.. problems.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Checks, through the upgraded application's own services, that the evidence still verifies and still links.
    /// </summary>
    /// <param name="host">A host on the upgraded database.</param>
    /// <param name="cancellationToken">A token that cancels the checks.</param>
    /// <returns>The problems found; empty when every check passes.</returns>
    public static async Task<List<string>> VerifyEvidenceAsync(WebApplicationFactory<Program> host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);

        List<string> problems = [];
        try
        {
            using HttpClient client = host.CreateHttpsClient();
            using HttpResponseMessage ready = await client.GetAsync("/health/ready", cancellationToken);
            if (ready.StatusCode != HttpStatusCode.OK)
            {
                problems.Add($"The upgraded application is not ready: /health/ready returned {(int)ready.StatusCode}.");
            }

            await using AsyncServiceScope scope = host.Services.CreateAsyncScope();
            IServiceProvider services = scope.ServiceProvider;
            ApplicationDbContext dbContext = services.GetRequiredService<ApplicationDbContext>();

            var ledger = await dbContext.AsiBackboneAuditLedgerRecords.AsNoTracking()
                .Select(static record => new { record.RecordId, record.CorrelationId, record.CapabilityTokenId })
                .ToListAsync(cancellationToken);
            HashSet<string> recordIds = [.. ledger.Select(static record => record.RecordId)];
            if (ledger.Count == 0)
            {
                problems.Add("The upgraded ledger holds no decision.");
            }

            // Every decision record still hashes to the value stored with it.
            IGovernanceEvidenceVerifier verifier = services.GetRequiredService<IGovernanceEvidenceVerifier>();
            foreach (string correlationId in ledger.Select(static record => record.CorrelationId).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                GovernanceEvidenceVerificationResponse? verification = await verifier.VerifyAsync(correlationId, cancellationToken);
                foreach (GovernanceEvidenceRecordVerification record in verification?.Records ?? [])
                {
                    if (record.Status != GovernanceEvidenceVerificationStatuses.Verified)
                    {
                        problems.Add($"Decision record {record.RecordId} no longer verifies: {record.Status}.");
                    }
                }
            }

            // Mutation audit and audit completions still link to an existing decision.
            foreach (string? decisionId in await dbContext.AuditRecords.AsNoTracking()
                .Select(static record => record.DecisionAuditRecordId).Distinct().ToListAsync(cancellationToken))
            {
                if (decisionId is not null && !recordIds.Contains(decisionId))
                {
                    problems.Add($"Mutation audit links to decision {decisionId}, which is missing.");
                }
            }

            var completions = await dbContext.ApplicationAuditCompletionOutboxEntries.AsNoTracking()
                .Select(static entry => new { entry.IdempotencyKey, entry.DecisionAuditRecordId, entry.Status, entry.CorrelationId })
                .ToListAsync(cancellationToken);
            if (completions.Count == 0)
            {
                problems.Add("The upgraded outbox holds no audit completion.");
            }

            HashSet<string> lifecycleEventIds = [.. await dbContext.AsiBackboneDecisionReceiptLifecycleEvents.AsNoTracking()
                .Select(static lifecycleEvent => lifecycleEvent.EventId).ToListAsync(cancellationToken)];
            foreach (var completion in completions)
            {
                if (completion.DecisionAuditRecordId is not null && !recordIds.Contains(completion.DecisionAuditRecordId))
                {
                    problems.Add($"Audit completion {completion.IdempotencyKey} links to a missing decision.");
                }

                if (completion.Status == ApplicationAuditCompletionOutboxStatuses.Delivered &&
                    !lifecycleEventIds.Contains(AsiBackboneAuditCompletionPublisher.CreateLifecycleEventId(completion.IdempotencyKey)))
                {
                    problems.Add($"Delivered audit completion {completion.IdempotencyKey} has lost its lifecycle event.");
                }
            }

            // The timeline of a delivered request still assembles every kind of evidence.
            if (completions.FirstOrDefault(static entry => entry.Status == ApplicationAuditCompletionOutboxStatuses.Delivered)?.CorrelationId
                is { } deliveredCorrelationId)
            {
                GovernanceEvidenceTimelineResponse? timeline = await services.GetRequiredService<IGovernanceEvidenceQuery>()
                    .FindAsync(deliveredCorrelationId, cancellationToken);
                string[] kinds = [.. timeline?.Entries.Select(static entry => entry.Kind) ?? []];
                foreach (string expected in new[] { "decision", "mutation-audit", "audit-completion", "lifecycle-event" })
                {
                    if (!kinds.Contains(expected, StringComparer.Ordinal))
                    {
                        problems.Add($"The delivered request's timeline has lost its {expected} evidence.");
                    }
                }
            }

            // A decision authorized by a capability token still has the token's use record.
            HashSet<string> usedTokenIds = [.. await dbContext.Set<CapabilityGrantUseEntity>().AsNoTracking()
                .Select(static use => use.TokenId).ToListAsync(cancellationToken)];
            string[] tokenIds = [.. ledger.Select(static record => record.CapabilityTokenId).OfType<string>()];
            if (tokenIds.Length == 0)
            {
                problems.Add("No decision in the upgraded ledger carries a capability token.");
            }

            foreach (string tokenId in tokenIds.Where(tokenId => !usedTokenIds.Contains(tokenId)))
            {
                problems.Add($"Capability token {tokenId} has lost its use record.");
            }

            // Reconciliation still reads the findings and finds nothing new: every committed batch still matches its
            // manifest and its completion, and the one deliberate missing completion is still the only finding.
            IApplicationAuditReconciler reconciler = services.GetRequiredService<IApplicationAuditReconciler>();
            IReadOnlyList<ApplicationAuditReconciliationFindingItem> findingsBefore = await reconciler
                .QueryFindingsAsync(new ApplicationAuditReconciliationQuery(), cancellationToken);
            if (findingsBefore.Count == 0)
            {
                problems.Add("The upgraded database has lost its reconciliation finding.");
            }

            _ = await reconciler.ReconcileAsync(cancellationToken);
            foreach (ApplicationAuditReconciliationFindingItem finding in await reconciler
                .QueryFindingsAsync(new ApplicationAuditReconciliationQuery(), cancellationToken))
            {
                if (finding.ReasonCode != ApplicationAuditReconciliationReasonCodes.MissingCompletion)
                {
                    problems.Add($"Reconciliation now reports {finding.ReasonCode} for batch {finding.MutationBatchId}.");
                }
            }
        }
        catch (Exception exception) when (exception is DbUpdateException or SqliteException or InvalidOperationException)
        {
            problems.Add($"The upgraded application could not read its evidence: {exception.GetType().Name}: {exception.Message}");
        }

        return problems;
    }

    /// <summary>
    /// Reads the normalized schema of a database, for comparing an upgraded database with a clean install.
    /// </summary>
    /// <param name="databasePath">The database file.</param>
    /// <returns>Each table, index, and trigger definition, normalized and sorted.</returns>
    public static List<string> ReadSchema(string databasePath)
    {
        using SqliteConnection connection = Open(databasePath);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> schema = [];
        while (reader.Read())
        {
            // A table rebuilt by a migration may quote its name differently from one created directly.
            string sql = string.Join(' ', reader.GetString(2).Replace("\"", string.Empty, StringComparison.Ordinal)
                .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
            schema.Add($"{reader.GetString(0)} {reader.GetString(1)}: {sql}");
        }

        return [.. schema.Order(StringComparer.Ordinal)];
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static List<string> ReadTableNames(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> $history ORDER BY name";
        _ = command.Parameters.AddWithValue("$history", _migrationHistoryTable);
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> tables = [];
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static string[] ReadPrimaryKey(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT name, pk FROM pragma_table_info('{table}') WHERE pk > 0 ORDER BY pk";
        using SqliteDataReader reader = command.ExecuteReader();
        List<string> columns = [];
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return [.. columns];
    }

    private static string ToLiteral(object? value)
    {
        return value switch
        {
            null or DBNull => "NULL",
            long integer => integer.ToString(CultureInfo.InvariantCulture),
            double real => real.ToString("R", CultureInfo.InvariantCulture),
            byte[] blob => $"X'{Convert.ToHexString(blob)}'",
            string text => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'",
            _ => throw new InvalidOperationException($"Unexpected SQLite value type {value.GetType().Name}.")
        };
    }

    private static async Task<string> PostAsync(
        HttpClient client,
        string path,
        string user,
        string? role,
        string? permission,
        Dictionary<string, string> fields,
        HttpStatusCode expectedStatus,
        CancellationToken cancellationToken,
        (string Name, string Value)? header = null)
    {
        using HttpRequestMessage tokenRequest = CreateRequest(HttpMethod.Get, "/test/authentication/antiforgery-token", user, role, permission);
        using HttpResponseMessage tokenResponse = await client.SendAsync(tokenRequest, cancellationToken);
        _ = tokenResponse.EnsureSuccessStatusCode();
        fields[_antiforgeryField] = await tokenResponse.Content.ReadAsStringAsync(cancellationToken);

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path, user, role, permission);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new FormUrlEncodedContent(fields);
        if (header is { } extra)
        {
            request.Headers.Add(extra.Name, extra.Value);
        }

        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        Assert.True(response.StatusCode == expectedStatus, $"POST {path} returned {(int)response.StatusCode}: {body}");
        return body;
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string user, string? role, string? permission)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Add(_userHeader, user);
        if (role is not null)
        {
            request.Headers.Add(_roleHeader, role);
        }

        if (permission is not null)
        {
            request.Headers.Add(_permissionHeader, permission);
        }

        return request;
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(_userHeader, out StringValues user) || string.IsNullOrWhiteSpace(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            List<Claim> claims = [new Claim("sub", user.ToString())];
            AddClaim(claims, _roleHeader, ApplicationClaimTypes.Role);
            AddClaim(claims, _permissionHeader, ApplicationClaimTypes.Permission);

            ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme.Name));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }

        private void AddClaim(List<Claim> claims, string headerName, string claimType)
        {
            if (Request.Headers.TryGetValue(headerName, out StringValues value) && !string.IsNullOrWhiteSpace(value))
            {
                claims.Add(new Claim(claimType, value.ToString()));
            }
        }
    }
}
