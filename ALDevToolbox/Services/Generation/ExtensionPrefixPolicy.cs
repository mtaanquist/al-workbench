using ALDevToolbox.Domain.Entities;
using ALDevToolbox.Domain.ValueObjects;

namespace ALDevToolbox.Services.Generation;

/// <summary>
/// Turns an organisation's prefix policy plus what the consultant typed into
/// the one value <c>{{extension_prefix}}</c> renders to. The New Workspace
/// page, the generation endpoint and the <c>generate_workspace</c> MCP tool all
/// resolve through here, so an agent and a person get the same prefix from the
/// same organisation. Specified in <c>.design/customer-naming.md</c>.
/// </summary>
public static class ExtensionPrefixPolicy
{
    /// <summary>
    /// Whether the New Workspace form asks for a prefix at all. Only
    /// <see cref="ExtensionPrefixMode.PerWorkspace"/> does - the other two modes
    /// already know the answer, so asking would be a question with one correct
    /// reply.
    /// </summary>
    public static bool IsAskedFor(ExtensionPrefixMode mode) => mode == ExtensionPrefixMode.PerWorkspace;

    /// <summary>
    /// The prefix a workspace for <paramref name="customerName"/> gets.
    /// </summary>
    /// <param name="settings">The acting organisation's settings row.</param>
    /// <param name="typedPrefix">
    /// What the form (or the MCP caller) supplied. Ignored unless the mode is
    /// <see cref="ExtensionPrefixMode.PerWorkspace"/>, so a stale value posted
    /// by a hidden field or an agent guessing cannot override the organisation.
    /// </param>
    /// <param name="shortName">The customer's abbreviation, or null/blank.</param>
    /// <param name="customerName">The customer's name as typed.</param>
    /// <remarks>
    /// Every branch falls back to the short name (which itself falls back to the
    /// customer's name) rather than to nothing, because the stock name templates
    /// read <c>"{{extension_prefix}} Core"</c> and an empty prefix leaves an
    /// extension called " Core".
    /// </remarks>
    public static string Resolve(
        OrganizationSettings settings, string? typedPrefix, string? shortName, string customerName)
    {
        var fallback = CustomerNaming.ShortNameOrFallback(shortName, customerName);
        var orgPrefix = settings.ExtensionPrefix?.Trim();
        return settings.ExtensionPrefixMode switch
        {
            ExtensionPrefixMode.Hidden => fallback,
            ExtensionPrefixMode.Fixed => string.IsNullOrWhiteSpace(orgPrefix) ? fallback : orgPrefix,
            _ => string.IsNullOrWhiteSpace(typedPrefix)
                ? (string.IsNullOrWhiteSpace(orgPrefix) ? fallback : orgPrefix)
                : typedPrefix.Trim(),
        };
    }

    /// <summary>
    /// The prefix a new extension joining an existing workspace gets: the one
    /// that workspace saved, so "Banking" lands as "CRO Banking" beside its
    /// "CRO Core" whatever the organisation's policy says today. A workspace
    /// saved before the prefix was recorded falls back to what
    /// <see cref="Resolve"/> gives it now.
    /// </summary>
    public static string ForExistingWorkspace(
        OrganizationSettings settings, string? savedPrefix, string? shortName, string customerName) =>
        string.IsNullOrWhiteSpace(savedPrefix)
            ? Resolve(settings, typedPrefix: null, shortName, customerName)
            : savedPrefix.Trim();
}
