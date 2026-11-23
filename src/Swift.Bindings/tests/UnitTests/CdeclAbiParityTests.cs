// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="CdeclAbiParity"/> — the verdict behind <c>nuke binding-tests
/// --compile-only</c>'s cdecl ABI parity gate, which holds every generated C# P/Invoke to the
/// registers its generated Swift <c>@_cdecl</c> reads. These pin the two signature parsers, the
/// register lowering of each side (C# <c>bool</c> by its marshalling included), the rules (width,
/// class, return, arity, and the arity exemption for undecided arguments), library scoping, and the
/// vacuity guards that turn a parser that stopped matching into a failure.
/// </summary>
public class CdeclAbiParityTests
{
    private const string Lib = "Wrapper";

    private const string Swift = """
        public typealias SBW_DoneFn = @convention(c) (Swift.UnsafeMutableRawPointer?, Swift.Int32) -> Swift.Void

        @_cdecl("SBW_Create")
        // A comment between the attribute and the function.
        @available(iOS 15.0, *)
        public func _sbw_create(_ ptr: Swift.UnsafePointer<Swift.Int32>?, _ count: Swift.Int, flag: Swift.Bool,
                                _ scale: Swift.Double, _ done: SBW_DoneFn?, _ cb: (@convention(c) (Swift.Int) -> Swift.Void)?,
                                _ out: inout Swift.Int64) -> Swift.UnsafeMutableRawPointer? {
            return nil
        }

        @_cdecl("SBW_Free")
        internal func _sbw_free(_ handle: Swift.UnsafeMutableRawPointer) {
        }

        @_cdecl("SBW_Point")
        public func _sbw_point(_ p: CGPoint) -> Swift.Float {
            return 0
        }
        """;

    private static CdeclSignature Sig(string entry, string library, string ret, params string[] parameters) =>
        new(entry, library, parameters.Select(Slot).ToList(), Slot(ret), "test");

    // Test shorthand: "i64", "i32", "i8", "f64", "f32", "void", "?" (undecided).
    private static CdeclSlot Slot(string s) => new(s, s switch
    {
        "?" => CdeclCarrier.Unknown,
        "void" => CdeclCarrier.Void,
        _ when s[0] == 'i' => CdeclCarrier.Int(int.Parse(s[1..])),
        _ => CdeclCarrier.Fp(int.Parse(s[1..])),
    });

    private static readonly CdeclPositiveControl[] NoControls = Array.Empty<CdeclPositiveControl>();

    // ---------------------------------------------------------------- Swift parser

    [Fact]
    public void ParseSwiftCdecls_LowersEachParameterAndReturn()
    {
        var defs = CdeclAbiParity.ParseSwiftCdecls(Swift, Lib, "w.swift");

        Assert.Equal(new[] { "SBW_Create", "SBW_Free", "SBW_Point" }, defs.Select(d => d.EntryPoint));
        Assert.All(defs, d => Assert.Equal(Lib, d.Library));

        var create = defs[0];
        Assert.Equal(
            new[]
            {
                CdeclCarrier.Pointer,   // optional typed pointer
                CdeclCarrier.Int(64),   // Swift.Int
                CdeclCarrier.Int(8),    // Swift.Bool (labelled parameter)
                CdeclCarrier.Fp(64),    // Swift.Double
                CdeclCarrier.Pointer,   // file typealias of a @convention(c) type, optional
                CdeclCarrier.Pointer,   // parenthesised optional @convention(c) type
                CdeclCarrier.Pointer,   // inout
            },
            create.Parameters.Select(p => p.Carrier));
        Assert.Equal(CdeclCarrier.Pointer, create.Return.Carrier);

        Assert.Equal(CdeclCarrier.Void, defs[1].Return.Carrier);
        Assert.Equal(CdeclCarrier.Unknown, defs[2].Parameters[0].Carrier);   // struct by value
        Assert.Equal(CdeclCarrier.Fp(32), defs[2].Return.Carrier);
    }

    [Theory]
    [InlineData("Swift.Int32", 32)]
    [InlineData("Int", 64)]
    [InlineData("Swift.UInt16", 16)]
    [InlineData("CChar", 8)]
    [InlineData("Swift.OpaquePointer", 64)]
    [InlineData("Swift.UnsafeMutablePointer<Swift.UnsafeRawPointer?>", 64)]
    public void SwiftCarrier_IntegerSpellings(string type, int bits) =>
        Assert.Equal(CdeclCarrier.Int(bits), CdeclAbiParity.SwiftCarrier(type));

    [Theory]
    [InlineData("Swift.Int?")]                       // an optional scalar is not C-representable
    [InlineData("Swift.UnsafeBufferPointer<Swift.Int>")]   // two words, not a pointer
    [InlineData("SomeModule.SomeStruct")]
    public void SwiftCarrier_UndecidableSpellings(string type) =>
        Assert.Equal(CdeclCarrier.Unknown, CdeclAbiParity.SwiftCarrier(type));

    // ---------------------------------------------------------------- C# parser

    [Fact]
    public void ParsePInvokes_ReadsLibraryEntryPointAndMarshalling()
    {
        const string cs = """
            [global::System.Runtime.InteropServices.LibraryImport("Wrapper", EntryPoint = "SBW_Create")]
            [global::System.Runtime.InteropServices.UnmanagedCallConv(CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvSwift) })]
            [return: global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.U1)]
            internal static unsafe partial bool Create(int* ptr, nint count,
                [global::System.Runtime.InteropServices.MarshalAs(global::System.Runtime.InteropServices.UnmanagedType.U1)] bool flag,
                double scale, delegate* unmanaged[Cdecl]<void*, int, void> done, ref long value, string? label,
                global::System.Runtime.InteropServices.SafeHandle café);

            [System.Runtime.InteropServices.DllImport("Other")]
            private static extern bool SBW_Free(global::System.IntPtr handle, bool legacy);

            [LibraryImport("Wrapper", EntryPoint = "SBW_Bare")]
            private static partial bool Bare(bool unannotated);
            """;

        var imports = CdeclAbiParity.ParsePInvokes(cs, "t.cs");

        Assert.Equal(3, imports.Count);
        var create = imports[0];
        Assert.Equal(("SBW_Create", "Wrapper"), (create.EntryPoint, create.Library));
        Assert.Equal(CdeclCarrier.Int(8), create.Return.Carrier);
        Assert.Equal(
            new[]
            {
                CdeclCarrier.Pointer, CdeclCarrier.Pointer, CdeclCarrier.Int(8), CdeclCarrier.Fp(64),
                CdeclCarrier.Pointer, CdeclCarrier.Pointer, CdeclCarrier.Pointer, CdeclCarrier.Pointer,
            },
            create.Parameters.Select(p => p.Carrier));

        // No EntryPoint: the method name is the symbol. DllImport's default bool is a 4-byte BOOL.
        var free = imports[1];
        Assert.Equal(("SBW_Free", "Other"), (free.EntryPoint, free.Library));
        Assert.Equal(CdeclCarrier.Int(32), free.Return.Carrier);
        Assert.Equal(new[] { CdeclCarrier.Pointer, CdeclCarrier.Int(32) }, free.Parameters.Select(p => p.Carrier));

        // LibraryImport cannot compile an unannotated bool, so the shape is left undecided.
        Assert.Equal(CdeclCarrier.Unknown, imports[2].Parameters[0].Carrier);
        Assert.Equal(CdeclCarrier.Unknown, imports[2].Return.Carrier);
    }

    // ---------------------------------------------------------------- Verdict

    [Theory]
    [InlineData("i32", "i64", "WIDTH")]   // C# int against Swift Int: the upper half is unspecified
    [InlineData("f64", "i64", "CLASS")]   // a floating-point register where Swift reads an integer one
    [InlineData("i8", "i32", "WIDTH")]
    public void Evaluate_FailsAnArgumentInADifferentRegister(string csharp, string swift, string kind)
    {
        var verdict = CdeclAbiParity.Evaluate(
            new[] { Sig("SBW_F", Lib, "void", "i64", swift) },
            new[] { Sig("SBW_F", Lib, "void", "i64", csharp) },
            minimumPairs: 1, NoControls);

        var failure = Assert.Single(verdict.Failures);
        Assert.StartsWith(kind, failure);
        Assert.Contains("argument 1", failure);
    }

    [Fact]
    public void Evaluate_FailsAReturnInADifferentRegister()
    {
        var verdict = CdeclAbiParity.Evaluate(
            new[] { Sig("SBW_F", Lib, "i64") }, new[] { Sig("SBW_F", Lib, "i32") }, 1, NoControls);

        Assert.Contains("return", Assert.Single(verdict.Failures));
    }

    [Fact]
    public void Evaluate_ArityIsCheckedOnlyWhenEveryArgumentIsDecided()
    {
        var decided = CdeclAbiParity.Evaluate(
            new[] { Sig("SBW_F", Lib, "void", "i64", "i64") }, new[] { Sig("SBW_F", Lib, "void", "i64") }, 1, NoControls);
        Assert.StartsWith("ARITY", Assert.Single(decided.Failures));

        // A struct by value may split into several Swift words: not a failure, counted instead.
        var undecided = CdeclAbiParity.Evaluate(
            new[] { Sig("SBW_F", Lib, "void", "i64", "i64", "i32") }, new[] { Sig("SBW_F", Lib, "void", "?", "i32") }, 1, NoControls);
        Assert.True(undecided.Passed, string.Join("\n", undecided.Failures));
        Assert.Equal(1, undecided.ArityUncheckedPairCount);
        Assert.Equal(0, undecided.FullyCheckedPairCount);
    }

    [Fact]
    public void Evaluate_SkipsUndecidedArgumentsAndChecksTheRest()
    {
        var verdict = CdeclAbiParity.Evaluate(
            new[] { Sig("SBW_F", Lib, "void", "?", "i64") }, new[] { Sig("SBW_F", Lib, "void", "?", "i32") }, 1, NoControls);

        Assert.StartsWith("WIDTH", Assert.Single(verdict.Failures));
        Assert.Equal(1, verdict.SkippedArgumentCount);
        Assert.Equal(1, verdict.CheckedArgumentCount);
    }

    [Fact]
    public void Evaluate_PairsOnlyWithinTheSameLibrary()
    {
        // The same entry point compiled into two wrappers with different signatures: each import is
        // held to its own library's definition.
        var verdict = CdeclAbiParity.Evaluate(
            new[] { Sig("SBW_F", "A", "void", "i64"), Sig("SBW_F", "B", "void", "i32") },
            new[] { Sig("SBW_F", "A", "void", "i64"), Sig("SBW_F", "B", "void", "i32"), Sig("SBW_F", "Elsewhere", "void", "f32") },
            minimumPairs: 2, NoControls);

        Assert.True(verdict.Passed, string.Join("\n", verdict.Failures));
        Assert.Equal(2, verdict.PairCount);
    }

    [Fact]
    public void Evaluate_VacuityGuardsFailClosed()
    {
        var swift = new[] { Sig("SBW_F", "A", "void", "i64"), Sig("SBW_G", "B", "void", "i64") };
        var cs = new[] { Sig("SBW_F", "A", "void", "i64") };

        var verdict = CdeclAbiParity.Evaluate(swift, cs, minimumPairs: 5,
            new[] { new CdeclPositiveControl("A", "SBW_Missing", "seeded") });

        Assert.Contains(verdict.Failures, f => f.Contains("below the floor of 5"));
        Assert.Contains(verdict.Failures, f => f.Contains("compiled into B"));
        Assert.Contains(verdict.Failures, f => f.Contains("SBW_Missing") && f.Contains("not paired"));
    }

    [Fact]
    public void Evaluate_PositiveControlMustBeCheckedArgumentByArgument()
    {
        var verdict = CdeclAbiParity.Evaluate(
            new[] { Sig("SBW_F", Lib, "void", "i64", "?") }, new[] { Sig("SBW_F", Lib, "void", "i64", "?") },
            minimumPairs: 1, new[] { new CdeclPositiveControl(Lib, "SBW_F", "seeded") });

        Assert.Contains("not checked argument by argument", Assert.Single(verdict.Failures));
    }

    // ---------------------------------------------------------------- Tree

    [Fact]
    public void EvaluateTree_ReadsTheGeneratedTreeAndSkipsBuildProducts()
    {
        var root = Directory.CreateTempSubdirectory("cdecl-parity-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "W.Wrapper.swift"), Swift);
            File.WriteAllText(Path.Combine(root, "W.cs"), """
                [LibraryImport("Wrapper", EntryPoint = "SBW_Free")]
                internal static partial void Free(global::System.IntPtr handle);
                """);
            // A stale copy under obj/ with the wrong width must not be read.
            Directory.CreateDirectory(Path.Combine(root, "obj"));
            File.WriteAllText(Path.Combine(root, "obj", "Stale.cs"), """
                [LibraryImport("Wrapper", EntryPoint = "SBW_Free")]
                internal static partial void Free(int handle);
                """);

            var verdict = CdeclAbiParity.EvaluateTree(root,
                new[] { new CdeclSwiftSource(Path.Combine(root, "W.Wrapper.swift"), Lib) },
                minimumPairs: 1, new[] { new CdeclPositiveControl(Lib, "SBW_Free", "fixture") });
            Assert.True(verdict.Passed, string.Join("\n", verdict.Failures));
            Assert.Equal(1, verdict.PairCount);

            var missing = CdeclAbiParity.EvaluateTree(root,
                new[] { new CdeclSwiftSource(Path.Combine(root, "Absent.swift"), Lib) }, 0, NoControls);
            Assert.Contains(missing.Failures, f => f.Contains("Absent.swift") && f.Contains("not found"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
