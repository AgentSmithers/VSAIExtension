using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace DCAAIExtension
{
    /// <summary>
    /// Applies a proposed edit in place (focus-independent, via the document snapshot) so the
    /// user reviews it in their real file, then asks for keep/revert approval. Approval is
    /// delegated to an injected <c>approver</c> (e.g. Accept/Reject buttons in a tool window);
    /// when none is supplied it falls back to an OK/Cancel popup.
    /// </summary>
    public sealed class DiffService
    {
        private readonly VisualStudioExtensibility _ext;
        private readonly Func<string, CancellationToken, Task<bool>> _approver;

        public DiffService(
            VisualStudioExtensibility extensibility,
            Func<string, CancellationToken, Task<bool>> approver = null)
        {
            _ext = extensibility;
            _approver = approver;
        }

        public Task<bool> ShowDiffAndApplyAsync(
            string filePath, string proposedContent, bool requireApproval, CancellationToken ct)
            => ShowDiffAndApplyCoreAsync(filePath, proposedContent, requireApproval, null, ct);

        public Task<bool> ShowDiffAndApplyAsync(
            string filePath, string proposedContent, bool requireApproval,
            ContextPiece selection, CancellationToken ct)
            => ShowDiffAndApplyCoreAsync(filePath, proposedContent, requireApproval, selection, ct);

        private async Task<bool> ShowDiffAndApplyCoreAsync(
            string filePath, string proposedContent, bool requireApproval,
            ContextPiece selection, CancellationToken ct)
        {
            var snapshot = await TryGetTextDocumentAsync(filePath, ct);

            if (snapshot == null)
            {
                if (selection != null && selection.IsSelection) return false;
                if (!requireApproval ||
                    await AskApprovalAsync($"Apply AI changes to {Path.GetFileName(filePath)}?", ct))
                {
                    await File.WriteAllTextAsync(filePath, proposedContent, ct);
                    return true;
                }
                return false;
            }

            string docText = snapshot.Text.CopyToString();
            int len = docText.Length;
            string content = NormalizeNewlines(proposedContent, docText);

            int start, length;
            if (selection != null && selection.IsSelection)
            {
                start = Math.Clamp(selection.SelectionStart, 0, len);
                int end = Math.Clamp(selection.SelectionEnd, start, len);
                length = end - start;
            }
            else
            {
                start = 0;
                length = len;
            }

            string originalText = docText.Substring(start, length);

            // Apply in place so the user reviews the change in their real file.
            if (!await ReplaceRangeAsync(filePath, start, length, content, ct))
                return false;

            if (!requireApproval) return true;

            var label = selection != null && selection.IsSelection
                ? $"Keep AI changes to the SELECTION in {Path.GetFileName(filePath)}?"
                : $"Keep AI changes to {Path.GetFileName(filePath)}?";

            if (await AskApprovalAsync(label, ct)) return true;

            // Reject -> revert. Use None so the undo still completes if the op was cancelled.
            await ReplaceRangeAsync(filePath, start, content.Length, originalText, CancellationToken.None);
            return false;
        }

        private Task<bool> AskApprovalAsync(string label, CancellationToken ct) =>
            _approver != null ? _approver(label, ct) : ShowPopupApprovalAsync(label, ct);

        private async Task<bool> ShowPopupApprovalAsync(string label, CancellationToken ct) =>
            await _ext.Shell().ShowPromptAsync(label, PromptOptions.OKCancel, ct) == true;

        private async Task<ITextDocumentSnapshot> TryGetTextDocumentAsync(string filePath, CancellationToken ct)
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                var doc = await _ext.Documents().OpenDocumentAsync(new Uri(filePath, UriKind.Absolute), ct);
                return await doc.AsTextDocumentAsync(_ext, ct);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Open text document failed: " + ex.Message);
                return null;
            }
        }

        private async Task<bool> ReplaceRangeAsync(
            string filePath, int start, int length, string content, CancellationToken ct)
        {
            var snapshot = await TryGetTextDocumentAsync(filePath, ct);
            if (snapshot == null)
            {
                try { await File.WriteAllTextAsync(filePath, content, ct); return true; }
                catch { return false; }
            }

            try
            {
                int len = snapshot.Text.Length;
                int s = Math.Clamp(start, 0, len);
                int l = Math.Clamp(length, 0, len - s);
                await _ext.Editor().EditAsync(batch =>
                {
                    var editable = snapshot.AsEditable(batch);
                    editable.Replace(new TextRange(snapshot, s, l), content);
                }, ct);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Replace range failed: " + ex.Message);
                return false;
            }
        }

        private static string NormalizeNewlines(string text, string sample)
        {
            var lf = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return sample.Contains("\r\n") ? lf.Replace("\n", "\r\n") : lf;
        }
    }
}