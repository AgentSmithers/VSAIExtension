namespace DCAAIExtension;

using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;

/// <summary>
/// A command for showing a tool window.
/// </summary>
[VisualStudioContribution]
public class ToolWindow1Command : Command
{
    /// <inheritdoc />
    /// 
    public override CommandConfiguration CommandConfiguration => new(displayName: "Ask Internal AI")
    {
        // Use this object initializer to set optional parameters for the command. The required parameter,
        // displayName, is set above. To localize the displayName, add an entry in .vsextension\string-resources.json
        // and reference it here by passing "%DCAAIExtension.ToolWindow1Command.DisplayName%" as a constructor parameter.
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
        Icon = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
    };

    /// <inheritdoc />
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        var project = await context.GetActiveProjectAsync(cancellationToken);
        if (project == null)
        {
            Debug.WriteLine("No active project found.");
            await this.Extensibility.Shell().ShowPromptAsync("No active project found.", PromptOptions.OK, cancellationToken);
            return;
        }

        string FullSourceCodeDump = "";
        var ActiveTextView = await context.GetActiveTextViewAsync(cancellationToken);
        FullSourceCodeDump = await System.IO.File.ReadAllTextAsync(ActiveTextView.FilePath, cancellationToken);
        if (ActiveTextView.Selection.End.Offset != ActiveTextView.Selection.Start.Offset)
        {
            FullSourceCodeDump = ActiveTextView.Document.Text.CopyToString().Substring(ActiveTextView.Selection.Start, ActiveTextView.Selection.End.Offset - ActiveTextView.Selection.Start.Offset);
        }

        ToolWindow1Data.PendingClientContext = context;
        await this.Extensibility.Shell().ShowToolWindowAsync<ToolWindow1>(activate: true, cancellationToken);
        //await this.Extensibility.Shell().GetToolWindow()
    }
}
