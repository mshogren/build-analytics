using System.Net;
using System.Net.Sockets;
using System.Text;
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public TempDirectory() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class EnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = new(StringComparer.OrdinalIgnoreCase);

    public EnvironmentScope Set(string name, string? value)
    {
        if (!_previous.ContainsKey(name))
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
        }

        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var kvp in _previous)
        {
            Environment.SetEnvironmentVariable(kvp.Key, kvp.Value);
        }
    }
}

internal sealed class TestHttpServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Func<HttpListenerRequest, string>> _routes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Task _loop;

    public Uri BaseUri { get; }

    private TestHttpServer(int port)
    {
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseUri.ToString());
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public static TestHttpServer Start()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new TestHttpServer(port);
    }

    public void Register(string path, Func<HttpListenerRequest, string> handler)
    {
        _routes[path] = handler;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
            var body = _routes.TryGetValue(path, out var handler)
                ? handler(ctx.Request)
                : "{\"error\":\"not-found\"}";
            await WriteJsonAsync(ctx.Response, body, _routes.ContainsKey(path) ? 200 : 404);
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, string body, int statusCode)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        response.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
        }
    }
}
