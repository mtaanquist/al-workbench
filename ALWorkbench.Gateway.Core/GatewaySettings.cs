using System.Text.Json;
using System.Text.Json.Serialization;

namespace ALWorkbench.Gateway.Core;

/// <summary>
/// What the gateway remembers about its registration. None of it is secret: the
/// credential is the private key in <see cref="ISigningKeyStore"/>, and this only
/// names it. Written by <c>register</c> and <c>install</c> (as an administrator),
/// read by the service.
/// </summary>
public sealed record GatewaySettings
{
    /// <summary>The AL Workbench address given to <c>register</c>, normalised by <see cref="WorkbenchAddress"/>.</summary>
    [JsonPropertyName("workbenchUrl")]
    public string WorkbenchUrl { get; init; } = string.Empty;

    /// <summary>The id AL Workbench returned at registration; the <c>iss</c> of every request token.</summary>
    [JsonPropertyName("gatewayId")]
    public int GatewayId { get; init; }

    /// <summary>The name the gateway is listed under on the Gateways page.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>The name of the key in the key store.</summary>
    [JsonPropertyName("keyName")]
    public string KeyName { get; init; } = string.Empty;

    /// <summary>The key's fingerprint, as printed at registration and shown on the Gateways page.</summary>
    [JsonPropertyName("keyFingerprint")]
    public string KeyFingerprint { get; init; } = string.Empty;

    [JsonPropertyName("registeredAt")]
    public DateTimeOffset RegisteredAt { get; init; }

    /// <summary>
    /// The Windows account the service runs under, set by <c>install</c>. Registering
    /// after installing grants this account the new key.
    /// </summary>
    [JsonPropertyName("serviceAccount")]
    public string? ServiceAccount { get; init; }
}

/// <summary>Reads and writes <see cref="GatewaySettings"/>.</summary>
public interface IGatewaySettingsStore
{
    /// <summary>The settings, or null when the gateway has not been registered or installed.</summary>
    GatewaySettings? Load();

    void Save(GatewaySettings settings);

    /// <summary>Removes the stored settings. Nothing happens when there are none.</summary>
    void Delete();
}

/// <summary>
/// <see cref="IGatewaySettingsStore"/> as a JSON file. Written whole through a
/// temporary file and a rename, so a crash mid-write never leaves half a file.
/// </summary>
public sealed class JsonFileGatewaySettingsStore : IGatewaySettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public JsonFileGatewaySettingsStore(string path)
    {
        _path = path;
    }

    public string Path => _path;

    public GatewaySettings? Load()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            return JsonSerializer.Deserialize<GatewaySettings>(File.ReadAllBytes(_path), Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"The gateway's settings file {_path} is damaged. Register the gateway again with a new code.", ex);
        }
    }

    public void Save(GatewaySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(settings, Options));
    }

    public void Delete()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}

/// <summary>Whole-file writes that survive a crash or a power cut part way through.</summary>
internal static class AtomicFile
{
    public static void Write(string path, byte[] content)
    {
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(content);
            // On disk before the rename, so the rename never exposes an empty file.
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
