namespace ReferenceApplication.Web.Tests.Infrastructure;

/// <summary>
/// Locates checked-in repository files for tests that guard configuration contracts, such as the Dockerfile, Compose
/// file, CI workflows, rulesets, and application settings.
/// </summary>
internal static class RepositoryFiles
{
    private const string _solutionFileName = "ReferenceApplication.slnx";

    /// <summary>
    /// Gets the repository root, found by walking up from the test output directory to the solution file.
    /// </summary>
    /// <returns>The absolute repository root path.</returns>
    public static string GetRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, _solutionFileName)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException($"The repository root containing '{_solutionFileName}' was not found.");
    }

    /// <summary>
    /// Gets the absolute path of a repository file or directory.
    /// </summary>
    /// <param name="relativePath">The path relative to the repository root, using forward slashes.</param>
    /// <returns>The absolute path.</returns>
    public static string GetPath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        return Path.Combine([GetRoot(), .. relativePath.Split('/')]);
    }

    /// <summary>
    /// Reads a repository text file with line endings normalized to <c>\n</c>.
    /// </summary>
    /// <param name="relativePath">The path relative to the repository root, using forward slashes.</param>
    /// <returns>The file text.</returns>
    public static string ReadAllText(string relativePath)
    {
        return File.ReadAllText(GetPath(relativePath)).ReplaceLineEndings("\n");
    }
}
