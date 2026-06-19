using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DCAAIExtension
{
    /// <summary>
    /// Single interface every backend implements. Streaming is the primary path;
    /// non-streaming callers can just concatenate the stream.
    /// </summary>
    public interface IAiBackend : IDisposable
    {
        string Name { get; }
        IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct = default);
    }

    /// <summary>Factory: builds the right backend(s) from config.</summary>
    public static class AiBackendFactory
    {
        public static IAiBackend Create(DcaMode mode, AIConfiguration cfg, WebSocketClass sharedWebSocket = null)
        {
            return mode switch
            {
                DcaMode.WebSocket => new WebSocketBackend(cfg, sharedWebSocket),
                DcaMode.OpenAiLlamaCpp => new OpenAiBackend(cfg),
                DcaMode.OpenWebUI => new OpenWebUiBackend(cfg),
                DcaMode.GenericRest => new GenericRestBackend(cfg),
                _ => throw new NotSupportedException($"No backend for {mode}")
            };
        }
    }

    // ---- WebSocket adapter: wraps your existing WebSocketClass, no rewrite ----
    internal sealed class WebSocketBackend : IAiBackend
    {
        private readonly WebSocketClass _ws;
        private readonly bool _owned;
        public string Name => "WebSocket";

        public WebSocketBackend(AIConfiguration cfg, WebSocketClass shared)
        {
            _ws = shared ?? new WebSocketClass();
            _owned = shared == null;
            if (_owned) Task.Run(() => _ws.ConnectWebSocket());
        }

        public async IAsyncEnumerable<string> StreamAsync(string prompt,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            // WebSocket path is fire-and-forget with an event callback in your design.
            // We bridge the next DataReceived event into a one-shot async result.
            var tcs = new TaskCompletionSource<string>();
            void Handler(string d) { tcs.TrySetResult(d); }
            _ws.DataReceived += Handler;
            try
            {
                await _ws.AddMessageToWebSocketQueue(prompt);
                using (ct.Register(() => tcs.TrySetCanceled()))
                    yield return await tcs.Task;
            }
            finally { _ws.DataReceived -= Handler; }
        }

        public void Dispose() { /* shared socket is owned elsewhere */ }
    }

    // ---- OpenAI / LlamaCPP adapter: wraps your existing OpenAIClientAPI ----
    internal sealed class OpenAiBackend : IAiBackend
    {
        private readonly OpenAIClientAPI _client;
        private readonly AIConfiguration _cfg;
        public string Name => "OpenAI/LlamaCPP";

        public OpenAiBackend(AIConfiguration cfg)
        {
            _cfg = cfg;
            _client = new OpenAIClientAPI(cfg.BaseHttpUrl, cfg.GetSecret(),
                logger: m => System.Diagnostics.Debug.WriteLine(m));
        }

        public IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct = default)
        {
            var req = new OpenAIClientAPI.ChatCompletionRequest
            {
                Model = _cfg.Model,
                Temperature = _cfg.Temperature,
                Messages = new[]
                {
                    new OpenAIClientAPI.ChatMessage { Role = "system", Content = "You are an expert C# and VB.NET coding assistant." },
                    new OpenAIClientAPI.ChatMessage { Role = "user", Content = prompt }
                }
            };
            return _client.StreamChatCompletionAsync(req, ct);
        }

        public void Dispose() => _client.Dispose();
    }

    // ---- Open-WebUI adapter (OpenAI-compatible chat endpoint, session/bearer auth) ----
    internal sealed class OpenWebUiBackend : IAiBackend
    {
        private readonly HttpClient _http;
        private readonly AIConfiguration _cfg;
        public string Name => "Open-WebUI";

        public OpenWebUiBackend(AIConfiguration cfg)
        {
            _cfg = cfg;
            _http = HttpBackendShared.Build(cfg);
        }

        public IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct = default) =>
            HttpBackendShared.StreamOpenAiCompatible(_http, _cfg.OpenWebUiChatPath, _cfg, prompt, ct);

        public void Dispose() => _http.Dispose();
    }

    // ---- Generic REST adapter (configurable path, OpenAI-compatible body) ----
    internal sealed class GenericRestBackend : IAiBackend
    {
        private readonly HttpClient _http;
        private readonly AIConfiguration _cfg;
        public string Name => "Generic REST";

        public GenericRestBackend(AIConfiguration cfg)
        {
            _cfg = cfg;
            _http = HttpBackendShared.Build(cfg);
        }

        public IAsyncEnumerable<string> StreamAsync(string prompt, CancellationToken ct = default) =>
            HttpBackendShared.StreamOpenAiCompatible(_http, _cfg.GenericRestPath, _cfg, prompt, ct);

        public void Dispose() => _http.Dispose();
    }

    /// <summary>Shared HTTP plumbing: auth wiring + OpenAI-style SSE parsing.</summary>
    internal static class HttpBackendShared
    {
        public static HttpClient Build(AIConfiguration cfg)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                UseCookies = cfg.Auth == AuthKind.Session,
            };
            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri(cfg.BaseHttpUrl),
                Timeout = TimeSpan.FromSeconds(120),
            };
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var secret = cfg.GetSecret();
            switch (cfg.Auth)
            {
                case AuthKind.Bearer:
                case AuthKind.OAuth:
                    if (!string.IsNullOrEmpty(secret))
                        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                    break;
                case AuthKind.ApiKeyHeader:
                    if (!string.IsNullOrEmpty(secret))
                        http.DefaultRequestHeaders.Add("X-API-Key", secret);
                    break;
                case AuthKind.Session:
                    if (!string.IsNullOrEmpty(secret))
                        http.DefaultRequestHeaders.Add("Cookie", $"{cfg.SessionCookieName}={secret}");
                    break;
            }
            return http;
        }

        public static async IAsyncEnumerable<string> StreamOpenAiCompatible(
            HttpClient http, string path, AIConfiguration cfg, string prompt,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var body = JsonSerializer.Serialize(new
            {
                model = cfg.Model,
                temperature = cfg.Temperature,
                stream = true,
                messages = new[]
                {
                    new { role = "system", content = "You are an expert C# and VB.NET coding assistant." },
                    new { role = "user", content = prompt }
                }
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new System.IO.StreamReader(stream);
            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.StartsWith("data: [DONE]")) break;
                if (!line.StartsWith("data: ")) continue;

                string delta = null;
                try
                {
                    using var doc = JsonDocument.Parse(line.Substring(6));
                    var choices = doc.RootElement.GetProperty("choices");
                    if (choices.GetArrayLength() > 0 &&
                        choices[0].TryGetProperty("delta", out var d) &&
                        d.TryGetProperty("content", out var c))
                        delta = c.GetString();
                }
                catch { /* skip malformed chunk */ }

                if (!string.IsNullOrEmpty(delta)) yield return delta;
            }
        }
    }
}
