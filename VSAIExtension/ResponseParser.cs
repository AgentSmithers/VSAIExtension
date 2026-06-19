using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DCAAIExtension
{
    public sealed class ReturnedFile
    {
        public string Path { get; init; }
        public string Content { get; init; }
        /// <summary>True when the block had no path= tag (treat as selection replacement).</summary>
        public bool IsUntagged { get; init; }
    }

    /// <summary>
    /// Parses model output for fenced code blocks, optionally tagged with a path
    /// (```path=Foo.cs ... ```), and decides whether a response looks like code.
    /// </summary>
    public static class ResponseParser
    {
        private static readonly Regex PathFence = new(
            @"```(?:[a-zA-Z0-9#+]*\s+)?path=(?<path>[^\r\n`]+)\r?\n(?<body>.*?)```",
            RegexOptions.Singleline | RegexOptions.Compiled);

        // First fenced block of any kind (used for untagged/selection replies).
        private static readonly Regex FirstFence = new(
            @"```[a-zA-Z0-9#+]*\r?\n(?<body>.*?)```",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex AnyFence = new(
            @"```.*?```", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>Path-tagged files (multi-file, whole-file replacements).</summary>
        public static List<ReturnedFile> ParsePathFencedFiles(string response)
        {
            var result = new List<ReturnedFile>();
            if (string.IsNullOrEmpty(response)) return result;

            foreach (Match m in PathFence.Matches(response))
            {
                result.Add(new ReturnedFile
                {
                    Path = m.Groups["path"].Value.Trim(),
                    Content = m.Groups["body"].Value.TrimEnd('\r', '\n'),
                    IsUntagged = false,
                });
            }
            return result;
        }

        /// <summary>The first untagged fenced block, if any (selection replacement).</summary>
        public static ReturnedFile ParseFirstUntaggedBlock(string response)
        {
            if (string.IsNullOrEmpty(response)) return null;
            if (PathFence.IsMatch(response)) return null; // path-tagged takes precedence
            var m = FirstFence.Match(response);
            if (!m.Success) return null;
            return new ReturnedFile
            {
                Path = null,
                Content = m.Groups["body"].Value.TrimEnd('\r', '\n'),
                IsUntagged = true,
            };
        }

        public static bool LooksLikeCode(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return false;
            if (response.Trim().Equals("ok!", StringComparison.OrdinalIgnoreCase)) return false;
            if (PathFence.IsMatch(response)) return true;
            if (FirstFence.IsMatch(response)) return true;

            int fencedChars = 0;
            foreach (Match m in AnyFence.Matches(response)) fencedChars += m.Length;
            return fencedChars > 0 && fencedChars >= response.Length * 0.4;
        }
    }
}