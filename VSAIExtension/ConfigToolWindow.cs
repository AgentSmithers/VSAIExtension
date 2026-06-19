namespace DCAAIExtension;

using System;
using System.IO;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.UI;

/// <summary>Remote control hosting the configuration UI.</summary>
internal class ConfigToolWindowContent : RemoteUserControl
{
    public ConfigToolWindowContent(ConfigToolWindowData data) : base(dataContext: data) { }
}

/// <summary>
/// ViewModel for the configuration GUI. Bound to ConfigToolWindowContent.xaml.
/// Persists to .dcaai.json in the supplied project directory.
/// </summary>
[DataContract]
internal class ConfigToolWindowData : NotifyPropertyChangedObject
{
    private readonly string _projectDir;
    private AIConfiguration _cfg;

    public ConfigToolWindowData(string projectDirectory)
    {
        _projectDir = projectDirectory;
        _cfg = AIConfiguration.Load(projectDirectory);

        SaveCommand = new AsyncCommand((p, ctx, ct) =>
        {
            PushToConfig();
            _cfg.Save(_projectDir);
            Status = $"Saved to {AIConfiguration.PathFor(_projectDir)}";
            return Task.CompletedTask;
        });

        TestCommand = new AsyncCommand(async (p, ctx, ct) =>
        {
            PushToConfig();
            Status = "Testing connection...";
            try
            {
                var mode = UseWebSocket ? DcaMode.WebSocket
                         : UseOpenAi ? DcaMode.OpenAiLlamaCpp
                         : UseOpenWebUi ? DcaMode.OpenWebUI
                         : DcaMode.GenericRest;
                using var backend = AiBackendFactory.Create(mode, _cfg);
                await foreach (var _ in backend.StreamAsync("ping", ct)) break;
                Status = "Connection OK.";
            }
            catch (Exception ex) { Status = "Failed: " + ex.Message; }
        });

        PullFromConfig();
    }

    private void PullFromConfig()
    {
        UseWebSocket = _cfg.Modes.HasFlag(DcaMode.WebSocket);
        UseOpenAi = _cfg.Modes.HasFlag(DcaMode.OpenAiLlamaCpp);
        UseOpenWebUi = _cfg.Modes.HasFlag(DcaMode.OpenWebUI);
        UseGenericRest = _cfg.Modes.HasFlag(DcaMode.GenericRest);
        UseMcpServer = _cfg.Modes.HasFlag(DcaMode.McpServer);

        Host = _cfg.Host; Port = _cfg.Port.ToString(); UseTls = _cfg.UseTls;
        AuthIndex = (int)_cfg.Auth;
        SecretText = _cfg.GetSecret();
        Model = _cfg.Model;

        McpStreamable = _cfg.McpTransports.HasFlag(McpTransport.StreamableHttp);
        McpStdio = _cfg.McpTransports.HasFlag(McpTransport.Stdio);
        McpSse = _cfg.McpTransports.HasFlag(McpTransport.Sse);
        McpPort = _cfg.McpPort.ToString();
        RequireDiffApproval = _cfg.RequireDiffApproval;
    }

    private void PushToConfig()
    {
        DcaMode m = DcaMode.None;
        if (UseWebSocket) m |= DcaMode.WebSocket;
        if (UseOpenAi) m |= DcaMode.OpenAiLlamaCpp;
        if (UseOpenWebUi) m |= DcaMode.OpenWebUI;
        if (UseGenericRest) m |= DcaMode.GenericRest;
        if (UseMcpServer) m |= DcaMode.McpServer;
        _cfg.Modes = m;

        _cfg.Host = Host;
        _cfg.Port = int.TryParse(Port, out var p) ? p : _cfg.Port;
        _cfg.UseTls = UseTls;
        _cfg.Auth = (AuthKind)AuthIndex;
        _cfg.SetSecret(SecretText);
        _cfg.Model = Model;

        McpTransport t = McpTransport.None;
        if (McpStreamable) t |= McpTransport.StreamableHttp;
        if (McpStdio) t |= McpTransport.Stdio;
        if (McpSse) t |= McpTransport.Sse;
        _cfg.McpTransports = t;
        _cfg.McpPort = int.TryParse(McpPort, out var mp) ? mp : _cfg.McpPort;
        _cfg.RequireDiffApproval = RequireDiffApproval;
    }

    // ---- Bound properties ----
    private bool _useWebSocket, _useOpenAi, _useOpenWebUi, _useGenericRest, _useMcp;
    [DataMember] public bool UseWebSocket { get => _useWebSocket; set => SetProperty(ref _useWebSocket, value); }
    [DataMember] public bool UseOpenAi { get => _useOpenAi; set => SetProperty(ref _useOpenAi, value); }
    [DataMember] public bool UseOpenWebUi { get => _useOpenWebUi; set => SetProperty(ref _useOpenWebUi, value); }
    [DataMember] public bool UseGenericRest { get => _useGenericRest; set => SetProperty(ref _useGenericRest, value); }
    [DataMember] public bool UseMcpServer { get => _useMcp; set => SetProperty(ref _useMcp, value); }

    private string _host = "", _port = "", _model = "", _secret = "";
    private bool _tls;
    [DataMember] public string Host { get => _host; set => SetProperty(ref _host, value); }
    [DataMember] public string Port { get => _port; set => SetProperty(ref _port, value); }
    [DataMember] public bool UseTls { get => _tls; set => SetProperty(ref _tls, value); }
    [DataMember] public string Model { get => _model; set => SetProperty(ref _model, value); }
    [DataMember] public string SecretText { get => _secret; set => SetProperty(ref _secret, value); }

    private int _authIndex;
    [DataMember] public int AuthIndex { get => _authIndex; set => SetProperty(ref _authIndex, value); }

    private bool _mcpStreamable, _mcpStdio, _mcpSse, _requireDiff = true;
    private string _mcpPort = "8090";
    [DataMember] public bool McpStreamable { get => _mcpStreamable; set => SetProperty(ref _mcpStreamable, value); }
    [DataMember] public bool McpStdio { get => _mcpStdio; set => SetProperty(ref _mcpStdio, value); }
    [DataMember] public bool McpSse { get => _mcpSse; set => SetProperty(ref _mcpSse, value); }
    [DataMember] public string McpPort { get => _mcpPort; set => SetProperty(ref _mcpPort, value); }
    [DataMember] public bool RequireDiffApproval { get => _requireDiff; set => SetProperty(ref _requireDiff, value); }

    private string _status = "Ready.";
    [DataMember] public string Status { get => _status; set => SetProperty(ref _status, value); }

    [DataMember] public AsyncCommand SaveCommand { get; }
    [DataMember] public AsyncCommand TestCommand { get; }
}
