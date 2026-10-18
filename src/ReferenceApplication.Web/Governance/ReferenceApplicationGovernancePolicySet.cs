using System.Security.Cryptography;
using System.Text;

namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Defines the stable identity and shared vocabulary for the Reference Application's sample governance policy set.
/// </summary>
public static class ReferenceApplicationGovernancePolicySet
{
    private const string _canonicalDefinition =
        "referenceapplication-governance-policy-set\n" +
        "version=1.0.0\n" +
        "administrator-action=authenticated actor has a configured administrator role\n" +
        "consequential-action=explicit acknowledgment required\n" +
        "capability-protected-action=endpoint declares scope referenceapplication.consequential.execute";

    /// <summary>
    /// Gets the policy version written to governance decisions.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// Gets the capability scope that a capability-protected endpoint must declare.
    /// </summary>
    public const string ConsequentialExecutionScope = "referenceapplication.consequential.execute";

    /// <summary>
    /// Gets the stable hash of the canonical policy definition written to governance decisions.
    /// </summary>
    public static string PolicyHash { get; } = CreatePolicyHash();

    private static string CreatePolicyHash()
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(_canonicalDefinition));
        return $"sha256:{Convert.ToHexStringLower(digest)}";
    }
}

/// <summary>
/// Marks an operation as requiring an authenticated actor in one of the configured administrator roles.
/// </summary>
public sealed class AdministratorActionPolicy;

/// <summary>
/// Marks an otherwise-allowed operation as requiring explicit acknowledgment before execution.
/// </summary>
public sealed class ConsequentialActionPolicy;

/// <summary>
/// Marks an operation whose endpoint must declare the Reference Application's consequential-execution capability.
/// </summary>
public sealed class CapabilityProtectedActionPolicy;
