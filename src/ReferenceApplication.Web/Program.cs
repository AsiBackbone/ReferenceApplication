using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ReferenceApplication.Web.Authentication.Extensions;
using ReferenceApplication.Web.ErrorHandling;
using ReferenceApplication.Web.Extensions;
using Serilog;

Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Debug(
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [{SourceContext}] [CorrelationId: {CorrelationId}] [TraceId: {TraceId}] [SpanId: {SpanId}] [RequestId: {RequestId}] [RequestPath: {RequestPath}] {Message:lj}{NewLine}{Exception}",
        formatProvider: CultureInfo.InvariantCulture)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [{SourceContext}] [CorrelationId: {CorrelationId}] [TraceId: {TraceId}] [SpanId: {SpanId}] [RequestId: {RequestId}] [RequestPath: {RequestPath}] {Message:lj}{NewLine}{Exception}",
        formatProvider: CultureInfo.InvariantCulture)
    .CreateBootstrapLogger();

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    builder.UseApplicationDemoDatabase();
    builder.AddApplicationSerilog();
    Log.Information("Bootstrapping ReferenceApplication.Web application");
    // Validate antiforgery tokens on every unsafe (POST, PUT, PATCH, DELETE) MVC request by default, so consumer
    // actions are protected without opting in. Use [IgnoreAntiforgeryToken] only for endpoints that do not rely
    // on ambient (cookie) credentials.
    builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
    builder.Services.AddApplicationApiVersioning(builder.Configuration);
    builder.Services.AddRazorPages();
    builder.Services.AddApplicationHealthChecks();
    builder.Services.AddApplicationForwardedHeaders(builder.Configuration);
    builder.Services.AddApplicationSecurityHeaders(builder.Configuration);
    builder.Services.AddApplicationRateLimiting(builder.Configuration, builder.Environment);
    builder.Services.AddApplicationRequestLogging(builder.Configuration);
    builder.Services.AddApplicationOpenTelemetry(builder.Configuration, builder.Environment);
    builder.Services.AddApplicationProblemDetails(builder.Environment);
    builder.Services.AddApplicationDataProtection(builder.Configuration, builder.Environment);
    builder.Services.AddApplicationAuthentication(builder.Configuration, builder.Environment);
    builder.Services.AddApplicationDemoIdentity(builder.Configuration, builder.Environment);
    builder.Services.AddApplicationAuthorization(builder.Configuration);
    builder.Services.AddApplicationDataAccess(builder.Configuration);
    builder.Services.AddApplicationAsiBackbone();
    builder.Services.AddApplicationGovernanceEvidence();
    builder.Services.AddApplicationCapabilityTokens(builder.Configuration, builder.Environment);
    builder.Services.AddApplicationGovernanceLoadDemonstration(builder.Configuration, builder.Environment);
    builder.Services.AddApplicationPolicySimulator(builder.Configuration, builder.Environment);
    builder.Services.AddApplicationDecisionReplay(builder.Configuration, builder.Environment);
    builder.Services.AddApplicationPipelineInspector(builder.Configuration, builder.Environment);
    builder.Services.AddApplicationFailureScenarios(builder.Configuration, builder.Environment);

    Log.Information("Starting ReferenceApplication.Web application");
    WebApplication app = builder.Build();

    Log.Information("Configuring pipeline for ReferenceApplication.Web application");
    app.UseApplicationPipeline();
    app.MapApplicationHealthChecks();
    app.LogApplicationSecurityPosture();

    Log.Information("Running ReferenceApplication.Web application");
    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "ReferenceApplication.Web application terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
