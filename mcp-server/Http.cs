using System.Text;
using System.Text.Json;

namespace UevrMcp;

static class Http
{
    static readonly string Base = Environment.GetEnvironmentVariable("UEVR_MCP_API_URL") ?? "http://localhost:8899";
    static readonly HttpClient Client = new();

    public static async Task<string> Get(string path, Dictionary<string, string?>? query = null)
    {
        var url = Base + path;
        if (query is { Count: > 0 })
        {
            var qs = string.Join("&", query
                .Where(kv => kv.Value is not null)
                .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value!)}"));
            if (qs.Length > 0) url += "?" + qs;
        }
        return await Client.GetStringAsync(url);
    }

    public static async Task<string> Post(string path, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        var res = await Client.PostAsync(Base + path, content);
        return await res.Content.ReadAsStringAsync();
    }

    public static async Task<string> Delete(string path, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Delete, Base + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        var res = await Client.SendAsync(request);
        return await res.Content.ReadAsStringAsync();
    }
}
