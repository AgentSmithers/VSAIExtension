namespace DCAAIExtension;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

/// <summary>
/// Tool window that hosts the configuration GUI.
/// Project directory is set by OpenConfigCommand before the window is shown
/// (resolved via IClientContext.GetActiveProjectAsync, which is the proven,
/// already-used-in-this-codebase way to get the active project).
/// </summary>
[VisualStudioContribution]
internal class ConfigToolWindow : ToolWindow
{
    private ConfigToolWindowData _data;

    /// <summary>Set by OpenConfigCommand right before ShowToolWindowAsync.</summary>
    public static string PendingProjectDirectory { get; set; }

    public ConfigToolWindow(VisualStudioExtensibility extensibility) : base(extensibility)
    {
        Title = "DCA AI Configuration";
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.Floating,
    };

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        var dir = PendingProjectDirectory ?? Directory.GetCurrentDirectory();
        _data = new ConfigToolWindowData(dir);
        return Task.CompletedTask;
    }

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
        => Task.FromResult<IRemoteUserControl>(new ConfigToolWindowContent(_data));
}