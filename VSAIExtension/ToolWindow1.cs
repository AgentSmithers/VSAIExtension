namespace DCAAIExtension;

using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

/// <summary>
/// A sample tool window.
/// </summary>
[VisualStudioContribution]
public class ToolWindow1 : ToolWindow
{
    public WebSocketClass MyWebSocketClass1 = new();
    public string test = "asd";
    /// <summary>
    /// Initializes a new instance of the <see cref="ToolWindow1" /> class.
    /// </summary>
    public ToolWindow1(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
        this.Title = "Ask Internal AI";
    }

    /// <inheritdoc />
    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        // Use this object initializer to set optional parameters for the tool window.
        Placement = ToolWindowPlacement.Floating,
    };

    /// <inheritdoc />
    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Use InitializeAsync for any one-time setup or initialization.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
        => Task.FromResult<IRemoteUserControl>(new ToolWindow1Content(this.Extensibility));
}
