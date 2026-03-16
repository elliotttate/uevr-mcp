using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace UevrMcp;

static class Pipe
{
    static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
    static int _nextId = 1;
    static readonly SemaphoreSlim _gate = new(1, 1);

    public static bool IsAvailable { get; private set; }

    public static Task<string?> Request(string command) => Request(command, null, 5000);

    public static async Task<string?> Request(string command, Dictionary<string, object>? extra, int readTimeoutMs = 5000)
    {
        var id = Interlocked.Increment(ref _nextId);

        var requestDict = new Dictionary<string, object> { ["command"] = command, ["id"] = id };
        if (extra != null)
            foreach (var kv in extra)
                requestDict[kv.Key] = kv.Value;

        var request = JsonSerializer.Serialize(requestDict);

        await _gate.WaitAsync();
        try
        {
            return await ConnectSendReceive(request, readTimeoutMs);
        }
        finally
        {
            _gate.Release();
        }
    }

    static async Task<string?> ConnectSendReceive(string request, int readTimeoutMs)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(".", "UEVR_MCP", PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(2000);

                pipe.ReadMode = PipeTransmissionMode.Message;

                // Write request
                var requestBytes = Utf8NoBom.GetBytes(request);
                await pipe.WriteAsync(requestBytes);
                await pipe.FlushAsync();

                // Read response with timeout
                using var cts = new CancellationTokenSource(readTimeoutMs);
                var buffer = new byte[4096];
                var bytesRead = await pipe.ReadAsync(buffer, cts.Token);

                if (bytesRead == 0)
                {
                    IsAvailable = false;
                    return null;
                }

                IsAvailable = true;
                var response = Utf8NoBom.GetString(buffer, 0, bytesRead);

                // Extract result or error
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                if (root.TryGetProperty("error", out var err))
                    return JsonSerializer.Serialize(new { error = err.GetString() });

                return response;
            }
            catch
            {
                // First attempt failed — retry once
            }
            finally
            {
                try { pipe?.Dispose(); } catch { }
            }
        }

        IsAvailable = false;
        return null;
    }
}
