namespace DCAAIExtension;

/// <summary>
/// Process-wide memory of the most recent "Ask AI" request so the editor
/// right-click "Rerun last Ask" command can replay it without the tool window.
/// </summary>
internal static class LastAskRequest
{
    public static ContextScope Scope { get; set; } = ContextScope.Selection | ContextScope.ActiveFile;
    public static string Question { get; set; }
    public static bool HasValue { get; set; }

    public static void Set(ContextScope scope, string question)
    {
        Scope = scope;
        Question = question;
        HasValue = true;
    }
}
