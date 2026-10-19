using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;

namespace ReferenceApplication.Benchmarks;

internal static class Program
{
    public static int Main(string[] args)
    {
        IEnumerable<Summary> summaries = BenchmarkSwitcher
            .FromAssembly(typeof(GovernancePolicyBenchmarks).Assembly)
            .Run(args);

        return summaries.Any(summary =>
            summary.HasCriticalValidationErrors ||
            summary.Reports.Any(report => !report.Success))
            ? 1
            : 0;
    }
}
