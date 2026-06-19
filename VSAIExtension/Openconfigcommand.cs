namespace DCAAIExtension;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

/// <summary>
/// Menu command that opens the DCA AI Configuration tool window.
/// This is the missing piece that makes ConfigToolWindow reachable from the IDE
/// (Extensions menu > DCA AI > Configuration...).
/// </summary>
[VisualStudioContribution]
internal class OpenConfigCommand : Command
{
    public OpenConfigCommand(VisualStudioExtensibility extensibility) : base(extensibility) { }

    public override CommandConfiguration CommandConfiguration => new("DCA AI Configuration...")
    {
        Icon = new(ImageMoniker.KnownValues.Settings, IconSettings.IconAndText),
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
    };

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        var project = await context.GetActiveProjectAsync(cancellationToken);
        ConfigToolWindow.PendingProjectDirectory = project != null
            ? System.IO.Path.GetDirectoryName(project.Path)
            : null;

        await this.Extensibility.Shell().ShowToolWindowAsync<ConfigToolWindow>(activate: true, cancellationToken);
    }
}