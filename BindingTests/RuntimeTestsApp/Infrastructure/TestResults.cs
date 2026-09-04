// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Self-contained nullable context so this file compiles identically whether built in a runtime-test
// host (Nullable=enable) or link-compiled into the unit-test project (Nullable=disable +
// warnings-as-errors), where the string?/StreamWriter? annotations would otherwise raise CS8632.
// It is link-compiled there so the run-token the writer stamps is checked against the harness-side
// reader (JsonlTestResults) in one round-trip test rather than two hand-copied string literals.
#nullable enable

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// Tracks test results for summary reporting.
/// Optionally writes crash-safe JSONL output (one JSON object per line, flushed after each test).
/// </summary>
public class TestResults
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Skipped { get; private set; }
    public int Warnings { get; private set; }
    public List<string> FailedTests { get; } = new();
    public List<string> SkippedTests { get; } = new();
    public Dictionary<string, TimeSpan> TestDurations { get; } = new();

    private readonly object _lock = new();

    // JSONL output for crash-safe structured results
    private StreamWriter? _jsonlWriter;
    private string? _currentClassName;
    private int _currentClassTestCount;

    /// <summary>
    /// Initializes JSONL output to the specified file path.
    /// The file is created/overwritten and each test result is appended + flushed.
    /// </summary>
    /// <param name="filePath">Destination JSONL path.</param>
    /// <param name="runToken">
    /// Per-launch identity token from the harness (<c>--run-token</c>). When present it is written
    /// and flushed as the FIRST line of the file, before any test result, so that a file recovered
    /// from the app sandbox carries proof of which launch produced it. See
    /// <see cref="TestRunFlags.RunToken"/> for why that proof is required.
    /// </param>
    public void InitializeJsonl(string filePath, string? runToken = null)
    {
        lock (_lock)
        {
            _jsonlWriter = new StreamWriter(filePath, append: false) { AutoFlush = false };

            // Written first and flushed immediately so the token survives a crash at any later
            // point — a partial JSONL from a genuinely-crashed run must still be attributable to
            // this launch, otherwise crash recovery would lose its results. Omitted entirely when
            // no token was supplied (hand-launched app); the harness treats a token-less file as
            // unusable rather than trusting it.
            if (!string.IsNullOrEmpty(runToken))
                WriteJsonlRaw($"{{\"run_token\":{JsonEscape(runToken)}}}");
        }
    }

    /// <summary>
    /// Sets the current class being tested. Call before running each class's tests.
    /// </summary>
    public void BeginClass(string className)
    {
        lock (_lock)
        {
            // Emit class_done for previous class if there was one
            if (_currentClassName != null)
                WriteClassDone();

            _currentClassName = className;
            _currentClassTestCount = 0;
        }
    }

    /// <summary>
    /// Emits a class_done record for the current class. Called automatically by BeginClass
    /// for the previous class, and must be called after the last class finishes.
    /// </summary>
    public void EndClass()
    {
        lock (_lock)
        {
            if (_currentClassName != null)
            {
                WriteClassDone();
                _currentClassName = null;
            }
        }
    }

    /// <summary>
    /// Writes the final summary record and flushes. Call after all tests complete.
    /// Returns the path to the JSONL file, or null if JSONL was not initialized.
    /// </summary>
    public void FinalizeJsonl()
    {
        lock (_lock)
        {
            // Ensure last class is closed
            if (_currentClassName != null)
                WriteClassDone();
            _currentClassName = null;

            if (_jsonlWriter != null)
            {
                WriteJsonlRaw($"{{\"done\":true,\"total\":{Total},\"passed\":{Passed},\"failed\":{Failed},\"skipped\":{Skipped}}}");
                _jsonlWriter.Flush();
                _jsonlWriter.Dispose();
                _jsonlWriter = null;
            }
        }
    }

    private void WriteClassDone()
    {
        WriteJsonlRaw($"{{\"class_done\":{JsonEscape(_currentClassName)},\"tests_run\":{_currentClassTestCount}}}");
    }

    /// <summary>
    /// Writes a raw JSON string as a JSONL line. Uses manual string building instead of
    /// JsonSerializer to avoid NativeAOT incompatibility (JsonSerializer.Serialize with
    /// anonymous types requires runtime code generation which IL3050 prohibits).
    /// </summary>
    private void WriteJsonlRaw(string json)
    {
        if (_jsonlWriter == null) return;
        try
        {
            _jsonlWriter.WriteLine(json);
            _jsonlWriter.Flush();
        }
        catch
        {
            // Best-effort: don't let JSONL failures crash the test runner
        }
    }

    /// <summary>
    /// JSON-escapes a string value (wraps in quotes, escapes special chars).
    /// Handles all control characters below U+0020 per JSON spec (RFC 8259 §7).
    /// </summary>
    private static string JsonEscape(string? value)
    {
        if (value == null) return "null";
        var sb = new System.Text.StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < '\u0020')
                        sb.Append($"\\u{(int)c:X4}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Extracts the class name and method name from a fully-qualified test name (ClassName.MethodName).
    /// </summary>
    private static (string className, string methodName) SplitTestName(string testName)
    {
        var dotIndex = testName.IndexOf('.');
        if (dotIndex > 0 && dotIndex < testName.Length - 1)
            return (testName[..dotIndex], testName[(dotIndex + 1)..]);
        return ("", testName);
    }

    public void Pass(string testName, TimeSpan? duration = null)
    {
        lock (_lock)
        {
            Passed++;
            _currentClassTestCount++;
            if (duration.HasValue)
            {
                TestDurations[testName] = duration.Value;
            }
            TestLogger.Success($"{testName}" + (duration.HasValue ? $" ({duration.Value.TotalMilliseconds:F0}ms)" : ""));

            var (className, methodName) = SplitTestName(testName);
            var ms = duration.HasValue ? (int)duration.Value.TotalMilliseconds : 0;
            WriteJsonlRaw($"{{\"class\":{JsonEscape(className)},\"test\":{JsonEscape(methodName)},\"status\":\"pass\",\"ms\":{ms}}}");
        }
    }

    public void Fail(string testName, string reason = "", TimeSpan? duration = null)
    {
        lock (_lock)
        {
            Failed++;
            _currentClassTestCount++;
            var msg = string.IsNullOrEmpty(reason) ? testName : $"{testName}: {reason}";
            FailedTests.Add(msg);
            if (duration.HasValue)
            {
                TestDurations[testName] = duration.Value;
            }
            TestLogger.Error(msg + (duration.HasValue ? $" ({duration.Value.TotalMilliseconds:F0}ms)" : ""));

            var (className, methodName) = SplitTestName(testName);
            var ms = duration.HasValue ? (int)duration.Value.TotalMilliseconds : 0;
            WriteJsonlRaw($"{{\"class\":{JsonEscape(className)},\"test\":{JsonEscape(methodName)},\"status\":\"fail\",\"error\":{JsonEscape(reason)},\"ms\":{ms}}}");
        }
    }

    public void Skip(string testName, string reason = "")
    {
        lock (_lock)
        {
            Skipped++;
            _currentClassTestCount++;
            var msg = string.IsNullOrEmpty(reason) ? testName : $"{testName}: {reason}";
            SkippedTests.Add(msg);
            TestLogger.Warning($"SKIP: {msg}");

            var (className, methodName) = SplitTestName(testName);
            WriteJsonlRaw($"{{\"class\":{JsonEscape(className)},\"test\":{JsonEscape(methodName)},\"status\":\"skip\",\"reason\":{JsonEscape(reason)}}}");
        }
    }

    public void Warn(string message)
    {
        lock (_lock)
        {
            Warnings++;
            TestLogger.Warning(message);
        }
    }

    public bool AllPassed => Failed == 0;

    public int Total => Passed + Failed + Skipped;

    public TimeSpan TotalDuration => TestDurations.Values.Aggregate(TimeSpan.Zero, (a, b) => a + b);

    public override string ToString()
    {
        var status = AllPassed ? "ALL TESTS PASSED" : "SOME TESTS FAILED";
        var parts = new List<string> { $"{Passed} passed" };
        if (Failed > 0) parts.Add($"{Failed} failed");
        if (Skipped > 0) parts.Add($"{Skipped} skipped");
        if (Warnings > 0) parts.Add($"{Warnings} warnings");
        return $"{status}: {string.Join(", ", parts)}";
    }
}

/// <summary>
/// Target platform for test execution. This is the <b>build flavor</b> the harness launched, not a
/// guess about the live runtime — runtime-detected classification lives in <c>TestBase</c>
/// (<c>IsMonoJitRuntime</c>, <c>IsMonoAotRuntime</c>).
/// </summary>
public enum TestPlatform
{
    /// <summary>iOS Simulator (Mono JIT).</summary>
    Simulator,

    /// <summary>
    /// Physical device, NativeAOT (<c>dotnet publish -c Release -r ios-arm64 -p:PublishAot=true</c>).
    /// The CLI-flag-keyed <see cref="SkipOnDeviceAttribute"/> is scoped to THIS value only: its
    /// skips are about the NativeAOT Release app, not about being on a phone.
    /// </summary>
    Device,

    /// <summary>
    /// Physical device, Mono full-AOT (<c>dotnet build -c Debug -r ios-arm64</c>, no PublishAot) —
    /// the .NET-for-iOS default device runtime, and the one a MAUI app ships on unless its author
    /// opts into PublishAot. Distinct from both siblings: it is Mono (so the runtime-detected
    /// Mono skips apply, exactly as they do on the Simulator) but it is AOT-compiled and trimmed
    /// (so AOT reflection/trimming behavior applies, as under NativeAOT), and it is a Debug build
    /// (so <c>[Conditional("DEBUG")]</c> code is live, unlike the NativeAOT Release app).
    /// Selected by <c>nuke binding-tests --device --mono-aot</c>.
    /// </summary>
    DeviceMonoAot
}


/// <summary>
/// Marks tests that are broken everywhere (generator bugs, missing entry points).
/// Always skipped. The reason is visible in test output.
///
/// The reason MUST be one of:
/// - A specific generator bug description (e.g., "UniqueResource is ~Copyable: @_cdecl wrapper needs move semantics")
/// - A reference to a RuntimeLimitations.Limitation that affects both runtimes
///
/// Do NOT use vague runtime blame like "Mono JIT crash" or "NativeAOT issue".
/// See RuntimeLimitations registry (Swift.RuntimeLimitations) for all known upstream bugs.
/// If a crash doesn't match a registered limitation, it is a generator bug.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class SkipAttribute : Attribute
{
    public string Reason { get; }

    public SkipAttribute(string reason)
    {
        Reason = reason;
    }
}

/// <summary>
/// Marks tests that crash on simulator (Mono JIT) but work on device (NativeAOT).
/// Skipped on simulator, runs on device. The reason is visible in test output.
///
/// The reason MUST reference either:
/// - A Mono-specific RuntimeLimitations.Limitation (MonoCallConvSwiftJitAssertion,
///   MonoSetInsertDoneBlocking, MonoAsyncSafeHandleLifetime, or
///   NonBlittableCallConvSwiftRejection)
/// - A specific generator bug that only manifests on Mono (prefixed with "Generator bug:")
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class SkipOnSimulatorAttribute : Attribute
{
    public string Reason { get; }

    public SkipOnSimulatorAttribute(string reason)
    {
        Reason = reason;
    }
}

/// <summary>
/// Marks tests that crash on device (NativeAOT) but work on simulator (Mono).
/// Skipped on device, runs on simulator. The reason is visible in test output.
///
/// The reason MUST reference either:
/// - A NativeAOT-applicable RuntimeLimitations.Limitation (currently only
///   NonBlittableCallConvSwiftRejection — the registry has no NativeAOT-only entries)
/// - A specific generator bug that only manifests on NativeAOT (prefixed with "Generator bug:")
///
/// Scoped to <see cref="TestPlatform.Device"/> only — NOT to the Mono full-AOT device lane
/// (<see cref="TestPlatform.DeviceMonoAot"/>), which is also a phone but a different runtime and a
/// Debug build. Every reason this attribute admits is a NativeAOT (or NativeAOT-Release) property,
/// so applying it there would blanket-suppress tests that have no reason to fail. A test that fails
/// only under device Mono full-AOT is a bug to fix, not a skip to widen.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class SkipOnDeviceAttribute : Attribute
{
    public string Reason { get; }

    public SkipOnDeviceAttribute(string reason)
    {
        Reason = reason;
    }
}

/// <summary>
/// Marks tests that crash on Mac Catalyst x86_64 (Rosetta on Apple Silicon) but
/// work on Mac Catalyst arm64, macOS x86_64, and iOS Simulator arm64. Skipped on
/// maccatalyst-x64 only — runs on every other RID, including osx-x64 under the
/// same Rosetta layer. The reason is visible in test output.
///
/// The reason MUST describe the specific deterministic crash, referencing the
/// upstream filing for Issue 4 (Mono Catalyst x64 instability).
///
/// Detected at runtime via <see cref="OperatingSystem.IsMacCatalyst"/> +
/// <see cref="System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture"/>;
/// no enum/CLI-flag plumbing required, so the attribute is a strict superset of the
/// previous skip surface.
///
/// Method-level only. Unlike the CLI-flag-keyed <see cref="SkipOnSimulatorAttribute"/> /
/// <see cref="SkipOnDeviceAttribute"/> (which <c>TestDiscoveryGenerator</c> also reads at
/// class scope), this runtime-detected skip is honored only per-method in
/// <c>TestBase.RunAllTestsAsync</c>; a class-level annotation would be silently ignored and
/// the class would still run. Restricting the target to <see cref="AttributeTargets.Method"/>
/// makes that a compile error instead of a footgun.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class SkipOnCatalystX64Attribute : Attribute
{
    public string Reason { get; }

    public SkipOnCatalystX64Attribute(string reason)
    {
        Reason = reason;
    }
}

/// <summary>
/// Marks tests that crash specifically under the <b>Mono</b> runtime (the upstream
/// <c>!ji-&gt;async</c> JIT assertion at <c>jit-info.c:918</c>, a.k.a. "Issue 1", fired during a
/// signal-handler unwind through a CallConvSwift frame). Skipped wherever the process runs on
/// Mono — iOS/tvOS Simulator, Mac Catalyst, <b>and</b> the Mono full-AOT device lane
/// (<c>--device --mono-aot</c>), which is Mono on a phone and so satisfies the same runtime
/// predicate. It runs everywhere else: macOS (CoreCLR) and the NativeAOT device lane, neither of
/// which can hit a Mono assertion. Note the asymmetry with the CLI-flag-keyed
/// <see cref="SkipOnDeviceAttribute"/>, which the Mono full-AOT device lane does NOT honor: that
/// one names the Release/NativeAOT app, this one names the runtime.
///
/// This is distinct from <see cref="SkipOnSimulatorAttribute"/>, which is keyed off the
/// <c>--platform simulator</c> CLI flag. The harness passes <c>--platform simulator</c> for the
/// macOS and Catalyst runs too (they share the "no native runtime dylib" property), so a plain
/// [SkipOnSimulator] would <em>also</em> suppress the test on macOS — where it provably cannot
/// crash because CoreCLR is not Mono. To avoid that false suppression, a Mono-JIT skip is
/// detected at <b>runtime</b> (see <c>TestBase.IsMonoJitRuntime</c>) rather than from the CLI
/// flag, mirroring the existing <see cref="SkipOnCatalystX64Attribute"/> precedent. Note that
/// <c>SwiftRuntimeInfo.IsMonoRuntime</c> alone is insufficient: its RID check only recognizes
/// "simulator" RIDs and misses <c>maccatalyst-*</c>, so the predicate must also consult
/// <see cref="OperatingSystem.IsMacCatalyst"/>.
///
/// The reason MUST name the specific CallConvSwift entry-point symbol (<c>$s…</c>) on the test's
/// own path — enforced by <c>Issue1SkipAttributionTests</c> in the generator unit tests.
///
/// Method-level only (same rationale as <see cref="SkipOnCatalystX64Attribute"/>): this skip is
/// runtime-detected and honored per-method in <c>TestBase.RunAllTestsAsync</c>. Class-level skips
/// are wired only for the CLI-flag-keyed <see cref="SkipOnSimulatorAttribute"/> /
/// <see cref="SkipOnDeviceAttribute"/> (read at class scope by <c>TestDiscoveryGenerator</c>), so a
/// class-level <c>[SkipOnMonoJit]</c> would be silently ignored and the class would still run on
/// Mono — the exact crash this attribute exists to prevent. <see cref="AttributeTargets.Method"/>
/// makes the unsupported usage a compile error.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class SkipOnMonoJitAttribute : Attribute
{
    public string Reason { get; }

    public SkipOnMonoJitAttribute(string reason)
    {
        Reason = reason;
    }
}

/// <summary>
/// Marks stress/slow tests. Always runs but can be filtered if needed.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class SlowAttribute : Attribute { }
