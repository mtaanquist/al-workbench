using ALDevToolbox.Domain.ValueObjects;
using ALDevToolbox.Services.Translation.Providers;

namespace ALDevToolbox.Services.Translation;

/// <summary>
/// Orchestrates per-tenant machine translation for the Translator page and the
/// MCP <c>machine_translate</c> tool. Resolves the org's decrypted settings via
/// <see cref="MachineTranslationSettingsService"/>, builds the configured provider, and
/// returns a translation — degrading to <see langword="null"/> (memory-only) when
/// the feature is off, unconfigured, or the provider call fails, so a missing or
/// invalid key never surfaces as an error to the translator. See
/// <c>.design/translator/</c> for the feature design.
/// </summary>
public sealed class MachineTranslationService
{
    private readonly MachineTranslationSettingsService _config;
    private readonly IMachineTranslationProviderFactory _factory;
    private readonly ILogger<MachineTranslationService> _logger;

    public MachineTranslationService(
        MachineTranslationSettingsService config,
        IMachineTranslationProviderFactory factory,
        ILogger<MachineTranslationService> logger)
    {
        _config = config;
        _factory = factory;
        _logger = logger;
    }

    /// <summary>
    /// The org's effective trigger, or <see cref="MtTrigger.Off"/> when the
    /// feature is disabled or unusable (no key / undecryptable). The Translator
    /// uses this to decide whether and when to call out, and to show or hide the
    /// on-demand action.
    /// </summary>
    public async Task<MtTrigger> GetTriggerAsync(CancellationToken ct = default)
    {
        var settings = await _config.ResolveMachineTranslationAsync(ct);
        return settings?.Trigger ?? MtTrigger.Off;
    }

    /// <summary>
    /// Translates one string, or returns <see langword="null"/> when machine
    /// translation isn't configured for the org or the provider call fails.
    /// <paramref name="context"/> is optional hint text (developer note / kind).
    /// </summary>
    public async Task<MtTranslation?> TranslateAsync(
        string sourceText, string? sourceLanguage, string targetLanguage,
        string? context, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return null;
        var settings = await _config.ResolveMachineTranslationAsync(ct);
        if (settings is null) return null;

        var provider = _factory.Create(settings);
        try
        {
            var result = await provider.TranslateAsync(sourceText, sourceLanguage, targetLanguage, context, ct);
            _logger.LogInformation(
                "Machine translation via {Provider}: {SourceLang}→{TargetLang}.",
                settings.Provider, sourceLanguage ?? "auto", targetLanguage);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Machine translation via {Provider} failed; degrading to memory-only.", settings.Provider);
            return null;
        }
    }

    /// <summary>
    /// Reachability + auth check for the admin "Test connection" button. Returns a
    /// failure result (rather than throwing) when the feature isn't configured.
    /// </summary>
    public async Task<MtTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        var settings = await _config.ResolveMachineTranslationAsync(ct);
        if (settings is null)
        {
            return new MtTestResult(false,
                "Machine translation isn't configured. Pick a mode other than Off, enter an API key, and save first.");
        }

        var provider = _factory.Create(settings);
        try
        {
            return await provider.TestConnectionAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Machine translation test connection via {Provider} failed.", settings.Provider);
            return new MtTestResult(false, ex.Message);
        }
    }
}
