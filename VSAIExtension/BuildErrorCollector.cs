using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DCAAIExtension
{
    /// <summary>
    /// Collects compiler errors/warnings by invoking `dotnet build` and parsing MSBuild/Roslyn
    /// diagnostic lines. VisualStudio.Extensibility 17.9 doesn't expose the live Error List
    /// out-of-proc, so this builds the project on disk: it reflects SAVED files and reports
    /// build (not IntelliSense-only) diagnostics.
    /// </summary>
    public static class BuildErrorCollector
    {
        private static readonly Regex DiagLine = new(
            @"^.*?:\s*(error|warning)\s+[A-Za-z]+\d+:.*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

        public static async Task<string> CollectAsync(
            string projectPath, CancellationToken ct, int maxDiagnostics = 60)
        {
            if (string.IsNullOrWhiteSpace(projectPath)) return "";

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"build \"{projectPath}\" -nologo --no-restore -clp:NoSummary -v q",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                using var proc = Process.Start(psi);
                if (proc == null) return "";

                // Read both streams concurrently to avoid a buffer-fill deadlock.
                var outTask = proc.StandardOutput.ReadToEndAsync(ct);
                var errTask = proc.StandardError.ReadToEndAsync(ct);
                await Task.WhenAll(outTask, errTask);
                await proc.WaitForExitAsync(ct);

                var combined = outTask.Result + "\n" + errTask.Result;
                var seen = new HashSet<string>();
                var sb = new StringBuilder();
                int count = 0;
                foreach (Match m in DiagLine.Matches(combined))
                {
                    var line = m.Value.Trim();
                    if (seen.Add(line))
                    {
                        sb.AppendLine(line);
                        if (++count >= maxDiagnostics) { sb.AppendLine("... (truncated)"); break; }
                    }
                }
                return sb.ToString().TrimEnd();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return "(could not collect build errors: " + ex.Message + ")";
            }
        }
    }
}