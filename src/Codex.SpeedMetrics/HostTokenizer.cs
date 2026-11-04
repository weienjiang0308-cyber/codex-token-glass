using System;
using System.Collections.Generic;
using SharpToken;

namespace Codex.SpeedMetrics;

// This small adapter is compiled in EACH host's target framework. SharpToken's net6 and net8
// assets expose different signatures, so calling it from a cross-target library breaks at runtime.
public static class HostTokenizer
{
    private static readonly Lazy<GptEncoding> Encoding = new(() => GptEncoding.GetEncoding("o200k_base"));
    private static readonly HashSet<string> NoSpecialTokens = new();
    public static void Initialize() => VisibleTextWindow.TokenCounter = text =>
        Encoding.Value.CountTokens(text, NoSpecialTokens, NoSpecialTokens);
}
