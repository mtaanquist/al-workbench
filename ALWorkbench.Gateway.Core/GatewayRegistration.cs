using System.Net.Http.Headers;
using System.Text.Json;
using ALWorkbench.Gateway.Contracts;
using Microsoft.Extensions.Logging;

namespace ALWorkbench.Gateway.Core;

/// <summary>A registration that did not go through, with a message for the person at the console.</summary>
public sealed class GatewayRegistrationException : Exception
{
    public GatewayRegistrationException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// The <c>register</c> step: makes a new key pair in the key store, exchanges the
/// registration code and the public key with AL Workbench, checks that the
/// fingerprint AL Workbench stored is the one of the key it made, and records the
/// gateway id. A rotation code goes through the same steps; the old key is deleted
/// only once AL Workbench has accepted the new one, so a failed attempt leaves the
/// gateway as it was. See <c>.design/on-prem-gateway.md</c>, "Registration and
/// authentication".
/// </summary>
public sealed class GatewayRegistration
{
    /// <summary>Prefix of every key the gateway makes, so customer IT can recognise them in the key store.</summary>
    public const string KeyNamePrefix = "ALWorkbenchGateway-";

    private readonly HttpClient _http;
    private readonly ISigningKeyStore _keys;
    private readonly IGatewaySettingsStore _settings;
    private readonly IGatewayClock _clock;
    private readonly ILogger _logger;

    public GatewayRegistration(HttpClient http, ISigningKeyStore keys, IGatewaySettingsStore settings, IGatewayClock clock, ILogger logger)
    {
        _http = http;
        _keys = keys;
        _settings = settings;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Registers this machine. Returns the saved settings; throws
    /// <see cref="GatewayRegistrationException"/> with a message to print otherwise.
    /// </summary>
    public async Task<GatewaySettings> RegisterAsync(string? url, string? code, string machineName, string version, CancellationToken ct)
    {
        var workbenchUrl = WorkbenchAddress.TryNormalise(url, out var urlError)
            ?? throw new GatewayRegistrationException(urlError);
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new GatewayRegistrationException(
                "Give the registration code with --code. An Admin gets one in AL Workbench under Administration → Gateways → Add gateway.");
        }

        var existing = _settings.Load();
        var keyName = NewKeyName();
        byte[] publicKey;
        try
        {
            publicKey = _keys.Create(keyName, existing?.ServiceAccount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GatewayRegistrationException(
                $"Could not create the gateway's key in the Windows key store: {ex.Message} Run the command as an administrator.", ex);
        }

        var fingerprint = GatewayKeyFingerprint.Compute(publicKey);
        GatewayRegistrationResponse response;
        try
        {
            response = await ExchangeAsync(workbenchUrl, new GatewayRegistrationRequest
            {
                Code = code.Trim(),
                PublicKey = Convert.ToBase64String(publicKey),
                MachineName = machineName,
                Version = version,
                ProtocolVersion = GatewayProtocol.Version,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            _keys.Delete(keyName);
            throw;
        }

        if (!string.Equals(response.KeyFingerprint, fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            _keys.Delete(keyName);
            throw new GatewayRegistrationException(
                $"AL Workbench stored a different key ({response.KeyFingerprint}) from the one this computer made ({fingerprint}). " +
                "Something between this computer and AL Workbench changed the request. Do not approve gateway " +
                $"'{response.Name}'; ask an Admin to revoke it, and check the network path before trying again.");
        }

        var saved = (existing ?? new GatewaySettings()) with
        {
            WorkbenchUrl = workbenchUrl,
            GatewayId = response.GatewayId,
            Name = response.Name,
            KeyName = keyName,
            KeyFingerprint = fingerprint,
            RegisteredAt = _clock.UtcNow,
        };
        _settings.Save(saved);
        if (existing is { KeyName.Length: > 0 } && existing.KeyName != keyName)
        {
            _keys.Delete(existing.KeyName);
        }
        _logger.Registered(workbenchUrl, response.GatewayId, response.Name, fingerprint);
        return saved;
    }

    private string NewKeyName()
    {
        var name = KeyNamePrefix + _clock.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var candidate = name;
        for (var i = 2; _keys.Exists(candidate); i++) candidate = $"{name}-{i}";
        return candidate;
    }

    private async Task<GatewayRegistrationResponse> ExchangeAsync(string workbenchUrl, GatewayRegistrationRequest body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, WorkbenchAddress.RequestUriFor(workbenchUrl, GatewayProtocol.RegisterPath))
        {
            Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        try
        {
            using var response = await WorkbenchClient.SendAsync(_http, request, WorkbenchClient.RequestTimeout,
                HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            return await WorkbenchClient.ReadJsonAsync<GatewayRegistrationResponse>(response, ct).ConfigureAwait(false);
        }
        catch (WorkbenchUnreachableException ex)
        {
            throw new GatewayRegistrationException(
                $"{ex.Message} Check the address, and that this computer can open it in a browser (proxy and firewall).", ex);
        }
        catch (Exception ex) when (ex is WorkbenchRefusedException or GatewayCredentialRefusedException
                                   or GatewayNotApprovedException or CommandClosedException or GatewayOutdatedException)
        {
            throw new GatewayRegistrationException($"AL Workbench refused the registration: {ex.Message}", ex);
        }
    }
}
