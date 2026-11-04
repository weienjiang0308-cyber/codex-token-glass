using System;
using System.Collections.Generic;
using SharpToken;
using System.Text.RegularExpressions;

namespace Codex.SpeedMetrics;

// This small adapter is compiled in EACH host's target framework. SharpToken's net6 and net8
// assets expose different signatures, so calling it from a cross-target library breaks at runtime.
public static class HostTokenizer
{
    private static readonly Lazy<GptEncoding> Encoding = new(() => GptEncoding.GetEncoding("o200k_base"));
    private static readonly HashSet<string> NoSpecialTokens = new();

    // o200k_base pre-tokenization, from OpenAI tiktoken (MIT). Keep the final two
    // regex pieces pending: appending text can change the unfinished word or space.
    private static readonly Regex Pieces = new(
        @"[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]*[\p{Ll}\p{Lm}\p{Lo}\p{M}]+(?i:'s|'t|'re|'ve|'m|'ll|'d)?" +
        @"|[^\r\n\p{L}\p{N}]?[\p{Lu}\p{Lt}\p{Lm}\p{Lo}\p{M}]+[\p{Ll}\p{Lm}\p{Lo}\p{M}]*(?i:'s|'t|'re|'ve|'m|'ll|'d)?" +
        @"|\p{N}{1,3}| ?[^\s\p{L}\p{N}]+[\r\n/]*|\s*[\r\n]+|\s+(?!\S)|\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static void Initialize()
    {
        VisibleTextWindow.TokenCounter = text => Encoding.Value.CountTokens(text, NoSpecialTokens, NoSpecialTokens);
        VisibleTextWindow.StablePrefixLength = text =>
        {
            try { var pieces = Pieces.Matches(text); return pieces.Count <= 2 ? 0 : pieces[pieces.Count - 2].Index; }
            catch (RegexMatchTimeoutException) { return 0; }
        };
    }
}
