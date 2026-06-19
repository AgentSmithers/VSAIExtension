namespace DCAAIExtension;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

/// <summary>
/// Remote user control hosting the "Ask Internal AI" UI.
/// Disposes the data context so the ViewModel's size-polling loop is stopped
/// when the tool window closes.
/// </summary>
internal class ToolWindow1Content : RemoteUserControl
{
    private readonly ToolWindow1Data _data;

    public ToolWindow1Content(VisualStudioExtensibility extensibility)
        : base(dataContext: new ToolWindow1Data(extensibility))
    {
        _data = (ToolWindow1Data)this.DataContext!;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _data.Dispose();
        }

        base.Dispose(disposing);
    }
}