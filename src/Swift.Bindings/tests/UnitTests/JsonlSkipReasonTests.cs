// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

public class JsonlSkipReasonTests
{
    [Theory]
    [InlineData("TestBundle01_LifetimeStress_EqualsAndGenericEnumExtractor", "SafeHandle/refcount lifetime stress requires --lifetime")]
    [InlineData("TestBundleB_ClosureLifetime_EphemeralRepeatCall", "Closure lifetime ephemeral stress requires --lifetime")]
    [InlineData("TestBundleB_ClosureLifetime_HeapAllocShapeMatrix", "Heap-alloc shape matrix requires --lifetime")]
    [InlineData("TestBundleB_ClosureLifetime_PropertySetterReplace", "Property-setter replace requires --lifetime")]
    [InlineData("TestBundleB_ClosureLifetime_GCPressureDuringCall", "GC-pressure-during-call requires --lifetime")]
    public void ActualAppWriter_SkipReasonSurvivesParsingAndIdentityProjection(string method, string reason)
    {
        var path = Path.Combine(Path.GetTempPath(), $"skip-reason-{Guid.NewGuid():N}.jsonl");
        var writer = new RuntimeTestsApp.Infrastructure.TestResults();
        try
        {
            writer.InitializeJsonl(path, "skip-reason-roundtrip");
            writer.BeginClass("OwnershipGCStressTests");
            writer.Skip($"OwnershipGCStressTests.{method}", reason);
            writer.FinalizeJsonl();

            var parsed = JsonlTestResults.Parse(File.ReadAllText(path));
            var test = Assert.Single(parsed.Tests);
            Assert.Equal("skip", test.Status);
            Assert.Equal(reason, test.Error);
            Assert.Equal(1, parsed.SkipCount);
            Assert.True(parsed.Done);
            Assert.True(parsed.MatchesRunToken("skip-reason-roundtrip"));

            var current = parsed.Tests.Select(t => new RuntimeIdentityBaseline.TestRecord(
                t.ClassName, t.TestName, t.Status, t.Error ?? "")).ToArray();
            Assert.Equal(reason, Assert.Single(RuntimeIdentityBaseline.FromResults(current).Skips).Reason);
            var baseline = new RuntimeIdentityBaseline().WithPlatform("device",
                RuntimeIdentityBaseline.FromResults(new[]
                {
                    new RuntimeIdentityBaseline.TestRecord("OwnershipGCStressTests", method, "skip", "")
                }));
            var (regressions, improvements) = baseline.Compare("device", current);
            Assert.Empty(regressions); // Reason text is not part of the status identity.
            Assert.Empty(improvements);
        }
        finally
        {
            writer.FinalizeJsonl();
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("skip", "\"error\":\"legacy\"", "legacy")]
    [InlineData("skip", "\"reason\":\"current\",\"error\":\"legacy\"", "current")]
    [InlineData("skip", "\"reason\":null,\"error\":\"legacy\"", "legacy")]
    [InlineData("skip", "\"reason\":73,\"error\":\"legacy\"", "legacy")]
    [InlineData("skip", "\"reason\":\"\",\"error\":\"legacy\"", "")]
    [InlineData("fail", "\"reason\":\"skip-only\",\"error\":\"failure\"", "failure")]
    [InlineData("fail", "\"reason\":\"skip-only\"", null)]
    public void SkipReasonCompatibility_PreservesLegacyErrorAndFailureMeaning(
        string status, string fields, string? expected)
    {
        var jsonl = $"{{\"class\":\"ExampleTests\",\"test\":\"TestValue\",\"status\":\"{status}\",{fields}}}";
        Assert.Equal(expected, Assert.Single(JsonlTestResults.Parse(jsonl).Tests).Error);
    }
}
