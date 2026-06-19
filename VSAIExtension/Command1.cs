namespace DCAAIExtension;

using System.Diagnostics;
using System.Threading;
using System.Windows;
using Microsoft;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;

/// <summary>
/// Command1 handler.
/// </summary>
[VisualStudioContribution]
internal class Command1 : Command
{
    private readonly TraceSource logger;
    ShellExtensibility MyShell;


    private AIConfiguration _cfg;
    private McpServer _mcp;
    private DiffService _diff;


    /// <summary>
    /// Initializes a new instance of the <see cref="Command1"/> class.
    /// </summary>
    /// <param name="traceSource">Trace source instance to utilize.</param>
    public Command1(TraceSource traceSource)
    {
        // This optional TraceSource can be used for logging in the command. You can use dependency injection to access
        // other services here as well.
        this.logger = Requires.NotNull(traceSource, nameof(traceSource));
    }

    /// <inheritdoc />
    public override CommandConfiguration CommandConfiguration => new("%DCAAIExtension.Command1.DisplayName%")
    {
        // Use this object initializer to set optional parameters for the command. The required parameter,
        // displayName, is set above. DisplayName is localized and references an entry in .vsextension\string-resources.json.
        Icon = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    };


        // The handler method that will be called when the event is raised
   private void HandleDataReceived(string data)
    {
        //Console.WriteLine($"Data received: {data}");
        if (MyShell != null)
        {
            if (MyShell.ShowPromptAsync(data, PromptOptions.OKCancel, new CancellationToken()).Result == true)
            {
                Clipboard.SetText(data);
            }
        }
    }
    //

    /// <inheritdoc />
    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        return base.InitializeAsync(cancellationToken);
    }

    public async void MakeRecommendation(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            var project = await context.GetActiveProjectAsync(cancellationToken);
            if (project == null)
            {
                Debug.WriteLine("No active project found.");
                await this.Extensibility.Shell().ShowPromptAsync("No active project found.", PromptOptions.OK, cancellationToken);
                return;
            }

            string projectDirectory = Path.GetDirectoryName(project.Path);
            if (string.IsNullOrEmpty(projectDirectory))
            {
                Debug.WriteLine("Project directory could not be determined.");
                await this.Extensibility.Shell().ShowPromptAsync("Project directory could not be determined.", PromptOptions.OK, cancellationToken);
                return;
            }

            //string[] csFiles = Directory.GetFiles(projectDirectory, "*.cs|*.vb", SearchOption.AllDirectories);
            string[] csFiles = Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(projectDirectory, "*.vb", SearchOption.AllDirectories))
            .ToArray();

            string FullSourceCodeDump = "";

            foreach (var file in csFiles)
            {
                string content = await File.ReadAllTextAsync(file, cancellationToken);

                string FormattedCode = $"Contents of {file}:\n{content}\n";
                Debug.WriteLine(FormattedCode);

                FullSourceCodeDump += FormattedCode;
            }
            if (FullSourceCodeDump.Length > 55000) //Max size RTX8000 can handle with 48G VRAM and using Distilled qwen.
            {
                //FullSourceCodeDump = File.ReadAllText(project.Path);
                var ActiveTextView = await context.GetActiveTextViewAsync(cancellationToken);
                FullSourceCodeDump = await File.ReadAllTextAsync(ActiveTextView.FilePath, cancellationToken);
                //Cant send all source files, sending just the main one thats selected.

               
                foreach (Microsoft.VisualStudio.Extensibility.Editor.ITextDocumentSnapshotLine Myline in ActiveTextView.Document.Lines) //Microsoft.VisualStudio.Extensibility.TextDocumentSnapshotLine
                {
                    //if ActiveTextView.Document.
                    Debug.WriteLine(Myline.TextIncludingLineBreak.CopyToString()); //Microsoft.VisualStudio.Extensibility.EditorHostService.TextDocumentSnapshotLine
                }

                if (FullSourceCodeDump.Length > 55000) //Max size RTX8000 can handle with 48G VRAM and using Distilled qwen.
                {
                    //Get lines from Visual Studio thats highlighted.
                }

                

                if (ActiveTextView.Selection.End.Offset != ActiveTextView.Selection.Start.Offset)
                {
                    FullSourceCodeDump = ActiveTextView.Document.Text.CopyToString().Substring(ActiveTextView.Selection.Start, ActiveTextView.Selection.End.Offset - ActiveTextView.Selection.Start.Offset);
                }



            }

            if (FullSourceCodeDump.Length > 55000) //Max size RTX8000 can handle with 48G VRAM and using Distilled qwen.
            {
                Debug.WriteLine("Request too large - " + FullSourceCodeDump.Length);
                return;
            }

            //This needs to be a JSON class that sends request for RAG, To Infer prompts, or to use one raw.
            MyWebSocket.AddMessageToWebSocketQueue(FullSourceCodeDump + "\r\n\r\n" + "What is wrong with this code, if you see anything reply back only in the same language with the corrections. If you don't see anything wrong then just reply back 'ok!'."); //Can only send up to characters 65500 chars
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading .cs files: {ex.Message}");
        }


    }

    /// <summary>
    /// Gathers code context and sends it to the LlamaCPP endpoint for evaluation.
    /// Changed from 'async void' to 'async Task' for safe async execution.
    /// </summary>
    public async Task MakeRecommendationAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            var project = await context.GetActiveProjectAsync(cancellationToken);
            if (project == null)
            {
                Debug.WriteLine("No active project found.");
                return;
            }

            string projectDirectory = Path.GetDirectoryName(project.Path);
            if (string.IsNullOrEmpty(projectDirectory))
            {
                Debug.WriteLine("Project directory could not be determined.");
                return;
            }

            string[] csFiles = Directory.GetFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(projectDirectory, "*.vb", SearchOption.AllDirectories))
                .ToArray();

            string FullSourceCodeDump = "";

            foreach (var file in csFiles)
            {
                string content = await File.ReadAllTextAsync(file, cancellationToken);
                string FormattedCode = $"Contents of {file}:\n{content}\n";
                Debug.WriteLine(FormattedCode);

                FullSourceCodeDump += FormattedCode;
            }

            // Fallback if the payload is too large
            if (FullSourceCodeDump.Length > 55000)
            {
                var ActiveTextView = await context.GetActiveTextViewAsync(cancellationToken);
                FullSourceCodeDump = await File.ReadAllTextAsync(ActiveTextView.FilePath, cancellationToken);

                foreach (ITextDocumentSnapshotLine Myline in ActiveTextView.Document.Lines)
                {
                    Debug.WriteLine(Myline.TextIncludingLineBreak.CopyToString());
                }

                if (ActiveTextView.Selection.End.Offset != ActiveTextView.Selection.Start.Offset)
                {
                    // Truncate to just the selected text if there is an active selection
                    FullSourceCodeDump = ActiveTextView.Document.Text.CopyToString().Substring(
                        ActiveTextView.Selection.Start.Offset,
                        ActiveTextView.Selection.End.Offset - ActiveTextView.Selection.Start.Offset);
                }
            }

            if (FullSourceCodeDump.Length > 55000)
            {
                Debug.WriteLine("Request too large - " + FullSourceCodeDump.Length);
                return;
            }

            // ------------------------------------------------------------------
            // Call the OpenAI Client API here when the user executes the command
            // ------------------------------------------------------------------

            string prompt = FullSourceCodeDump + "\r\n\r\n" + "What is wrong with this code, if you see anything reply back only in the same language with the corrections. If you don't see anything wrong then just reply back 'ok!'.";

            // If you still want to push to the websocket, you can leave this here:
            // MyWebSocket.AddMessageToWebSocketQueue(prompt); 

            //Update this with your informtion for testing.
            using var client = new OpenAIClientAPI(
                baseUrl: "http://192.168.0.xxx:8081",
                bearerToken: "sk-XXXXXXXXXXXXXXX",
                logger: msg => Debug.WriteLine(msg));

            var request = new OpenAIClientAPI.ChatCompletionRequest
            {
                Temperature = 0.2, // Lowered temperature slightly for code corrections
                Messages = new[] {
                    new OpenAIClientAPI.ChatMessage { Role = "system", Content = "You are an expert C# and VB.NET coding assistant." },
                    new OpenAIClientAPI.ChatMessage { Role = "user", Content = prompt }
                }
            };

            Debug.WriteLine("--- AI Response Stream Starting ---");

            // Consume the stream token by token
            await foreach (var token in client.StreamChatCompletionAsync(request, cancellationToken))
            {
                // Print to debug window, or append to your Tool Window UI here
                Debug.Write(token);
            }

            Debug.WriteLine("\n--- AI Response Stream Finished ---");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error during MakeRecommendationAsync: {ex.Message}");
        }
    }

    WebSocketClass MyWebSocket = new WebSocketClass();

    public void startWebSocket()
    {
        // Use InitializeAsync for any one-time setup or initialization.
        //MyWebSocket.ConnectWebSocket();    
        Task.Run(() => MyWebSocket.ConnectWebSocket());
        MyWebSocket.DataReceived += HandleDataReceived;
    }

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        MyShell = this.Extensibility.Shell();

        var project = await context.GetActiveProjectAsync(cancellationToken);
        var dir = project != null ? Path.GetDirectoryName(project.Path) : Directory.GetCurrentDirectory();
        _cfg = AIConfiguration.Load(dir);
        _diff ??= new DiffService(this.Extensibility);

        // Start MCP server if enabled (idempotent-ish: only first run wires it).
        if (_cfg.Modes.HasFlag(DcaMode.McpServer) && _mcp == null)
        {
            _mcp = new McpServer(_cfg, m => Debug.WriteLine(m))
            {
                ListFiles = glob => Directory.GetFiles(dir, glob, SearchOption.AllDirectories),
                ReadFile = path => File.ReadAllText(path),
                ProposeEdit = (path, content) =>
                    _diff.ShowDiffAndApplyAsync(path, content, _cfg.RequireDiffApproval, cancellationToken),
            };
            _mcp.StartHttp();
        }

        // Pick the first enabled chat backend for the recommendation flow.
        DcaMode chat =
            _cfg.Modes.HasFlag(DcaMode.OpenAiLlamaCpp) ? DcaMode.OpenAiLlamaCpp :
            _cfg.Modes.HasFlag(DcaMode.OpenWebUI) ? DcaMode.OpenWebUI :
            _cfg.Modes.HasFlag(DcaMode.GenericRest) ? DcaMode.GenericRest :
            DcaMode.WebSocket;

        await MakeRecommendationViaBackendAsync(context, chat, cancellationToken);
    }
    private async Task MakeRecommendationViaBackendAsync(IClientContext context, DcaMode mode, CancellationToken ct)
    {
        var gatherer = new ContextGatherer(this.Extensibility);
        var prompt = await gatherer.BuildPromptAsync(
            context, ContextScope.ActiveFile | ContextScope.Selection, null, ct);
        if (prompt == null) return;

        using var backend = AiBackendFactory.Create(mode, _cfg, MyWebSocket);
        var sb = new System.Text.StringBuilder();
        await foreach (var token in backend.StreamAsync(prompt, ct))
        {
            sb.Append(token);
            Debug.Write(token);
        }
        // Show the response; replace with a tool-window append if you prefer.
        await MyShell.ShowPromptAsync(sb.ToString(), PromptOptions.OK, ct);
    }
}
