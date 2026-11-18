// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Swift.Runtime;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// R4's guarded acceptance (<see cref="ResidualGuardCheck.AcceptedReverseCallbacks"/>): a
/// Swift-convention reverse callback passes the engine and both packaged checks (SWIFTBIND133 through
/// <see cref="PackageTransitionEvidence.Check"/>, SWIFTBIND132 through <see cref="ConsumerTransitionCheck.Check"/>)
/// only when its address reaches Swift solely through members guarded for
/// <see cref="SwiftTransitionShapes.ReverseCallback"/>. Every other way to reach it is a negative control
/// that keeps R4 red in all three.
/// </summary>
public class ReverseCallbackAcceptanceTests
{
    private const string CallbackMember = "Factory::Body_Callback";

    private const string Guard = "SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)0x10000, \"Factory.Make()\");";

    /// <summary>The closure emitters' legacy shape: a private callback, its address in a private static field.</summary>
    private const string Callback = """
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static unsafe void Body_Callback(void* arg0, SwiftSelf context) { }
            private static unsafe readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_Body_Callback = &Body_Callback;
            [DllImport("lib", EntryPoint = "take")]
            private static extern void Take(nint callback, nint context);
        """;

    private static string Factory(string members) => $$"""
        public sealed unsafe class Factory
        {
            {{members}}
        }
        """;

    private static readonly string Guarded = Factory(Callback + $$"""
        public static void Make() { {{Guard}} Take((nint)s_Body_Callback, 0); }
        """);

    /// <summary>Each way the callback can reach Swift without a guard of the member handing it over.</summary>
    public static TheoryData<string, string> Unaccepted => new()
    {
        {
            "unguarded hand-off",
            Factory(Callback + "public static void Make() { Take((nint)s_Body_Callback, 0); }")
        },
        {
            "a guarded and an unguarded hand-off",
            Factory(Callback + $"public static void Make() {{ {Guard} Take((nint)s_Body_Callback, 0); }} public static void Other() {{ Take((nint)s_Body_Callback, 0); }}")
        },
        {
            "address taken in an unguarded member",
            Factory("""
                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
                private static void Body_Callback(SwiftSelf context) { }
                [DllImport("lib", EntryPoint = "take")] private static extern void Take(nint callback, nint context);
                public static void Make() { Take((nint)(delegate* unmanaged[Swift]<SwiftSelf, void>)&Body_Callback, 0); }
                """)
        },
        {
            "public callback",
            Factory(Callback.Replace("private static unsafe void Body_Callback", "public static unsafe void Body_Callback", StringComparison.Ordinal)
                    + $"public static void Make() {{ {Guard} Take((nint)s_Body_Callback, 0); }}")
        },
        {
            "internal field",
            Factory(Callback.Replace("private static unsafe readonly", "internal static unsafe readonly", StringComparison.Ordinal)
                    + $"public static void Make() {{ {Guard} Take((nint)s_Body_Callback, 0); }}")
        },
        {
            "address returned",
            Factory(Callback + $"public static nint Make() {{ {Guard} return (nint)s_Body_Callback; }}")
        },
        {
            "address stored in another field",
            Factory(Callback + $"private static nint s_copy; public static void Make() {{ {Guard} s_copy = (nint)s_Body_Callback; }}")
        },
        {
            "exported entry point",
            Factory(Callback.Replace("typeof(CallConvSwift) })]", "typeof(CallConvSwift) }, EntryPoint = \"body_callback\")]", StringComparison.Ordinal)
                    + $"public static void Make() {{ {Guard} Take((nint)s_Body_Callback, 0); }}")
        },
        {
            "never handed over",
            Factory("""
                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
                private static void Body_Callback(SwiftSelf context) { }
                """)
        },
        {
            "SwiftSelf<T> in the reverse signature",
            Factory("""
                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
                private static void Body_Callback(SwiftSelf<long> context) { }
                private static readonly delegate* unmanaged[Swift]<SwiftSelf<long>, void> s_Body_Callback = &Body_Callback;
                [DllImport("lib", EntryPoint = "take")] private static extern void Take(nint callback, nint context);
                """ + $"public static void Make() {{ {Guard} Take((nint)s_Body_Callback, 0); }}")
        },
        {
            "guard that does not refuse ReverseCallback",
            Factory(Callback + "public static void Make() { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)5, \"Factory.Make()\"); Take((nint)s_Body_Callback, 0); }")
        },
    };

    // ---- the engine (swift-transition-gate) -----------------------------------------------------

    [Fact]
    public void AGuardedPrivateCallback_IsAccepted()
    {
        var (report, result) = Engine(Guarded);

        var violation = Assert.Single(report.Violations);
        Assert.Equal(TransitionRule.R4, violation.Rule);
        Assert.Equal([SwiftTransitionScanner.SwiftConventionCallbackClass], violation.Classes.ToArray());
        Assert.True(result.Passed, ResidualGuardCheck.ToSummary(result));
        Assert.Equal([CallbackMember], ResidualGuardCheck.AcceptedReverseCallbacks(report, result));
        Assert.True(ResidualGuardCheck.IsAcceptedReverseCallback(violation, ResidualGuardCheck.AcceptedReverseCallbacks(report, result)));
    }

    [Theory]
    [MemberData(nameof(Unaccepted))]
    public void AnUnconfinedOrUnguardedCallback_StaysAnR4Failure(string shape, string body)
    {
        var (report, result) = Engine(body);

        Assert.Contains(report.Violations, v => v.Rule == TransitionRule.R4 && v.Member == CallbackMember);
        Assert.True(ResidualGuardCheck.AcceptedReverseCallbacks(report, result).Count == 0, $"{shape}: accepted\n{ResidualGuardCheck.ToSummary(result)}");
    }

    [Fact]
    public void AnExportedCallback_CarriesItsOwnClass()
    {
        var (report, _) = Engine(Factory(Callback.Replace("typeof(CallConvSwift) })]", "typeof(CallConvSwift) }, EntryPoint = \"body_callback\")]", StringComparison.Ordinal)));
        var violation = Assert.Single(report.Violations);
        Assert.Equal([SwiftTransitionScanner.SwiftConventionCallbackClass, SwiftTransitionScanner.ExportedCallbackClass], violation.Classes.ToArray());
        Assert.Contains("exported", violation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ACallbackStoredInAGenericTypesField_IsAcceptedThroughItsInstantiatedField()
    {
        // An UnmanagedCallersOnly method cannot be generic or live in a generic type, but a generic
        // type nested in its declaring type can hold its address.
        var (report, result) = Engine("""
            public sealed unsafe class Outer
            {
                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
                private static void Body_Callback(void* arg0, SwiftSelf context) { }
                [DllImport("lib", EntryPoint = "take")] private static extern void Take(nint callback, nint context);
                public sealed class Box<T>
                {
                    private static readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_Body_Callback = &Body_Callback;
                    public static void Make() { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)0x10000, "Box.Make()"); Take((nint)s_Body_Callback, 0); }
                    public static nint Leak() { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)0x10000, "Box.Leak()"); return (nint)s_Body_Callback; }
                }
            }
            """);

        Assert.True(result.Passed, ResidualGuardCheck.ToSummary(result));
        // Leak returns the address through the instantiated field: the reference resolves to the
        // definition, so the escape is seen.
        Assert.Empty(ResidualGuardCheck.AcceptedReverseCallbacks(report, result));

        var (confined, confinedResult) = Engine("""
            public sealed unsafe class Outer
            {
                [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
                private static void Body_Callback(void* arg0, SwiftSelf context) { }
                [DllImport("lib", EntryPoint = "take")] private static extern void Take(nint callback, nint context);
                public sealed class Box<T>
                {
                    private static readonly delegate* unmanaged[Swift]<void*, SwiftSelf, void> s_Body_Callback = &Body_Callback;
                    public static void Make() { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts)0x10000, "Box.Make()"); Take((nint)s_Body_Callback, 0); }
                }
            }
            """);
        Assert.True(confinedResult.Passed, ResidualGuardCheck.ToSummary(confinedResult));
        Assert.Equal(["Outer::Body_Callback"], ResidualGuardCheck.AcceptedReverseCallbacks(confined, confinedResult));
    }

    /// <summary>
    /// A throwing callback whose result Swift receives by address declares both the error and the
    /// indirect result ahead of its arguments, which a reverse thunk placing special registers by
    /// position cannot carry: its hand-offs must refuse <see cref="SwiftTransitionShapes.SpecialBeforeArguments"/> too.
    /// </summary>
    private static string ErrorAndIndirect(string guardFacts) => Factory($$"""
            [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
            private static unsafe void Body_Callback(SwiftError* errorOut, SwiftIndirectResult result, nint arg0, SwiftSelf context) { }
            private static unsafe readonly delegate* unmanaged[Swift]<SwiftError*, SwiftIndirectResult, nint, SwiftSelf, void> s_Body_Callback = &Body_Callback;
            [DllImport("lib", EntryPoint = "take")]
            private static extern void Take(nint callback, nint context);
            public static void Make() { SwiftTransitionGuard.ThrowIfUnsupported((SwiftTransitionFacts){{guardFacts}}, "Factory.Make()"); Take((nint)s_Body_Callback, 0); }
        """);

    [Fact]
    public void ACallbackTakingAnErrorAndAnIndirectResult_IsAcceptedOnlyBehindAGuardForBothShapes()
    {
        var (report, result) = Engine(ErrorAndIndirect("0x10002"));

        var hazard = Assert.Single(result.Hazards, h => h.Kind == TransitionSiteKind.UnmanagedCallersOnly);
        Assert.Equal(SwiftTransitionShapes.ReverseCallback | SwiftTransitionShapes.SpecialBeforeArguments, hazard.Shapes);
        Assert.True(result.Passed, ResidualGuardCheck.ToSummary(result));
        Assert.Equal([CallbackMember], ResidualGuardCheck.AcceptedReverseCallbacks(report, result));

        // A guard for the reverse callback alone lets the hand-off run where only the leading specials are refused.
        var (underReport, underResult) = Engine(ErrorAndIndirect("0x10000"));
        Assert.Contains(underReport.Violations, v => v.Rule == TransitionRule.R4 && v.Member == CallbackMember);
        Assert.Empty(ResidualGuardCheck.AcceptedReverseCallbacks(underReport, underResult));
    }

    [Fact]
    public void ACallbackTakingAnErrorAndAnIndirectResult_PassesThePackagedChecksOnlyBehindAGuardForBothShapes()
    {
        var (pack, consumer) = Packaged(ErrorAndIndirect("0x10002"));
        Assert.True(pack.Passed, string.Join("\n", pack.Problems));
        Assert.Equal(ConsumerTransitionVerdict.Verified, consumer.Verdict);

        var (underPack, underConsumer) = Packaged(ErrorAndIndirect("0x10000"));
        Assert.Contains(underPack.Problems, p => p.Contains($"R4 {CallbackMember}", StringComparison.Ordinal));
        Assert.Equal(ConsumerTransitionVerdict.Refused, underConsumer.Verdict);
    }

    // ---- SWIFTBIND133 (pack) and SWIFTBIND132 (consumer) ----------------------------------------

    [Fact]
    public void TheGuardedCallback_PassesBothPackagedChecks()
    {
        var (pack, consumer) = Packaged(Guarded);

        Assert.True(pack.Passed, string.Join("\n", pack.Problems));
        Assert.Equal(ConsumerTransitionVerdict.Verified, consumer.Verdict);
    }

    [Theory]
    [MemberData(nameof(Unaccepted))]
    public void AnUnacceptedCallback_FailsBothPackagedChecksOnR4(string shape, string body)
    {
        var (pack, consumer) = Packaged(body);

        Assert.True(pack.Problems.Any(p => p.Contains($"R4 {CallbackMember}", StringComparison.Ordinal)),
            $"{shape}: the pack check names no R4 for the callback:\n{string.Join("\n", pack.Problems)}");
        Assert.Equal(ConsumerTransitionVerdict.Refused, consumer.Verdict);
        Assert.True(consumer.Problems.Any(p => p.Contains($"R4 {CallbackMember}", StringComparison.Ordinal)),
            $"{shape}: the consumer check names no R4 for the callback:\n{string.Join("\n", consumer.Problems)}");
    }

    private static (AssemblyTransitionReport Report, ResidualGuardResult Result) Engine(string body)
    {
        var image = BridgeWithdrawalCheckTests.Compile(body, EmptyPlans);
        var report = SwiftTransitionScanner.Scan(image, "Fixture.dll",
            new SwiftTransitionScanOptions { ReferenceDirectories = [AppContext.BaseDirectory] });
        Assert.Empty(report.InspectionErrors);
        return (report, ResidualGuardCheck.Check(image, report));
    }

    private static (PackageTransitionEvidenceResult Pack, ConsumerTransitionResult Consumer) Packaged(string body)
    {
        var image = BridgeWithdrawalCheckTests.Compile(body, EmptyPlans);
        var scratch = Directory.CreateTempSubdirectory("reverse-callback-").FullName;
        try
        {
            var assembly = new PackagedAssembly("Fixture.dll", image, [AppContext.BaseDirectory]);
            var pack = PackageTransitionEvidence.Check([assembly], [], requireRoutedBridges: false, Path.Combine(scratch, "pack"));
            var consumer = ConsumerTransitionCheck.Check(assembly, Array.Empty<PackagedNative>(), requireNativeImages: false, Path.Combine(scratch, "consumer"));
            return (pack, consumer);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>The plans of a module that routes nothing.</summary>
    private static string EmptyPlans => new JsonObject
    {
        ["planVersion"] = SwiftCallPlan.Version,
        ["factsVersion"] = SwiftCallFacts.CurrentVersion,
        ["module"] = "Fixture",
        ["coverage"] = new JsonArray(),
        ["plans"] = new JsonArray(),
    }.ToJsonString();
}
