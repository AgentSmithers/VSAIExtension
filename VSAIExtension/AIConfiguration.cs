using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DCAAIExtension
{
    /// <summary>
    /// Operating modes the extension can run in. Multiple may be enabled at once.
    /// </summary>
    [Flags]
    public enum DcaMode
    {
        None = 0,
        WebSocket = 1 << 0,
        OpenAiLlamaCpp = 1 << 1,
        OpenWebUI = 1 << 2,
        GenericRest = 1 << 3,
        McpServer = 1 << 4,
    }

    public enum AuthKind { None, Bearer, Session, OAuth, ApiKeyHeader }

    [Flags]
    public enum McpTransport
    {
        None = 0,
        StreamableHttp = 1 << 0,
        Stdio = 1 << 1,
        Sse = 1 << 2,
    }

    /// <summary>
    /// Serializable configuration persisted to .dcaai.json in the project root.
    /// Secret fields are DPAPI-encrypted (CurrentUser scope) before serialization,
    /// so the JSON is safe to commit/share.
    /// </summary>
    public class AIConfiguration
    {
        public const string FileName = ".dcaai.json";

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DcaMode Modes { get; set; } = DcaMode.WebSocket;

        // Connection
        public string Host { get; set; } = "192.168.0.100";
        public int Port { get; set; } = 8081;
        public bool UseTls { get; set; } = false;

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public AuthKind Auth { get; set; } = AuthKind.Bearer;

        /// <summary>Encrypted at rest. Use <see cref="GetSecret"/>/<see cref="SetSecret"/>.</summary>
        public string EncryptedSecret { get; set; } = "";

        public string SessionCookieName { get; set; } = "session";
        public string OAuthTokenUrl { get; set; } = "";

        // Backend specifics
        public string Model { get; set; } = "llama";
        public double Temperature { get; set; } = 0.2;
        public string WebSocketPath { get; set; } = "/ws/";
        public string OpenWebUiChatPath { get; set; } = "/api/chat/completions";
        public string GenericRestPath { get; set; } = "/v1/chat/completions";

        // MCP server
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public McpTransport McpTransports { get; set; } = McpTransport.StreamableHttp;
        public int McpPort { get; set; } = 8090;
        public bool McpRequireAuth { get; set; } = true;

        public bool RequireDiffApproval { get; set; } = true;

        // ---- Secret helpers (DPAPI) ----

        [JsonIgnore]
        public string Secret
        {
            get => GetSecret();
            set => SetSecret(value);
        }

        public string GetSecret()
        {
            if (string.IsNullOrEmpty(EncryptedSecret)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(EncryptedSecret), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }

        public void SetSecret(string plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) { EncryptedSecret = ""; return; }
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), null, DataProtectionScope.CurrentUser);
            EncryptedSecret = Convert.ToBase64String(bytes);
        }

        // ---- Computed endpoints ----

        [JsonIgnore]
        public string BaseHttpUrl => $"{(UseTls ? "https" : "http")}://{Host}:{Port}";

        [JsonIgnore]
        public string WebSocketUrl => $"{(UseTls ? "wss" : "ws")}://{Host}:{Port}{WebSocketPath}";

        // ---- Load / Save ----

        private static readonly JsonSerializerOptions Opts = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public static string PathFor(string projectDirectory) =>
            Path.Combine(projectDirectory, FileName);

        public static AIConfiguration Load(string projectDirectory)
        {
            try
            {
                var path = PathFor(projectDirectory);
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<AIConfiguration>(File.ReadAllText(path), Opts) ?? new AIConfiguration();
            }
            catch { /* fall through to default */ }
            return new AIConfiguration();
        }

        public void Save(string projectDirectory)
        {
            File.WriteAllText(PathFor(projectDirectory), JsonSerializer.Serialize(this, Opts));
        }
    }
}
