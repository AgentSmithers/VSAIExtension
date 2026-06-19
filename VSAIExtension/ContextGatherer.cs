using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Editor;

namespace DCAAIExtension
{
    [Flags]
    public enum ContextScope
    {
        None = 0,
        Selection = 1 << 0,
        ActiveFile = 1 << 1,
        FullProject = 1 << 2,
        OpenTabs = 1 << 3,
    }

    /// <summary>One gathered context piece plus its origin path (for diff mapping).</summary>
    public sealed class ContextPiece
    {
        public string Path { get; init; }
        public string Content { get; init; }

        /// <summary>True if this piece came from an editor selection (drives selection-only replace).</summary>
        public bool IsSelection { get; init; }

        /// <summary>For selection pieces: character offsets of the selection in the document.</summary>
        public int SelectionStart { get; init; }
        public int SelectionEnd { get; init; }

        public int Length => Content?.Length ?? 0;
    }

    /// <summary>
    /// Gathers code context from the IDE according to the selected scopes and
    /// builds the final prompt.
    /// </summary>
    public sealed class ContextGatherer
    {
        public const int MaxChars = 55000;

        public const string CodeReplyConvention =
            "When you return corrected or new code, output each file as a fenced block whose " +
            "info string is the file path, like:\n```path=RelativeOrFullPath.cs\n<full file contents>\n```\n" +
            "If the user sent only a selected snippet, return ONLY the replacement for that snippet " +
            "in a single fenced block (no path needed). If nothing needs changing, reply exactly: No changed required.";

        private readonly VisualStudioExtensibility _ext;
        public ContextGatherer(VisualStudioExtensibility extensibility) => _ext = extensibility;

        /// <summary>Gathers the raw pieces for the chosen scopes (no prompt wrapping).</summary>
        /// <summary>Gathers the raw pieces for the chosen scopes (no prompt wrapping).</summary>
        public async Task<List<ContextPiece>> GatherAsync(
            IClientContext context, ContextScope scope, CancellationToken ct)
        {
            ITextViewSnapshot activeView = null;
            try { activeView = await context.GetActiveTextViewAsync(ct); } catch { }
            return await BuildPiecesAsync(activeView, context, scope, ct);
        }

        private async Task<List<ContextPiece>> BuildPiecesAsync(
            ITextViewSnapshot activeView, IClientContext context, ContextScope scope, CancellationToken ct)
        {
            var pieces = new List<ContextPiece>();

            // Selection
            if (scope.HasFlag(ContextScope.Selection) && activeView != null)
            {
                var sel = activeView.Selection;
                if (sel.End.Offset != sel.Start.Offset)
                {
                    var text = activeView.Document.Text.CopyToString()
                        .Substring(sel.Start.Offset, sel.End.Offset - sel.Start.Offset);
                    pieces.Add(new ContextPiece
                    {
                        Path = activeView.FilePath,
                        Content = text,
                        IsSelection = true,
                        SelectionStart = sel.Start.Offset,
                        SelectionEnd = sel.End.Offset,
                    });
                }
            }

            // Active file
            // Active file - read the LIVE editor buffer so unsaved (accepted) edits are included,
            // not the stale on-disk copy.
            if (scope.HasFlag(ContextScope.ActiveFile) && activeView?.FilePath != null)
            {
                pieces.Add(new ContextPiece
                {
                    Path = activeView.FilePath,
                    Content = activeView.Document.Text.CopyToString()
                });
            }

            // Full project
            if (scope.HasFlag(ContextScope.FullProject) && context != null)
            {
                var project = await context.GetActiveProjectAsync(ct);
                var dir = project != null ? Path.GetDirectoryName(project.Path) : null;
                if (!string.IsNullOrEmpty(dir))
                {
                    foreach (var file in EnumerateSource(dir))
                    {
                        // Prefer the live buffer for the active doc; disk for the rest.
                        string text =
                            (activeView?.FilePath != null &&
                             string.Equals(file, activeView.FilePath, StringComparison.OrdinalIgnoreCase))
                                ? activeView.Document.Text.CopyToString()
                                : await File.ReadAllTextAsync(file, ct);
                        pieces.Add(new ContextPiece { Path = file, Content = text });
                    }
                }
            }

            // Open tabs (best-effort; active file only until a stable enumeration API is wired)
            if (scope.HasFlag(ContextScope.OpenTabs) && activeView?.FilePath != null)
            {
                if (!pieces.Any(p => !p.IsSelection &&
                    string.Equals(p.Path, activeView.FilePath, StringComparison.OrdinalIgnoreCase)))
                {
                    pieces.Add(new ContextPiece
                    {
                        Path = activeView.FilePath,
                        Content = activeView.Document.Text.CopyToString()
                    });
                }
            }

            // De-dup identical non-selection (path, content) pairs.
            var selections = pieces.Where(p => p.IsSelection).ToList();
            var others = pieces.Where(p => !p.IsSelection)
                .GroupBy(p => (p.Path, p.Content))
                .Select(g => g.First());
            return selections.Concat(others).ToList();
        }

        public static int EstimateChars(IEnumerable<ContextPiece> pieces) =>
            pieces.Sum(p => p.Length + 40);

        public static int EstimateTokens(int chars) => (int)Math.Ceiling(chars / 4.0);

        public string BuildPrompt(IEnumerable<ContextPiece> pieces, string userQuestion)
        {
            var sb = new StringBuilder();
            foreach (var p in pieces)
            {
                var label = p.IsSelection ? $"Selected snippet from {p.Path}" : $"Contents of {p.Path}";
                sb.Append(label).Append(":\n").Append(p.Content).Append('\n');
            }

            sb.Append("\r\n\r\n");
            sb.Append(string.IsNullOrWhiteSpace(userQuestion)
                ? "What is wrong with this code? If you see anything, reply only with corrections."
                : userQuestion);
            sb.Append("\r\n\r\n").Append(CodeReplyConvention);
            return sb.ToString();
        }

        public async Task<string> BuildPromptAsync(
            IClientContext context, ContextScope scope, string userQuestion, CancellationToken ct)
        {
            var pieces = await GatherAsync(context, scope, ct);
            return BuildPrompt(pieces, userQuestion);
        }

        private static IEnumerable<string> EnumerateSource(string dir) =>
            Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(dir, "*.vb", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }
}