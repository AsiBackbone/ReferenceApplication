namespace ReferenceApplication.Web.Governance;

/// <summary>
/// Marks code whose local-development signing wiring was reviewed for production, which AsiBackbone analyzer
/// <c>ASIB003</c> recognizes by name.
/// </summary>
/// <remarks>
/// The marker records a review; it does not make the wiring safe. Every type carrying it must be reachable only after a
/// runtime environment guard. Here that guard is in
/// <see cref="Extensions.CapabilityTokenServiceExtensions.AddApplicationCapabilityTokens" />, which refuses capability
/// tokens in the Production environment unless <see cref="CapabilityTokenOptions.AllowSampleSigningKeyInProduction" />
/// is set.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
internal sealed class AsiBackboneProductionConfigurationReviewedAttribute : Attribute;
