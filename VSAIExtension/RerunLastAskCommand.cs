namespace DCAAIExtension;

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

/// <summary>
/// Right-click (editor context menu) shortcut that replays the most recent
/// "Ask AI" request against the current code, then shows per-file diffs.
/// </summary>
[VisualStudioContribution]
internal class RerunLastAskCommand : Command
{
    // IDM_VS_CTXT_CODEWIN lives on guidSHLMainMenu. Numeric id 0x040D (1037).
    private static readonly Guid GuidShlMainMenu = new("{d309f791-903f-11d0-9efc-00a0c911004f}");
    private const int IDM_VS_CTXT_CODEWIN = 0x040D;

    public RerunLastAskCommand(VisualStudioExtensibility extensibility) : base(extensibility) { }

    public override CommandConfiguration CommandConfiguration => new("DCA AI: Rerun Last Ask")
    {
        Icon = new(ImageMoniker.KnownValues.PlayStepGroup, IconSettings.IconAndText),
        Placements =
        [
            // priority 0x0000 sorts this command as high as group ordering allows,
            // i.e. the top of the editor right-click menu.
            CommandPlacement.VsctParent(GuidShlMainMenu, IDM_VS_CTXT_CODEWIN, priority: 0x0000),
            CommandPlacement.KnownPlacements.ExtensionsMenu,
        ],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        if (!LastAskRequest.HasValue)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                "No previous Ask to rerun. Use the Ask Internal AI window first.",
                PromptOptions.OK, cancellationToken);
            return;
        }

        var project = await context.GetActiveProjectAsync(cancellationToken);
        var dir = project != null
            ? System.IO.Path.GetDirectoryName(project.Path)
            : System.IO.Directory.GetCurrentDirectory();
        var cfg = AIConfiguration.Load(dir ?? System.IO.Directory.GetCurrentDirectory());

        var gatherer = new ContextGatherer(this.Extensibility);
        var pieces = await gatherer.GatherAsync(context, LastAskRequest.Scope, cancellationToken);

        int chars = ContextGatherer.EstimateChars(pieces);
        if (chars > ContextGatherer.MaxChars)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                $"Payload {chars:N0} chars exceeds {ContextGatherer.MaxChars:N0}. Narrow the selection.",
                PromptOptions.OK, cancellationToken);
            return;
        }

        var prompt = gatherer.BuildPrompt(pieces, LastAskRequest.Question);

        var mode =
            cfg.Modes.HasFlag(DcaMode.OpenAiLlamaCpp) ? DcaMode.OpenAiLlamaCpp :
            cfg.Modes.HasFlag(DcaMode.OpenWebUI) ? DcaMode.OpenWebUI :
            cfg.Modes.HasFlag(DcaMode.GenericRest) ? DcaMode.GenericRest :
            DcaMode.WebSocket;

        var sb = new StringBuilder();
        using (var backend = AiBackendFactory.Create(mode, cfg))
        {
            await foreach (var token in backend.StreamAsync(prompt, cancellationToken))
                sb.Append(token);
        }

        var response = sb.ToString();
        if (ResponseParser.LooksLikeCode(response))
        {
            var diff = new DiffService(this.Extensibility);
            var selectionPiece = pieces.FirstOrDefault(p => p.IsSelection);

            // Selection ask -> single untagged block, replace only the selection.
            if (selectionPiece != null)
            {
                var block = ResponseParser.ParseFirstUntaggedBlock(response);
                if (block != null)
                {
                    await diff.ShowDiffAndApplyAsync(
                        selectionPiece.Path, block.Content, cfg.RequireDiffApproval,
                        selectionPiece, cancellationToken);
                    return;
                }
            }

            var files = ResponseParser.ParsePathFencedFiles(response);
            foreach (var f in files)
            {
                var path = System.IO.File.Exists(f.Path)
                    ? f.Path
                    : pieces.FirstOrDefault(p =>
                        string.Equals(System.IO.Path.GetFileName(p.Path),
                                      System.IO.Path.GetFileName(f.Path),
                                      StringComparison.OrdinalIgnoreCase))?.Path ?? f.Path;
                await diff.ShowDiffAndApplyAsync(path, f.Content, cfg.RequireDiffApproval, cancellationToken);
            }
            if (files.Count == 0)
                await this.Extensibility.Shell().ShowPromptAsync(response, PromptOptions.OK, cancellationToken);
        }
        else
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                response.Length > 0 ? response : "(empty response)",
                PromptOptions.OK, cancellationToken);
        }
    }
}