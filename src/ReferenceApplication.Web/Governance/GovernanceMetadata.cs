namespace ReferenceApplication.Web.Governance;

internal static class GovernanceMetadata
{
    private const string _policyTypesKey = "endpoint.policy_types";
    private const string _capabilityScopesKey = "endpoint.capability_scopes";

    internal static bool ContainsPolicy<TPolicy>(IReadOnlyDictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        string policyTypeName = typeof(TPolicy).FullName ?? typeof(TPolicy).Name;
        return ContainsCommaSeparatedValue(metadata, _policyTypesKey, policyTypeName);
    }

    internal static bool ContainsCapabilityScope(
        IReadOnlyDictionary<string, string> metadata,
        string requiredScope)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredScope);

        return ContainsCommaSeparatedValue(metadata, _capabilityScopesKey, requiredScope);
    }

    private static bool ContainsCommaSeparatedValue(
        IReadOnlyDictionary<string, string> metadata,
        string key,
        string expectedValue)
    {
        return metadata.TryGetValue(key, out string? values)
            && !string.IsNullOrWhiteSpace(values)
            && values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(expectedValue, StringComparer.Ordinal);
    }
}
