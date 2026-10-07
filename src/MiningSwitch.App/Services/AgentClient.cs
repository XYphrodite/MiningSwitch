using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace MiningSwitch.App.Services;

/// <summary>
/// Discovery of the local mining-fleet-agent: its config file names the loopback port and
/// the shared-secret token. The token is used in memory for the local API only and is never
/// sent to any pool. Client shape adapted from mining-fleet's AgentClient.
/// </summary>
public sealed record AgentEndpoint(int Port, string Token)
{
    public static AgentEndpoint Discover()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "mining-fleet-agent", "appsettings.json");
        if (!File.Exists(path))
            throw new InvalidOperationException("mining-fleet-agent не установлен на этом ПК.");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var listen = doc.RootElement.GetProperty("Agent").GetProperty("ListenUrl").GetString() ?? "";
        if (!Uri.TryCreate(listen, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535)
            throw new InvalidOperationException("Некорректный адрес локального агента.");
        var token = doc.RootElement.GetProperty("Agent").GetProperty("Token").GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("В настройках агента отсутствует ключ.");
        return new AgentEndpoint(uri.Port, token);
    }
}

/// <summary>Talks to the local agent over loopback.</summary>
public sealed class AgentClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public AgentClient(AgentEndpoint endpoint)
    {
        // A local VPN/proxy client must not sit in the middle of loopback traffic.
        _http = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{endpoint.Port}/api/v1/"),
            Timeout = TimeSpan.FromSeconds(8),
        };
        _http.DefaultRequestHeaders.Add("X-Fleet-Token", endpoint.Token);
    }

    public Task<MinerStatusDto?> GetMinerAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<MinerStatusDto>("miner", JsonOptions, ct);

    /// <summary>Null on an agent that predates GPU mining; the caller reports that, not an error.</summary>
    public async Task<GpuMinerStatusDto?> GetGpuAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("gpu", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GpuMinerStatusDto>(JsonOptions, ct);
    }

    public Task<MinerConfigDto?> GetConfigAsync(CancellationToken ct) =>
        _http.GetFromJsonAsync<MinerConfigDto>("config", JsonOptions, ct);

    public async Task<MinerConfigDto?> PutConfigAsync(MinerConfigDto patch, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync("config", patch, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<MinerConfigDto>(JsonOptions, ct);
    }

    public Task<CommandResultDto?> StartAsync(CancellationToken ct) => PostAsync("miner/start", ct);
    public Task<CommandResultDto?> StopAsync(CancellationToken ct) => PostAsync("miner/stop", ct);
    public Task<CommandResultDto?> GpuStartAsync(CancellationToken ct) => PostAsync("gpu/start", ct);
    public Task<CommandResultDto?> GpuStopAsync(CancellationToken ct) => PostAsync("gpu/stop", ct);

    private async Task<CommandResultDto?> PostAsync(string path, CancellationToken ct)
    {
        using var response = await _http.PostAsync(path, content: null, ct);
        return await response.Content.ReadFromJsonAsync<CommandResultDto>(JsonOptions, ct);
    }

    public void Dispose() => _http.Dispose();
}
