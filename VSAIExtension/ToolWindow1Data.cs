namespace DCAAIExtension;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

/// <summary>
/// ViewModel for the "Ask Internal AI" tool window.
/// - Live payload stats (polls the cached context so background selection
///   changes are reflected while the window is open).
/// - Cancellable requests (Stop).
/// - Selection asks replace only the selection; whole-file/path asks replace files.
/// - Decline leaves the proposed code open in a preview document.
/// </summary>
[DataContract]
internal class ToolWindow1Data : NotifyPropertyChangedObject, IDisposable
{
    public static IClientContext PendingClientContext { get; set; }

    private readonly VisualStudioExtensibility _ext;
    private readonly ContextGatherer _gatherer;
    private readonly DiffService _diff;
    private readonly WebSocketClass MyWebSocketClass = new();

    private IClientContext _lastContext;
    private CancellationTokenSource _runningCts;

    // Polling for live size updates.
    private readonly CancellationTokenSource _pollCts = new();
    private string _lastSizeSignature = "";

    private bool _includeErrors;
    [DataMember] public bool IncludeErrors { get => _includeErrors; set => SetProperty(ref _includeErrors, value); }

    public ToolWindow1Data(VisualStudioExtensibility extensibility)
    {
        _ext = extensibility;
        _gatherer = new ContextGatherer(extensibility);
        _diff = new DiffService(extensibility, ApproveInFormAsync);
        _lastContext = PendingClientContext;

        Task.Run(() => MyWebSocketClass.ConnectWebSocket());
        MyWebSocketClass.DataReceived += HandleDataReceived;

        LoadServerSummary();
        if (_lastContext != null) _ = RecomputeSizeAsync(_lastContext);
        _ = PollSizeLoopAsync(_pollCts.Token);   // #1: live updates

        RefreshSizeCommand = new AsyncCommand(async (p, ctx, ct) =>
        {
            _lastContext = ctx ?? _lastContext;   // fresh snapshot of the editor's current selection
            _lastSizeSignature = "";              // force refresh even if numbers match
            await RecomputeSizeAsync(_lastContext);
        });

        AcceptChangesCommand = new AsyncCommand((p, ctx, ct) =>
        {
            _approvalTcs?.TrySetResult(true);
            return Task.CompletedTask;
        });

        RejectChangesCommand = new AsyncCommand((p, ctx, ct) =>
        {
            _approvalTcs?.TrySetResult(false);
            return Task.CompletedTask;
        });

        ClearCommand = new AsyncCommand((p, ctx, ct) =>
        {
            _lastContext = ctx ?? _lastContext;
            Text = string.Empty; Status = "Cleared.";
            return Task.CompletedTask;
        });

        CopyCommand = new AsyncCommand((p, ctx, ct) =>
        {
            Status = string.IsNullOrEmpty(Text) ? "Nothing to copy." : "Response ready to copy.";
            return Task.CompletedTask;
        });

        StopCommand = new AsyncCommand((p, ctx, ct) =>
        {
            try { _runningCts?.Cancel(); Status = "Stopping..."; } catch { }
            return Task.CompletedTask;
        });

        HelloCommand = new AsyncCommand(async (parameter, clientContext, ct) =>
        {
            _lastContext = clientContext ?? _lastContext;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _runningCts = cts;
            IsBusy = true; IsNotBusy = false;
            try
            {
                var scope = CurrentScope();
                if (scope == ContextScope.None) { Status = "Select at least one context source."; return; }

                Status = "Gathering context...";
                var pieces = await _gatherer.GatherAsync(clientContext, scope, cts.Token);

                if (IncludeErrors)
                {
                    Status = "Building to collect errors...";
                    var projPath = await TryGetProjectPathAsync(clientContext, cts.Token);
                    if (projPath != null)
                    {
                        var errText = await BuildErrorCollector.CollectAsync(projPath, cts.Token);
                        if (!string.IsNullOrWhiteSpace(errText))
                            pieces.Add(new ContextPiece { Path = "VisualStudio Build Errors", Content = errText });
                    }
                }

                int chars = ContextGatherer.EstimateChars(pieces);
                // ...existing over-limit check continues unchanged...
                if (chars > ContextGatherer.MaxChars)
                {
                    Status = $"Payload {chars:N0} chars exceeds {ContextGatherer.MaxChars:N0}. Narrow the context.";
                    return;
                }

                var question = parameter?.ToString();
                LastAskRequest.Set(scope, question);

                var prompt = _gatherer.BuildPrompt(pieces, question);
                var cfg = LoadConfig(clientContext, cts.Token);
                var mode = PickChatMode(cfg);

                Status = $"Asking {mode}...";
                Text = string.Empty;
                var sb = new StringBuilder();
                using (var backend = AiBackendFactory.Create(mode, cfg, MyWebSocketClass))
                {
                    await foreach (var token in backend.StreamAsync(prompt, cts.Token))
                    {
                        sb.Append(token);
                        Text += token;
                    }
                }

                await HandleResponseAsync(sb.ToString(), pieces, cfg, cts.Token);
                Status = "Done.";
            }
            catch (OperationCanceledException) { Status = "Cancelled."; }
            catch (Exception ex) { Status = "Error: " + ex.Message; }
            finally { IsBusy = false; IsNotBusy = true; _runningCts = null; }
        });
    }

    private TaskCompletionSource<bool> _approvalTcs;

    /// <summary>
    /// Surfaces an Accept/Reject bar in the tool window and awaits the user's click.
    /// Returns true to keep the applied change, false to revert it.
    /// </summary>
    private Task<bool> ApproveInFormAsync(string label, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvalTcs = tcs;
        PendingApprovalLabel = label;
        IsAwaitingApproval = true;

        var reg = ct.CanBeCanceled ? ct.Register(() => tcs.TrySetResult(false)) : default;

        async Task<bool> Await()
        {
            try { return await tcs.Task; }
            finally
            {
                reg.Dispose();
                IsAwaitingApproval = false;
                PendingApprovalLabel = string.Empty;
                if (ReferenceEquals(_approvalTcs, tcs)) _approvalTcs = null;
            }
        }
        return Await();
    }
    private static async Task<string> TryGetProjectPathAsync(IClientContext ctx, CancellationToken ct)
    {
        try { var p = await ctx.GetActiveProjectAsync(ct); return p?.Path; }
        catch { return null; }
    }

    // ---- response -> diff routing ----
    private async Task HandleResponseAsync(
        string response, List<ContextPiece> sentPieces, AIConfiguration cfg, CancellationToken ct)
    {
        if (!ResponseParser.LooksLikeCode(response)) return;

        var selectionPiece = sentPieces.FirstOrDefault(p => p.IsSelection);

        // Selection ask -> expect a single untagged block, replace only the selection.
        if (selectionPiece != null)
        {
            var block = ResponseParser.ParseFirstUntaggedBlock(response);
            // If the model tagged paths anyway, fall through to file handling below.
            if (block != null)
            {
                await _diff.ShowDiffAndApplyAsync(
                    selectionPiece.Path, block.Content, cfg.RequireDiffApproval, selectionPiece, ct);
                return;
            }
        }

        // Whole-file / multi-file path-tagged replacements.
        var files = ResponseParser.ParsePathFencedFiles(response);
        if (files.Count == 0 && sentPieces.Count == 1 && sentPieces[0].Path != null)
        {
            var block = ResponseParser.ParseFirstUntaggedBlock(response);
            if (block != null)
                files.Add(new ReturnedFile { Path = sentPieces[0].Path, Content = block.Content });
        }

        foreach (var f in files)
        {
            var resolved = ResolvePath(f.Path, sentPieces);
            if (resolved == null) continue;
            await _diff.ShowDiffAndApplyAsync(resolved, f.Content, cfg.RequireDiffApproval, ct);
        }
    }

    private static string ResolvePath(string returned, List<ContextPiece> pieces)
    {
        if (string.IsNullOrWhiteSpace(returned)) return null;
        if (File.Exists(returned)) return returned;
        var name = Path.GetFileName(returned);
        var match = pieces.FirstOrDefault(p =>
            string.Equals(Path.GetFileName(p.Path), name, StringComparison.OrdinalIgnoreCase));
        return match?.Path ?? returned;
    }

    // ---- config / mode ----
    private AIConfiguration LoadConfig(IClientContext ctx, CancellationToken ct)
    {
        try
        {
            var project = ctx.GetActiveProjectAsync(ct).GetAwaiter().GetResult();
            var dir = project != null ? Path.GetDirectoryName(project.Path) : Directory.GetCurrentDirectory();
            return AIConfiguration.Load(dir ?? Directory.GetCurrentDirectory());
        }
        catch { return AIConfiguration.Load(Directory.GetCurrentDirectory()); }
    }

    private void LoadServerSummary()
    {
        try
        {
            var cfg = AIConfiguration.Load(Directory.GetCurrentDirectory());
            var mode = PickChatMode(cfg);
            ServerSummary = $"{mode} · {cfg.Host}:{cfg.Port}" + (cfg.UseTls ? " (TLS)" : "");
        }
        catch { ServerSummary = "config unavailable"; }
    }

    private static DcaMode PickChatMode(AIConfiguration cfg) =>
        cfg.Modes.HasFlag(DcaMode.OpenAiLlamaCpp) ? DcaMode.OpenAiLlamaCpp :
        cfg.Modes.HasFlag(DcaMode.OpenWebUI) ? DcaMode.OpenWebUI :
        cfg.Modes.HasFlag(DcaMode.GenericRest) ? DcaMode.GenericRest :
        DcaMode.WebSocket;

    // ---- size: poll loop + recompute ----
    private async Task PollSizeLoopAsync(CancellationToken ct)
    {
        // Periodically refresh the payload stats so background selection/file
        // changes are reflected while the window is open. Re-queries the cached
        // IClientContext, which returns the current active view/selection.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!IsBusy && _lastContext != null)
                    await RecomputeSizeAsync(_lastContext, quiet: true);
            }
            catch { /* ignore transient errors during polling */ }

            try { await Task.Delay(1500, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RecomputeSizeAsync(IClientContext ctx, bool quiet = false)
    {
        var scope = CurrentScope();
        if (scope == ContextScope.None) { SizeSummary = "0 chars"; IsOverLimit = false; return; }
        if (ctx == null) { SizeSummary = "click \"Refresh size\" to compute"; IsOverLimit = false; return; }
        try
        {
            // Must use the IClientContext: it carries the active view/selection captured
            // when the context was created (the last button press). There is no out-of-proc
            // API to read the live selection while this tool window is focused.
            var pieces = await _gatherer.GatherAsync(ctx, scope, CancellationToken.None);
            int chars = ContextGatherer.EstimateChars(pieces);
            int tokens = ContextGatherer.EstimateTokens(chars);

            var signature = $"{scope}:{chars}:{pieces.Count}";
            if (signature == _lastSizeSignature) return;
            _lastSizeSignature = signature;

            SizeSummary = $"{chars:N0} chars  (~{tokens:N0} tokens, {pieces.Count} file(s))";
            IsOverLimit = chars > ContextGatherer.MaxChars;
        }
        catch (Exception ex)
        {
            if (!quiet) { SizeSummary = "n/a"; Status = "Size: " + ex.Message; }
        }
    }

    private void TriggerSizeRecompute()
    {
        _lastSizeSignature = ""; // force refresh on explicit toggle
        if (_lastContext != null) _ = RecomputeSizeAsync(_lastContext);
        else SizeSummary = "open via menu to compute";
    }

    private ContextScope CurrentScope()
    {
        var s = ContextScope.None;
        if (IncludeSelection) s |= ContextScope.Selection;
        if (IncludeActiveFile) s |= ContextScope.ActiveFile;
        if (IncludeProject) s |= ContextScope.FullProject;
        if (IncludeOpenTabs) s |= ContextScope.OpenTabs;
        return s;
    }

    private void HandleDataReceived(string data) => Text += data;

    public void Dispose()
    {
        try { _approvalTcs?.TrySetResult(false); } catch { }
        try { _pollCts.Cancel(); _pollCts.Dispose(); } catch { }
        try { _runningCts?.Cancel(); } catch { }
    }

    // ---- bound properties ----
    private bool _isAwaitingApproval;
    [DataMember] public bool IsAwaitingApproval { get => _isAwaitingApproval; set => SetProperty(ref _isAwaitingApproval, value); }

    private string _pendingApprovalLabel = string.Empty;
    [DataMember] public string PendingApprovalLabel { get => _pendingApprovalLabel; set => SetProperty(ref _pendingApprovalLabel, value); }

    [DataMember] public AsyncCommand AcceptChangesCommand { get; }
    [DataMember] public AsyncCommand RejectChangesCommand { get; }

    private bool _selection = true, _activeFile, _project, _openTabs;
    [DataMember] public bool IncludeSelection { get => _selection; set { SetProperty(ref _selection, value); TriggerSizeRecompute(); } }
    [DataMember] public bool IncludeActiveFile { get => _activeFile; set { SetProperty(ref _activeFile, value); TriggerSizeRecompute(); } }
    [DataMember] public bool IncludeProject { get => _project; set { SetProperty(ref _project, value); TriggerSizeRecompute(); } }
    [DataMember] public bool IncludeOpenTabs { get => _openTabs; set { SetProperty(ref _openTabs, value); TriggerSizeRecompute(); } }

    private string _sizeSummary = "...";
    [DataMember] public string SizeSummary { get => _sizeSummary; set => SetProperty(ref _sizeSummary, value); }
    private bool _isOverLimit;
    [DataMember] public bool IsOverLimit { get => _isOverLimit; set => SetProperty(ref _isOverLimit, value); }

    private bool _isBusy;
    [DataMember] public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }
    private bool _isNotBusy = true;
    [DataMember] public bool IsNotBusy { get => _isNotBusy; set => SetProperty(ref _isNotBusy, value); }

    private string _serverSummary = "";
    [DataMember] public string ServerSummary { get => _serverSummary; set => SetProperty(ref _serverSummary, value); }

    private string _name = string.Empty;
    [DataMember] public string Name { get => _name; set => SetProperty(ref _name, value); }
    private string _text = string.Empty;
    [DataMember] public string Text { get => _text; set => SetProperty(ref _text, value); }
    private string _status = "Ready.";
    [DataMember] public string Status { get => _status; set => SetProperty(ref _status, value); }

    [DataMember] public AsyncCommand HelloCommand { get; }
    [DataMember] public AsyncCommand StopCommand { get; }
    [DataMember] public AsyncCommand ClearCommand { get; }
    [DataMember] public AsyncCommand CopyCommand { get; }
    [DataMember] public AsyncCommand RefreshSizeCommand { get; }
}