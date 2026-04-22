using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;

namespace UevrMcp;

static class Http
{
    static string? _base;
    // Bulk reflection dumps on AAA games can take multiple minutes on the plugin
    // game-thread. Default HttpClient timeout is 100s — raise it so large-game
    // dumps don't get killed client-side. Plugin itself caps at 120s per request.
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Resolve the HTTP base URL. Priority:
    ///   1. UEVR_MCP_API_URL environment variable (explicit override)
    ///   2. Port discovered from the named pipe (get_http_port command)
    ///   3. Fallback to http://localhost:8899
    /// </summary>
    static string Base
    {
        get
        {
            if (_base != null) return _base;

            var envUrl = Environment.GetEnvironmentVariable("UEVR_MCP_API_URL");
            if (!string.IsNullOrEmpty(envUrl))
            {
                _base = envUrl;
                return _base;
            }

            // Try to discover the actual port from the plugin pipe
            var pipeResponse = Pipe.Request("get_http_port").GetAwaiter().GetResult();
            if (pipeResponse != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(pipeResponse);
                    if (doc.RootElement.TryGetProperty("http_port", out var portEl))
                    {
                        var port = portEl.GetInt32();
                        _base = $"http://localhost:{port}";
                        return _base;
                    }
                }
                catch { /* fall through to default */ }
            }

            _base = "http://localhost:8899";
            return _base;
        }
    }

    /// <summary>
    /// Force re-discovery of the HTTP port on the next request.
    /// Called when an HTTP transport failure suggests the port may have changed.
    /// </summary>
    internal static void InvalidateBase() => _base = null;

    static string ErrorJson(string method, string url, string error, string? detail = null, int? status = null, string? body = null)
        => JsonSerializer.Serialize(new {
            error,
            method,
            url,
            detail,
            status,
            body
        }, JsonOptions);

    static async Task<string> Send(HttpRequestMessage request)
    {
        try
        {
            using var res = await Client.SendAsync(request);
            var body = await res.Content.ReadAsStringAsync();
            if (res.IsSuccessStatusCode)
            {
                return body;
            }

            return ErrorJson(
                request.Method.Method,
                request.RequestUri?.ToString() ?? request.Method.Method,
                "HTTP request failed",
                $"{(int)res.StatusCode} {res.ReasonPhrase}",
                (int)res.StatusCode,
                body);
        }
        catch (HttpRequestException ex)
        {
            // Connection refused / transport failure may mean the port changed —
            // invalidate so next request re-discovers from the pipe.
            InvalidateBase();
            return ErrorJson(
                request.Method.Method,
                request.RequestUri?.ToString() ?? request.Method.Method,
                "HTTP transport failure",
                ex.Message,
                ex.StatusCode is HttpStatusCode status ? (int)status : null);
        }
        catch (TaskCanceledException ex)
        {
            return ErrorJson(
                request.Method.Method,
                request.RequestUri?.ToString() ?? request.Method.Method,
                "HTTP request timed out",
                ex.Message);
        }
    }

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
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await Send(request);
    }

    public static async Task<string> Post(string path, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, Base + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return await Send(request);
    }

    public static async Task<string> Delete(string path, object body)
    {
        var json = JsonSerializer.Serialize(body, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Delete, Base + path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        return await Send(request);
    }
}
