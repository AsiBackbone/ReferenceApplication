using System.Text.Json;

namespace ReferenceApplication.Web.Tests;

/// <summary>
/// Verifies that every environment writes its rolling log file under the application's own name.
/// </summary>
/// <remarks>
/// NCAT 2.11.5 ships <c>appsettings.Development.json</c> with a <c>Logs/template-web-.log</c> path while the base
/// settings use <c>Logs/application-web-.log</c>. This guard keeps a future baseline refresh from reintroducing the
/// template name.
/// </remarks>
public sealed class SerilogFileSinkConfigurationTests
{
    private const string _expectedLogPath = "Logs/application-web-.log";

    /// <summary>
    /// Verifies the file sink path in the base and Development settings.
    /// </summary>
    /// <param name="settingsFileName">The settings file under the web project.</param>
    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void FileSink_EverySettingsFile_UsesApplicationLogName(string settingsFileName)
    {
        IReadOnlyList<string> paths = ReadFileSinkPaths(settingsFileName);

        string path = Assert.Single(paths);
        Assert.Equal(_expectedLogPath, path);
    }

    /// <summary>
    /// Verifies that no settings file in the web project, including ones added later, names a file sink after the
    /// template.
    /// </summary>
    [Fact]
    public void FileSink_AllSettingsFiles_NeverUseTemplateName()
    {
        string[] settingsFiles = Directory.GetFiles(GetWebProjectDirectory(), "appsettings*.json");

        Assert.NotEmpty(settingsFiles);
        Assert.All(settingsFiles, settingsFile => Assert.All(
            ReadFileSinkPaths(Path.GetFileName(settingsFile)),
            path => Assert.Equal(_expectedLogPath, path)));
    }

    private static List<string> ReadFileSinkPaths(string settingsFileName)
    {
        using var settings = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(GetWebProjectDirectory(), settingsFileName)),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        return settings.RootElement.TryGetProperty("Serilog", out JsonElement serilog) &&
            serilog.TryGetProperty("WriteTo", out JsonElement writeTo)
            ? [.. writeTo
                .EnumerateArray()
                .Where(sink => sink.TryGetProperty("Name", out JsonElement name) && name.GetString() == "File")
                .Select(sink => sink.GetProperty("Args").GetProperty("path").GetString() ?? string.Empty)]
            : [];
    }

    private static string GetWebProjectDirectory()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ReferenceApplication.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "ReferenceApplication.Web");
    }
}
