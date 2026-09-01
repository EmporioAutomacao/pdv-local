using System.Net.Http.Json;

namespace SyncAgent.Tray.LocalApi;

public sealed class LocalStatusClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public LocalStatusClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AgentStatusResponse> GetStatusAsync()
    {
        return await _httpClient.GetFromJsonAsync<AgentStatusResponse>("/status")
            ?? throw new InvalidOperationException("Local status response is empty.");
    }

    public async Task<SyncNowResponse> SyncNowAsync()
    {
        using var response = await _httpClient.PostAsync("/sync-now", content: null);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SyncNowResponse>()
            ?? throw new InvalidOperationException("Sync-now response is empty.");
    }

    public async Task<SyncNowResponse> TriggerUpdateAsync()
    {
        using var response = await _httpClient.PostAsync("/update-now", content: null);
        var body = await response.Content.ReadFromJsonAsync<SyncNowResponse>()
            ?? throw new InvalidOperationException("Update-now response is empty.");

        if (response.StatusCode is not (System.Net.HttpStatusCode.Accepted or System.Net.HttpStatusCode.Conflict))
        {
            response.EnsureSuccessStatusCode();
        }

        return body;
    }

    public async Task<UpdateStatusResponse> GetUpdateStatusAsync()
    {
        return await _httpClient.GetFromJsonAsync<UpdateStatusResponse>("/update-status")
            ?? throw new InvalidOperationException("Update-status response is empty.");
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
