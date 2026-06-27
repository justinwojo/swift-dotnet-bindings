// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;

namespace BindingsGeneration;

/// <summary>
/// Resolves the wall-clock timeouts for the two long-running external steps — the
/// SwiftInterfaceParser host binary and the swiftc wrapper compile — so a slow, contended CI
/// runner can be given more headroom without cutting an SDK release. Precedence is: an explicit
/// override (threaded from an MSBuild property) &gt; the matching environment variable (emergency
/// override) &gt; a raised built-in default. Values are whole seconds, clamped to a sane window so
/// a typo can neither disable the safety net (too small) nor wedge a build forever (too large).
/// A finite timeout always remains — there is no "infinite" setting, so a genuine hang still dies.
/// </summary>
internal static class GeneratorTimeouts
{
    internal const int MinSeconds = 30;
    internal const int MaxSeconds = 3600; // 1-hour outer bound — a true hang still terminates

    // Raised from the historical 60s/120s. The old values turned a transient contention slowdown
    // on a small shared runner (a wrapper compile racing a booted simulator for ~3 vCPUs) into a
    // hard, opaque release failure rather than a slow-but-correct build.
    internal const int DefaultParserSeconds = 300;   // was 60
    internal const int DefaultSwiftcSeconds = 600;   // was 120

    internal const string ParserEnvVar = "SWIFTBINDINGS_PARSER_TIMEOUT_SECONDS";
    internal const string SwiftcEnvVar = "SWIFTBINDINGS_SWIFTC_TIMEOUT_SECONDS";

    /// <summary>Resolved SwiftInterfaceParser timeout.</summary>
    internal static TimeSpan ResolveParserTimeout(int? overrideSeconds = null)
        => TimeSpan.FromSeconds(Resolve(overrideSeconds, ParserEnvVar, DefaultParserSeconds));

    /// <summary>Resolved swiftc wrapper-compile timeout, in milliseconds (what ICommandRunner takes).</summary>
    internal static int ResolveSwiftcTimeoutMs(int? overrideSeconds = null)
        => Resolve(overrideSeconds, SwiftcEnvVar, DefaultSwiftcSeconds) * 1000;

    private static int Resolve(int? overrideSeconds, string envVar, int defaultSeconds)
    {
        if (overrideSeconds is int cli && cli > 0)
            return Clamp(cli);
        var env = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env.Trim(), out var parsed) && parsed > 0)
            return Clamp(parsed);
        return Clamp(defaultSeconds);
    }

    private static int Clamp(int seconds)
        => seconds < MinSeconds ? MinSeconds : (seconds > MaxSeconds ? MaxSeconds : seconds);
}
