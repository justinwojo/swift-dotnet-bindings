// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Gate-hygiene invariant (REMEDIATION-PLAN Session 10 / Track-M4): no BindingTests
/// <c>RuntimeTestsApp</c> test method may be declared <c>async void</c>. The compile-time test
/// discovery (<c>TestDiscoveryGenerator</c>) drives each test through an invoker that cannot await
/// a <c>void</c>-returning method, so an <c>async void</c> body returns before completion — every
/// post-<c>await</c> assertion and exception is detached and the harness reports a false PASS.
///
/// The generator itself fails the BindingTests build on this via diagnostic <c>SBTD001</c>; this
/// unit test is the complementary guard at the <c>nuke test</c> layer (which does not build the
/// iOS app), so the footgun is caught even when only unit tests run.
/// </summary>
public class AsyncVoidTestMethodTests
{
    // `public async void TestSomething(` — the detaching shape. Allows any modifier ordering
    // before `async` and any whitespace, and only matches discovered test methods (name "Test…").
    private static readonly Regex AsyncVoidTest =
        new(@"\basync\s+void\s+(Test\w*)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void NoRuntimeTestMethodIsAsyncVoid()
    {
        var repoRoot = LocateRepoRoot();
        var runtimeTestsDir = Path.Combine(repoRoot, "BindingTests", "RuntimeTestsApp");
        Assert.True(Directory.Exists(runtimeTestsDir), $"RuntimeTestsApp not found at {runtimeTestsDir}");

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(runtimeTestsDir, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in AsyncVoidTest.Matches(text))
            {
                var rel = Path.GetRelativePath(repoRoot, file);
                violations.Add($"{rel}: '{m.Groups[1].Value}' is declared 'async void'");
            }
        }

        Assert.True(violations.Count == 0,
            "Test methods must be 'async Task' (or plain sync), never 'async void' — an async-void " +
            "test detaches its assertions/exceptions and falsely passes (see SBTD001):\n  " +
            string.Join("\n  ", violations));
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
