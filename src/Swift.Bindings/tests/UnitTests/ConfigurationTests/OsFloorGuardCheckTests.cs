// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SwiftBindings.TransitionGate;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// The managed half of the OS-floor gate: every method holding a call to a floored native entry
    /// point must throw PlatformNotSupportedException at entry on the OS that lacks the symbol, read
    /// from compiled IL (Debug and Release), whichever public member leads there.
    /// </summary>
    public class OsFloorGuardCheckTests
    {
        private static readonly OsFloorPoint Ios262 = new("ios", new Version(26, 2));
        private static readonly OsFloorPoint Catalyst262 = new("maccatalyst", new Version(26, 2));

        private const string Preamble = """
            using System;
            using System.Runtime.InteropServices;
            public static partial class Fixture
            {
                [DllImport("Wrapper", EntryPoint = "SBW_floored")] private static extern int Floored();
                [DllImport("Wrapper", EntryPoint = "SBW_fine")] private static extern int Fine();
            """;

        private const string Guard27 = """
                if ((OperatingSystem.IsOSPlatform("ios") && !OperatingSystem.IsOSPlatformVersionAtLeast("ios", 27, 2, 0, 0))
                    || (OperatingSystem.IsOSPlatform("maccatalyst") && !OperatingSystem.IsOSPlatformVersionAtLeast("maccatalyst", 27, 2, 0, 0)))
                    throw new PlatformNotSupportedException("needs 27.2");
            """;

        private static OsFloorHazard? Hazards(string library, string entryPoint, params OsFloorPoint[] points) =>
            entryPoint == "SBW_floored" ? new OsFloorHazard(library, entryPoint, points.Length > 0 ? points : new[] { Ios262 }, "reaches $sAbsent") : null;

        private static OsFloorGuardResult Check(string body, OptimizationLevel level, params OsFloorPoint[] points) =>
            OsFloorGuardCheck.Check(Compile(Preamble + body + "\n}", level), (l, e) => Hazards(l, e, points));

        [Theory]
        [InlineData(OptimizationLevel.Release)]
        [InlineData(OptimizationLevel.Debug)]
        public void AnUnguardedCallerFails_WhileItsGuardedSiblingAndAnUnflooredCallerPass(OptimizationLevel level)
        {
            var result = Check($$"""
                public static int Guarded()
                {
                {{Guard27}}
                    return Floored();
                }
                public static int Unguarded() => Floored();
                public static int Other() => Fine();
                """, level, Ios262, Catalyst262);

            var violation = Assert.Single(result.Violations);
            Assert.Equal(OsFloorGuardCheck.UnguardedRule, violation.Rule);
            Assert.Equal("Fixture::Unguarded", violation.Member);
            Assert.Contains("SBW_floored", violation.Reason);
            var import = Assert.Single(result.Hazards);
            Assert.Equal(new[] { "Fixture::Guarded", "Fixture::Unguarded" }, import.Leaves.OrderBy(l => l, StringComparer.Ordinal));
            Assert.Equal(1, result.RefusingLeaves);
            Assert.Empty(result.Errors);
        }

        [Theory]
        [InlineData(26, 0, false)]
        [InlineData(26, 2, false)]
        [InlineData(26, 3, true)]
        [InlineData(99, 0, true)]
        public void AGuardRefusesOnlyBelowItsFloor(int major, int minor, bool passes)
        {
            var result = Check($$"""
                public static int Guarded()
                {
                    if (OperatingSystem.IsOSPlatform("ios") && !OperatingSystem.IsOSPlatformVersionAtLeast("ios", {{major}}, {{minor}}, 0, 0))
                        throw new PlatformNotSupportedException("floor");
                    return Floored();
                }
                """, OptimizationLevel.Release);

            Assert.Equal(passes, result.Violations.IsEmpty);
        }

        [Fact]
        public void AGuardForAnotherPlatformDoesNotRefuseOnTheFlooredOne()
        {
            var result = Check("""
                public static int Guarded()
                {
                    if (OperatingSystem.IsOSPlatform("tvos") && !OperatingSystem.IsOSPlatformVersionAtLeast("tvos", 27, 2, 0, 0))
                        throw new PlatformNotSupportedException("floor");
                    return Floored();
                }
                """, OptimizationLevel.Release, Ios262);

            Assert.Equal(OsFloorGuardCheck.UnguardedRule, Assert.Single(result.Violations).Rule);
        }

        [Fact]
        public void MacCatalystAnswersToAnIosGuard_AtItsIosVersion()
        {
            var result = Check("""
                public static int Guarded()
                {
                    if (OperatingSystem.IsIOS() && !OperatingSystem.IsIOSVersionAtLeast(27, 2))
                        throw new PlatformNotSupportedException("floor");
                    return Floored();
                }
                """, OptimizationLevel.Debug, Ios262, Catalyst262);

            Assert.True(result.Passed, string.Join("\n", result.Violations));
        }

        [Fact]
        public void WorkBeforeTheGuard_FailsEvenWhenTheGuardFollows()
        {
            var result = Check($$"""
                public static int Guarded(int x)
                {
                    Console.WriteLine(x);
                {{Guard27}}
                    return Floored();
                }
                """, OptimizationLevel.Release);

            var violation = Assert.Single(result.Violations);
            Assert.Equal("Fixture::Guarded", violation.Member);
            // The run stops at the call that precedes the guard.
            Assert.Contains("Call without throwing", violation.Reason);
        }

        [Theory]
        [InlineData(OptimizationLevel.Release)]
        [InlineData(OptimizationLevel.Debug)]
        public void AGuardInsideATryBlock_DoesNotRefuse_SinceItsFinallyRunsFirst(OptimizationLevel level)
        {
            var result = Check($$"""
                public static int Guarded()
                {
                    try
                    {
                {{Guard27}}
                        return 0;
                    }
                    finally
                    {
                        Floored();
                    }
                }
                """, level);

            Assert.Equal("Fixture::Guarded", Assert.Single(result.Violations).Member);
        }

        [Fact]
        public void APrivateHelperIsCoveredByRefusingCallers_AndNotWhenOneCallerSkipsTheGuard()
        {
            var covered = Check($$"""
                private static int Helper() => Floored();
                public static int A()
                {
                {{Guard27}}
                    return Helper();
                }
                public static int B()
                {
                {{Guard27}}
                    return Helper() + 1;
                }
                """, OptimizationLevel.Release);
            Assert.True(covered.Passed, string.Join("\n", covered.Violations));
            Assert.Equal(1, covered.CoveredPrivateLeaves);

            var bypassed = Check($$"""
                private static int Helper() => Floored();
                public static int A()
                {
                {{Guard27}}
                    return Helper();
                }
                public static int B() => Helper() + 1;
                """, OptimizationLevel.Release);
            var violation = Assert.Single(bypassed.Violations);
            Assert.Equal("Fixture::Helper", violation.Member);
        }

        [Fact]
        public void AnInternalHelperMustRefuseItself_SinceCodeCompiledIntoTheAssemblyCanCallIt()
        {
            var result = Check($$"""
                internal static int Helper() => Floored();
                public static int A()
                {
                {{Guard27}}
                    return Helper();
                }
                """, OptimizationLevel.Release);

            Assert.Equal("Fixture::Helper", Assert.Single(result.Violations).Member);
        }

        [Theory]
        [InlineData(OptimizationLevel.Release)]
        [InlineData(OptimizationLevel.Debug)]
        public void ALambdaBelongsToTheMethodThatCreatesIt(OptimizationLevel level)
        {
            var result = Check($$"""
                public static Func<int> Guarded()
                {
                {{Guard27}}
                    return () => Floored();
                }
                public static Func<int> Unguarded() => () => Floored() + 1;
                """, level);

            Assert.Equal("Fixture::Unguarded", Assert.Single(result.Violations).Member);
        }

        [Theory]
        [InlineData(OptimizationLevel.Release)]
        [InlineData(OptimizationLevel.Debug)]
        public void AConstructorRefusesAfterItsFieldInitializers_ButNotAfterAChainedCall(OptimizationLevel level)
        {
            // Field initializers and the base call precede a constructor body's guard and call
            // nothing; an argument computed for a chained constructor runs before the guard.
            var source = $$"""
                using System;
                using System.Runtime.InteropServices;
                public sealed class Holder
                {
                    [DllImport("Wrapper", EntryPoint = "SBW_floored")] private static extern int Floored();
                    private int _value = 0;
                    private object? _handle = null;
                    private static readonly object Shared = new object();
                    public Holder()
                    {
                    {{Guard27}}
                        _value = Floored();
                    }
                    private Holder(int value) { _value = value; _handle = Shared; }
                    public Holder(long seed) : this(Floored())
                    {
                    {{Guard27}}
                    }
                }
                """;
            var result = OsFloorGuardCheck.Check(Compile(source, level), (l, e) => Hazards(l, e));

            var violation = Assert.Single(result.Violations);
            Assert.Equal("Holder::.ctor", violation.Member);
            Assert.Equal(OsFloorGuardCheck.UnguardedRule, violation.Rule);
            Assert.Equal(1, result.RefusingLeaves);
        }

        [Fact]
        public void AStaticConstructorCannotRefuse()
        {
            var result = Check("""
                public static readonly int Value;
                static Fixture() { Value = Floored(); }
                """, OptimizationLevel.Release);

            Assert.Equal(OsFloorGuardCheck.InitializerRule, Assert.Single(result.Violations).Rule);
        }

        [Fact]
        public void APublicImportIsExposed()
        {
            var source = """
                using System;
                using System.Runtime.InteropServices;
                public static class Fixture
                {
                    [DllImport("Wrapper", EntryPoint = "SBW_floored")] public static extern int Floored();
                }
                """;
            var result = OsFloorGuardCheck.Check(Compile(source, OptimizationLevel.Release), (l, e) => Hazards(l, e));

            Assert.Equal(OsFloorGuardCheck.ExposedRule, Assert.Single(result.Violations).Rule);
        }

        [Fact]
        public void AnAsyncMethodsGuardRunsInItsStateMachine_SoItsKickoffDoesNotRefuseAtEntry()
        {
            // An async method's body, guard included, runs in MoveNext: its caller gets a faulted task,
            // not a refusal, and the check names the async method rather than accepting it.
            var result = Check($$"""
                public static async System.Threading.Tasks.Task<int> GuardedAsync()
                {
                {{Guard27}}
                    await System.Threading.Tasks.Task.Yield();
                    return Floored();
                }
                """, OptimizationLevel.Release);

            Assert.Equal("Fixture::GuardedAsync", Assert.Single(result.Violations).Member);
        }

        private static byte[] Compile(string source, OptimizationLevel level)
        {
            var frameworkDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "")
                .Split(Path.PathSeparator)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(p) && p.StartsWith(frameworkDir, StringComparison.Ordinal))
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("Fixture",
                new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, optimizationLevel: level));
            using var stream = new MemoryStream();
            var emitted = compilation.Emit(stream);
            Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
            return stream.ToArray();
        }
    }
}
