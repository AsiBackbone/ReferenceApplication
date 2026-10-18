using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Hosting;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Provides tests for the exception filter on the application entry point's startup <c>catch</c>, which keeps
/// design-time tooling from being reported as an application crash while real failures stay fatal.
/// </summary>
/// <remarks>
/// EF Core design-time commands such as <c>dotnet ef migrations list</c> run the application's entry point, listen to
/// the <c>Microsoft.Extensions.Hosting</c> diagnostic source, and throw <see cref="HostAbortedException"/> once the
/// host is built so they can use its services without running it. These tests reproduce that mechanism against the
/// real entry point.
/// </remarks>
public sealed class ProgramStartupLoggingTests
{
    private const string _hostingDiagnosticListenerName = "Microsoft.Extensions.Hosting";
    private const string _hostBuiltEventName = "HostBuilt";
    private const string _fatalStartupMessage = "terminated unexpectedly";

    /// <summary>
    /// Verifies that a host aborted by design-time tooling is not logged as a fatal crash and that the abort still
    /// propagates to the tool that requested it.
    /// </summary>
    [Fact]
    public void EntryPoint_HostAbortedByTooling_PropagatesWithoutFatalLog()
    {
        (Exception? exception, string output) = RunEntryPointThrowingOnHostBuilt(
            () => new HostAbortedException());

        Assert.IsType<HostAbortedException>(exception);
        Assert.DoesNotContain(_fatalStartupMessage, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Verifies that the filter excludes only tooling aborts: any other startup failure is still logged as fatal and
    /// rethrown.
    /// </summary>
    [Fact]
    public void EntryPoint_StartupFailure_IsLoggedAsFatalAndRethrown()
    {
        (Exception? exception, string output) = RunEntryPointThrowingOnHostBuilt(
            () => new InvalidOperationException("Simulated startup failure."));

        InvalidOperationException failure = Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("Simulated startup failure.", failure.Message);
        Assert.Contains(_fatalStartupMessage, output, StringComparison.Ordinal);
    }

    private static (Exception? Exception, string Output) RunEntryPointThrowingOnHostBuilt(
        Func<Exception> createException)
    {
        MethodInfo entryPoint = typeof(Program).Assembly.EntryPoint
            ?? throw new InvalidOperationException("ReferenceApplication.Web has no entry point.");
        string[] args =
        [
            "--contentRoot", FindWebProjectDirectory(),
            "--environment", "Testing"
        ];

        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        using StringWriter output = new();
        Console.SetOut(output);
        Console.SetError(output);

        try
        {
            using HostBuiltInterceptor interceptor = new(createException);
            entryPoint.Invoke(null, [args]);

            return (null, output.ToString());
        }
        catch (TargetInvocationException ex)
        {
            return (ex.InnerException, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static string FindWebProjectDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "src", "ReferenceApplication.Web");
            if (File.Exists(Path.Combine(candidate, "ReferenceApplication.Web.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("Could not locate src/ReferenceApplication.Web above the test output.");
    }

    /// <summary>
    /// Throws a supplied exception when the hosting diagnostic source reports that a host has been built, which is
    /// how design-time tooling stops the application before it runs.
    /// </summary>
    private sealed class HostBuiltInterceptor
        : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly Func<Exception> _createException;
        private readonly IDisposable _allListenersSubscription;
        private readonly List<IDisposable> _listenerSubscriptions = [];
        private bool _thrown;

        public HostBuiltInterceptor(Func<Exception> createException)
        {
            _createException = createException;
            _allListenersSubscription = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == _hostingDiagnosticListenerName)
            {
                _listenerSubscriptions.Add(value.Subscribe(this));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (!_thrown && value.Key == _hostBuiltEventName)
            {
                _thrown = true;
                throw _createException();
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            foreach (IDisposable subscription in _listenerSubscriptions)
            {
                subscription.Dispose();
            }

            _allListenersSubscription.Dispose();
        }
    }
}
