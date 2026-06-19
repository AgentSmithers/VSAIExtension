using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DCAAIExtension
{
    /// <summary>
    /// Minimal MCP server (JSON-RPC 2.0) over HttpListener.
    /// Supports Streamable HTTP (POST /mcp), SSE (GET /mcp -> event stream),
    /// and stdio (RunStdioAsync). Exposes code tools so external AI clients can
    /// list/read files and propose edits routed through the diff approval flow.
    ///
    /// The host wires three callbacks so this class stays UI/VS agnostic.
    /// </summary>
    public sealed class McpServer : IDisposable
    {
        public Func<string, IEnumerable<string>> ListFiles { get; set; }   // glob -> paths
        public Func<string, string> ReadFile { get; set; }                 // path -> content
        public Func<string, string, Task<bool>> ProposeEdit { get; set; }  // (path,newContent) -> accepted

        private readonly AIConfiguration _cfg;
        private readonly Action<string> _log;
        private HttpListener _listener;
        private CancellationTokenSource _cts;

        public McpServer(AIConfiguration cfg, Action<string> log = null)
        {
            _cfg = cfg;
            _log = log ?? (_ => { });
        }

        public void StartHttp()
        {
            if (!_cfg.McpTransports.HasFlag(McpTransport.StreamableHttp) &&
                !_cfg.McpTransports.HasFlag(McpTransport.Sse))
                return;

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_cfg.McpPort}/mcp/");
            _listener.Start();
            _log($"MCP server listening on :{_cfg.McpPort}/mcp/");
            _ = Task.Run(() => AcceptLoop(_cts.Token));
        }

        private async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch { break; }
                _ = Task.Run(() => HandleRequest(ctx, ct));
            }
        }

        private async Task HandleRequest(HttpListenerContext ctx, CancellationToken ct)
        {
            try
            {
                if (_cfg.McpRequireAuth && !IsAuthorized(ctx.Request))
                {
                    ctx.Response.StatusCode = 401;
                    ctx.Response.Close();
                    return;
                }

                // SSE: client opens a GET stream and we keep it alive.
                if (ctx.Request.HttpMethod == "GET" && _cfg.McpTransports.HasFlag(McpTransport.Sse))
                {
                    ctx.Response.ContentType = "text/event-stream";
                    ctx.Response.Headers.Add("Cache-Control", "no-cache");
                    var keep = ctx.Response.OutputStream;
                    await WriteSse(keep, "endpoint", "/mcp/");
                    while (!ct.IsCancellationRequested && ctx.Response.OutputStream.CanWrite)
                        await Task.Delay(15000, ct); // heartbeat handled by client pings
                    return;
                }

                // Streamable HTTP: POST a JSON-RPC message, get response (optionally as SSE).
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                var requestBody = await reader.ReadToEndAsync();
                var responseJson = await Dispatch(requestBody);

                var accept = ctx.Request.Headers["Accept"] ?? "";
                if (accept.Contains("text/event-stream"))
                {
                    ctx.Response.ContentType = "text/event-stream";
                    await WriteSse(ctx.Response.OutputStream, "message", responseJson);
                }
                else
                {
                    ctx.Response.ContentType = "application/json";
                    var bytes = Encoding.UTF8.GetBytes(responseJson);
                    await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, ct);
                }
                ctx.Response.Close();
            }
            catch (Exception ex) { _log("MCP request error: " + ex.Message); }
        }

        private bool IsAuthorized(HttpListenerRequest req)
        {
            var secret = _cfg.GetSecret();
            if (string.IsNullOrEmpty(secret)) return true;
            var auth = req.Headers["Authorization"];
            return auth == $"Bearer {secret}";
        }

        private static async Task WriteSse(Stream s, string evt, string data)
        {
            var payload = Encoding.UTF8.GetBytes($"event: {evt}\ndata: {data}\n\n");
            await s.WriteAsync(payload, 0, payload.Length);
            await s.FlushAsync();
        }

        // ---- stdio transport ----
        public async Task RunStdioAsync(CancellationToken ct = default)
        {
            using var stdin = Console.OpenStandardInput();
            using var reader = new StreamReader(stdin);
            string line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var resp = await Dispatch(line);
                Console.Out.WriteLine(resp);
                await Console.Out.FlushAsync();
            }
        }

        // ---- JSON-RPC dispatch ----
        private async Task<string> Dispatch(string body)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch { return Error(null, -32700, "Parse error"); }

            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.Clone() : (JsonElement?)null;
            var method = root.TryGetProperty("method", out var m) ? m.GetString() : "";

            switch (method)
            {
                case "initialize":
                    return Result(id, new
                    {
                        protocolVersion = "2025-06-18",
                        capabilities = new { tools = new { } },
                        serverInfo = new { name = "DCAAIExtension", version = "1.0.0" }
                    });

                case "tools/list":
                    return Result(id, new { tools = ToolSchemas() });

                case "tools/call":
                    return await CallTool(id, root);

                default:
                    return Error(id, -32601, $"Method not found: {method}");
            }
        }

        private object[] ToolSchemas() => new object[]
        {
            new { name = "list_files", description = "List source files in the active project.",
                  inputSchema = new { type = "object", properties = new { glob = new { type = "string" } } } },
            new { name = "read_file", description = "Read a file's full contents.",
                  inputSchema = new { type = "object", properties = new { path = new { type = "string" } }, required = new[]{"path"} } },
            new { name = "propose_edit", description = "Propose new content for a file; shows a diff for user acceptance before writing.",
                  inputSchema = new { type = "object", properties = new { path = new { type = "string" }, content = new { type = "string" } }, required = new[]{"path","content"} } },
        };

        private async Task<string> CallTool(JsonElement? id, JsonElement root)
        {
            var p = root.GetProperty("params");
            var name = p.GetProperty("name").GetString();
            var args = p.TryGetProperty("arguments", out var a) ? a : default;

            try
            {
                switch (name)
                {
                    case "list_files":
                        var glob = args.TryGetProperty("glob", out var g) ? g.GetString() : "*.cs";
                        var files = (ListFiles?.Invoke(glob) ?? Enumerable.Empty<string>()).ToArray();
                        return ToolText(id, string.Join("\n", files));

                    case "read_file":
                        var rp = args.GetProperty("path").GetString();
                        return ToolText(id, ReadFile?.Invoke(rp) ?? "");

                    case "propose_edit":
                        var ep = args.GetProperty("path").GetString();
                        var content = args.GetProperty("content").GetString();
                        var accepted = ProposeEdit != null && await ProposeEdit(ep, content);
                        return ToolText(id, accepted ? "Edit accepted and applied." : "Edit rejected by user.");

                    default:
                        return Error(id, -32602, $"Unknown tool: {name}");
                }
            }
            catch (Exception ex) { return Error(id, -32603, ex.Message); }
        }

        // ---- JSON-RPC envelope helpers ----
        private static readonly JsonSerializerOptions Opts = new();

        private static string Result(JsonElement? id, object result) =>
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = IdValue(id), result }, Opts);

        private static string ToolText(JsonElement? id, string text) =>
            Result(id, new { content = new[] { new { type = "text", text } }, isError = false });

        private static string Error(JsonElement? id, int code, string message) =>
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = IdValue(id), error = new { code, message } }, Opts);

        private static object IdValue(JsonElement? id)
        {
            if (id is not JsonElement e) return null;
            return e.ValueKind switch
            {
                JsonValueKind.Number => e.GetInt64(),
                JsonValueKind.String => e.GetString(),
                _ => null
            };
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); _listener?.Stop(); _listener?.Close(); } catch { }
        }
    }
}
