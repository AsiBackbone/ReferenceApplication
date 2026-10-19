namespace ReferenceApplication.Web.Authentication.Demo;

/// <summary>
/// Defines the explicitly enabled, Development-only demo identity control.
/// </summary>
public sealed class DemoIdentityOptions
{
    /// <summary>
    /// Gets the configuration section for the demo identity control.
    /// </summary>
    public const string SectionName = "ReferenceApplication:DemoIdentity";

    /// <summary>
    /// Gets the path that creates a selected demo identity cookie.
    /// </summary>
    public const string SignInPath = "/Account/DemoSignIn";

    /// <summary>
    /// Gets the Development-only path that resets the disposable demonstration database.
    /// </summary>
    public const string ResetDatabasePath = "/governance-explorer/reset-database";

    /// <summary>
    /// Gets the connection-string name reserved for the disposable Development demonstration database.
    /// </summary>
    public const string DatabaseConnectionStringName = "DemonstrationDatabase";

    /// <summary>
    /// Gets or sets a value indicating whether the control is explicitly enabled.
    /// The control is still unavailable outside the Development environment.
    /// </summary>
    public bool Enabled { get; set; }
}
