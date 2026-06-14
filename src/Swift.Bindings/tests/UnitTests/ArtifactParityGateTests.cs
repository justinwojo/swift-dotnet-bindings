// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="ArtifactParityGate"/> — the pure cross-artifact parity logic
/// behind the <c>nuke binding-tests --compile-only</c> gate. Each gate is exercised with
/// synthetic inputs shaped like the documented defects it closes
/// (<c>src/docs/architecture-review-2026-06.md</c>):
///   • Gate 1 symbol existence → Defect A (dangling import), Defect cluster D (member-path).
///   • Gate 2 struct-mirror arity → Defect B (<c>static let</c> leak into a Buffer).
///   • Gate 3 vtable parity → Finding 8 (field-count), Defect C (optional-before-required skew).
/// The baseline ratchet (seed → diff → green; new divergence → fail) is covered too.
/// </summary>
public class ArtifactParityGateTests
{
    // Helper: an empty baseline (nothing absorbed).
    private static ArtifactParityGate.ParityBaseline EmptyBaseline => new();

    // ===================================================================
    //  ParseExterns + call-site detection
    // ===================================================================

    [Fact]
    public void ParseExterns_CapturesLibraryEntryPointAndMethod()
    {
        const string cs = """
            [LibraryImport("MainLib", EntryPoint = "SBW_foo_bar")]
            private static partial global::System.IntPtr PInvoke_FooBar(global::System.IntPtr self);
            """;

        var e = Assert.Single(ArtifactParityGate.ParseExterns(cs));
        Assert.Equal("MainLib", e.Library);
        Assert.Equal("SBW_foo_bar", e.EntryPoint);
        Assert.Equal("PInvoke_FooBar", e.Method);
    }

    [Fact]
    public void ParseExterns_MissingEntryPoint_DefaultsToMethodName()
    {
        const string cs = """
            [DllImport("MainLib")]
            private static extern int Native_Compute(int x);
            """;

        var e = Assert.Single(ArtifactParityGate.ParseExterns(cs));
        Assert.Equal("Native_Compute", e.EntryPoint);
        Assert.Equal("Native_Compute", e.Method);
    }

    [Fact]
    public void ParseExterns_InterleavedAttributes_DoNotCorruptMethodName()
    {
        // The [UnmanagedCallConv(... typeof(CallConvCdecl) ...)] line sits between the
        // import attribute and the decl. Anchoring on `partial`/`extern` must skip it so
        // `typeof(` is not mis-read as the method.
        const string cs = """
            [LibraryImport("MainLib", EntryPoint = "SBW_x")]
            [UnmanagedCallConv(CallConvs = new System.Type[] { typeof(CallConvCdecl) })]
            private static partial void SBW_x(global::System.IntPtr p);
            """;

        var e = Assert.Single(ArtifactParityGate.ParseExterns(cs));
        Assert.Equal("SBW_x", e.Method);
    }

    [Fact]
    public void ParseExterns_IsCalled_TrueOnlyWhenInvokedBeyondDeclaration()
    {
        const string cs = """
            [LibraryImport("L", EntryPoint = "called_sym")]
            private static partial int CalledPInvoke(int x);

            [LibraryImport("L", EntryPoint = "dead_sym")]
            private static partial int DeadPInvoke(int x);

            public int Use() => CalledPInvoke(1) + CalledPInvoke(2);
            """;

        var externs = ArtifactParityGate.ParseExterns(cs);
        Assert.True(externs.Single(e => e.Method == "CalledPInvoke").IsCalled);
        Assert.False(externs.Single(e => e.Method == "DeadPInvoke").IsCalled);
    }

    // ===================================================================
    //  ParseNmSymbols
    // ===================================================================

    [Theory]
    [InlineData("0000000000001234 T _SBW_foo\n", "SBW_foo")]   // addr type _name columns
    [InlineData("                 U _swift_release\n", "swift_release")]
    [InlineData("_BareName\n", "BareName")]                     // bare, leading underscore only
    public void ParseNmSymbols_StripsSingleLeadingUnderscore_AndTakesLastToken(string nm, string expected)
    {
        var syms = ArtifactParityGate.ParseNmSymbols(nm);
        Assert.Contains(expected, syms);
    }

    [Fact]
    public void ParseNmSymbols_IgnoresBlankLines()
    {
        Assert.Empty(ArtifactParityGate.ParseNmSymbols("\n   \n\n"));
    }

    [Theory]
    [InlineData("SBW_Foo_bar_123", true)]
    [InlineData("SBSW_MCB_ABC_0_run", true)]
    [InlineData("Get_EveryProtocol_VariadicItem_WitnessTable", true)]
    [InlineData("Get_EveryObjCProtocol_Foo_WitnessTable", true)]
    [InlineData("SetSummable_vtable", true)]
    [InlineData("Get_SwiftBindingsTestLib_ReadOnlyProps_storedInt", true)]
    [InlineData("$s18SwiftBindingsTestLib7Genericxcfr", false)] // mangled Swift — not authored
    [InlineData("swift_release", false)]
    public void IsAuthoredWrapperSymbol_MatchesGeneratorAuthoredShapes(string symbol, bool expected)
        => Assert.Equal(expected, ArtifactParityGate.IsAuthoredWrapperSymbol(symbol));

    // ===================================================================
    //  Gate 1 — forward symbol existence (Defect A)
    // ===================================================================

    [Fact]
    public void Forward_CalledExternMissingFromDylib_IsViolation()
    {
        // Defect A: a called P/Invoke binds a mangled symbol the compiler never exported.
        const string cs = """
            [LibraryImport("MainLib", EntryPoint = "$sDanglingmlF")]
            private static partial global::System.IntPtr PInvoke_Dangling(global::System.IntPtr p);
            public IntPtr Make() => PInvoke_Dangling(default);
            """;
        var findings = Compute(cs, symbolsByLibrary: Libs(("MainLib", new[] { "some_other_sym" })));

        Assert.True(findings.ForwardMissingByLibrary.TryGetValue("MainLib", out var miss));
        Assert.Equal(new[] { "$sDanglingmlF" }, miss);

        var v = Assert.Single(ArtifactParityGate.DiffAgainstBaseline(findings, EmptyBaseline),
            x => x.Gate == "symbol-forward");
        Assert.Contains("$sDanglingmlF", v.Detail);
    }

    [Fact]
    public void Forward_PresentSymbol_NoViolation()
    {
        const string cs = """
            [LibraryImport("MainLib", EntryPoint = "real_sym")]
            private static partial int PInvoke_Real(int x);
            public int Use() => PInvoke_Real(1);
            """;
        var findings = Compute(cs, symbolsByLibrary: Libs(("MainLib", new[] { "real_sym" })));
        Assert.Empty(findings.ForwardMissingByLibrary);
    }

    [Fact]
    public void Forward_UncalledMissingExtern_IsNotAViolation()
    {
        // Dead declaration: never invoked, so it cannot fault at runtime → excluded.
        const string cs = """
            [LibraryImport("MainLib", EntryPoint = "dead_missing")]
            private static partial int PInvoke_Dead(int x);
            """;
        var findings = Compute(cs, symbolsByLibrary: Libs(("MainLib", NoSyms)));
        Assert.Empty(findings.ForwardMissingByLibrary);
    }

    [Fact]
    public void Forward_UnknownLibrary_IsReportedAsSkipped_NotGated()
    {
        const string cs = """
            [LibraryImport("/usr/lib/swift/libswiftCore.dylib", EntryPoint = "swift_retain")]
            private static partial void PInvoke_Retain(global::System.IntPtr p);
            public void Use() => PInvoke_Retain(default);
            """;
        var findings = Compute(cs, symbolsByLibrary: Libs(("MainLib", new[] { "x" })));
        Assert.Empty(findings.ForwardMissingByLibrary);
        Assert.Equal(1, findings.SkippedLibraries["/usr/lib/swift/libswiftCore.dylib"]);
    }

    [Fact]
    public void Forward_Baseline_AbsorbsKnown_ButFlagsNew()
    {
        const string cs = """
            [LibraryImport("L", EntryPoint = "known_missing")]
            private static partial int A(int x);
            [LibraryImport("L", EntryPoint = "new_missing")]
            private static partial int B(int x);
            public int Use() => A(1) + B(2);
            """;
        var findings = Compute(cs, symbolsByLibrary: Libs(("L", NoSyms)));

        var baseline = new ArtifactParityGate.ParityBaseline
        {
            SymbolForwardKnownMissing = new() { ["L"] = new() { "known_missing" } },
        };

        var forward = ArtifactParityGate.DiffAgainstBaseline(findings, baseline)
            .Where(v => v.Gate == "symbol-forward").ToList();
        var v = Assert.Single(forward);
        Assert.Contains("new_missing", v.Detail);
    }

    // ===================================================================
    //  Gate 1 — reverse orphans
    // ===================================================================

    [Fact]
    public void Reverse_AuthoredExportNotReferenced_IsViolation_AndBaselineAbsorbs()
    {
        const string cs = """
            [LibraryImport("SwiftBindings", EntryPoint = "SBW_used")]
            private static partial void PInvoke_Used();
            public void Use() => PInvoke_Used();
            """;
        // Wrapper exports two authored symbols; only SBW_used is referenced.
        var wrapperAuthored = Set("SBW_used", "SBW_orphan");
        var findings = ArtifactParityGate.ComputeFindings(
            cs, swiftWrapperSource: "", abiJson: EmptyAbi,
            symbolsByLibrary: Libs(("SwiftBindings", new[] { "SBW_used", "SBW_orphan" })),
            wrapperAuthoredSymbols: wrapperAuthored);

        Assert.Equal(new[] { "SBW_orphan" }, findings.ReverseOrphans);

        Assert.Single(ArtifactParityGate.DiffAgainstBaseline(findings, EmptyBaseline), v => v.Gate == "symbol-reverse");

        var baseline = new ArtifactParityGate.ParityBaseline { SymbolReverseKnownOrphans = new() { "SBW_orphan" } };
        Assert.DoesNotContain(ArtifactParityGate.DiffAgainstBaseline(findings, baseline), v => v.Gate == "symbol-reverse");
    }

    // ===================================================================
    //  Gate 2 — struct-mirror arity (Defect B)
    // ===================================================================

    [Theory]
    [InlineData("storedInt_", "storedInt")]
    [InlineData("storedString_0_", "storedString")]
    [InlineData("storedString_1_", "storedString")]
    [InlineData("value_", "value")]
    public void FieldStem_CollapsesWordSuffixes(string field, string expected)
        => Assert.Equal(expected, ArtifactParityGate.FieldStem(field));

    [Fact]
    public void ParseCsBufferStems_CollapsesMultiWordPropertyToOneStem()
    {
        const string cs = """
            public partial class ReadOnlyProps : ISwiftObject, ISwiftStruct, IDisposable
            {
                public struct Buffer {
                    private int storedInt_;  // Note: Do not access this field directly - use the property accessors
                    private IntPtr storedString_0_;  // Note: Do not access this field directly - use the property accessors
                    private IntPtr storedString_1_;  // Note: Do not access this field directly - use the property accessors
                }
            }
            """;
        var stems = ArtifactParityGate.ParseCsBufferStems(cs)["ReadOnlyProps"];
        Assert.Equal(new[] { "storedInt", "storedString" }, stems);
    }

    [Fact]
    public void ParseAbiStoredInstanceProps_ExcludesStaticAndComputed()
    {
        const string abi = """
            { "ABIRoot": { "kind": "Root", "children": [
              { "kind": "TypeDecl", "declKind": "Struct", "name": "ReadOnlyProps", "children": [
                { "kind": "Var", "name": "storedInt",    "hasStorage": true },
                { "kind": "Var", "name": "storedString", "hasStorage": true },
                { "kind": "Var", "name": "version",      "hasStorage": true, "static": true },
                { "kind": "Var", "name": "summary" }
              ]}
            ]}}
            """;
        var props = ArtifactParityGate.ParseAbiStoredInstanceProps(abi)["ReadOnlyProps"];
        Assert.Equal(new[] { "storedInt", "storedString" }, props);
    }

    [Fact]
    public void StructArity_StaticLetLeakIntoBuffer_IsViolation()
    {
        // Defect B: `static let version` leaks a `version_` field into the frozen-struct
        // Buffer → over-sized mirror → OOB read. The gate flags the leaked non-instance prop.
        const string cs = """
            public partial class ReadOnlyProps : ISwiftObject, ISwiftStruct, IDisposable
            {
                public struct Buffer {
                    private int storedInt_;
                    private IntPtr storedString_0_;
                    private IntPtr storedString_1_;
                    private int version_;
                }
            }
            """;
        const string abi = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "ReadOnlyProps", "children": [
                { "kind": "Var", "name": "storedInt",    "hasStorage": true },
                { "kind": "Var", "name": "storedString", "hasStorage": true },
                { "kind": "Var", "name": "version",      "hasStorage": true, "static": true }
              ]}
            ]}}
            """;
        var findings = ArtifactParityGate.ComputeFindings(cs, "", abi, Libs(), Set());

        var f = Assert.Single(findings.StructArity);
        Assert.Equal("ReadOnlyProps", f.Struct);
        Assert.Equal(new[] { "version" }, f.BufferExtra);
        Assert.Empty(f.BufferMissing);

        var v = Assert.Single(ArtifactParityGate.DiffAgainstBaseline(findings, EmptyBaseline), x => x.Gate == "struct-arity");
        Assert.Contains("version", v.Detail);
    }

    [Fact]
    public void StructArity_CleanMirror_NoViolation()
    {
        const string cs = """
            public partial class P : ISwiftStruct {
                public struct Buffer { private int a_; private int b_; }
            }
            """;
        const string abi = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "P", "children": [
                { "kind": "Var", "name": "a", "hasStorage": true },
                { "kind": "Var", "name": "b", "hasStorage": true }
              ]}
            ]}}
            """;
        var findings = ArtifactParityGate.ComputeFindings(cs, "", abi, Libs(), Set());
        Assert.Empty(findings.StructArity);
    }

    [Fact]
    public void ParseCsDirectStructStems_ReadsInlineLayoutFields_ByNoteMarker()
    {
        // A direct value-type struct keeps its backing fields inline (no nested Buffer),
        // mixed with methods / P/Invoke decls. Only the "do not access" marked fields are
        // layout; the leading-underscore handle and the P/Invoke decl must be ignored.
        const string cs = """
            public unsafe partial struct SummableInt32 : ISwiftObject, IDisposable, Swift.Runtime.IExistentialBoxable
            {
                private int value_;  // Note: Do not access this field directly - use the property accessors
                private IntPtr _payload;
                private int Value_Get() { return 0; }
                [LibraryImport("X", EntryPoint = "y")]
                private static partial int PInvoke_value_Get_9B(IntPtr self);
            }
            """;
        var stems = ArtifactParityGate.ParseCsDirectStructStems(cs)["SummableInt32"];
        Assert.Equal(new[] { "value" }, stems);
    }

    [Fact]
    public void StructArity_DirectStructInlineLayoutLeak_IsViolation()
    {
        // The Defect-B shape in a *direct* struct (not a nested Buffer): an extra inline
        // backing field the ABI's stored-instance set lacks. The gate must catch it here too.
        const string cs = """
            public unsafe partial struct FrozenPoint : ISwiftObject, IDisposable
            {
                private int x_;  // Note: Do not access this field directly - use the property accessors
                private int y_;  // Note: Do not access this field directly - use the property accessors
                private int cachedHash_;  // Note: Do not access this field directly - use the property accessors
            }
            """;
        const string abi = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "FrozenPoint", "children": [
                { "kind": "Var", "name": "x", "hasStorage": true },
                { "kind": "Var", "name": "y", "hasStorage": true }
              ]}
            ]}}
            """;
        var findings = ArtifactParityGate.ComputeFindings(cs, "", abi, Libs(), Set());

        var f = Assert.Single(findings.StructArity);
        Assert.Equal("FrozenPoint", f.Struct);
        Assert.Equal(new[] { "cachedHash" }, f.BufferExtra);
        Assert.Single(ArtifactParityGate.DiffAgainstBaseline(findings, EmptyBaseline), x => x.Gate == "struct-arity");
    }

    [Fact]
    public void StructArity_GenericHost_NotFlagged_NoFixedLayout()
    {
        // A generic frozen struct has no fixed Buffer — it routes through PayloadBuffer<T>.
        // The gate must not invent an arity finding for it even when the ABI lists props.
        const string cs = """
            public partial class BlittableElementBuffer<T> : ISwiftObject, ISwiftStruct, IDisposable
            {
                public unsafe PayloadBuffer<BlittableElementBuffer<T>> PayloadBuffer => default;
                private T Value_Get() => default;
            }
            """;
        const string abi = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "BlittableElementBuffer", "children": [
                { "kind": "Var", "name": "element", "hasStorage": true }
              ]}
            ]}}
            """;
        var findings = ArtifactParityGate.ComputeFindings(cs, "", abi, Libs(), Set());
        Assert.Empty(findings.StructArity);
        Assert.DoesNotContain("BlittableElementBuffer", ArtifactParityGate.ParseCsLayoutStems(cs).Keys);
    }

    [Fact]
    public void ParseCsLayoutStems_UnionsBufferHostAndDirectStruct()
    {
        const string cs = """
            public partial class HostWithBuffer : ISwiftStruct {
                public struct Buffer { private int a_; }
            }
            public unsafe partial struct DirectStruct : ISwiftObject, IDisposable {
                private int b_;  // Note: Do not access this field directly - use the property accessors
            }
            """;
        var stems = ArtifactParityGate.ParseCsLayoutStems(cs);
        Assert.Equal(new[] { "a" }, stems["HostWithBuffer"]);
        Assert.Equal(new[] { "b" }, stems["DirectStruct"]);
    }

    [Fact]
    public void ParseAbiStoredInstanceProps_MergedArrayOfDocuments_CoversBothModules()
    {
        // The harness concatenates the main + dependency ABIs as a JSON array so one
        // ParseAbiStoredInstanceProps call covers both modules' structs.
        const string main = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "MainStruct", "children": [
                { "kind": "Var", "name": "m", "hasStorage": true } ]}
            ]}}
            """;
        const string dep = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "DepStruct", "children": [
                { "kind": "Var", "name": "d", "hasStorage": true } ]}
            ]}}
            """;
        var props = ArtifactParityGate.ParseAbiStoredInstanceProps("[" + main + "," + dep + "]");
        Assert.Equal(new[] { "m" }, props["MainStruct"]);
        Assert.Equal(new[] { "d" }, props["DepStruct"]);
    }

    [Fact]
    public void ParseAbiStoredInstanceProps_CrossModuleSplitStruct_FirstNonEmptyWins()
    {
        // The real cross-module shape: an importing module re-emits an imported struct
        // FIRST, carrying only a computed extension member (no storage); the home module's
        // doc carries the real stored props SECOND. "First decl wins" would mask the layout
        // and fire a false struct-arity violation — first-NON-EMPTY-wins recovers [x, y].
        const string importer = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "DependencyPoint", "children": [
                { "kind": "Var", "name": "manhattanDistance", "hasStorage": false } ]}
            ]}}
            """;
        const string home = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "DependencyPoint", "children": [
                { "kind": "Var", "name": "x", "hasStorage": true },
                { "kind": "Var", "name": "y", "hasStorage": true } ]}
            ]}}
            """;
        var props = ArtifactParityGate.ParseAbiStoredInstanceProps("[" + importer + "," + home + "]");
        Assert.Equal(new[] { "x", "y" }, props["DependencyPoint"]);
    }

    [Fact]
    public void ParseAbiStoredInstanceProps_DistinctSameNameStructs_DoNotCrossContaminate()
    {
        // Two GENUINELY-DISTINCT structs share the simple name "Tag" (nested under different
        // hosts), each with its OWN stored vars. A union would merge them to [a1, a2, b1] and
        // could mask a real C# layout leak whose field name equals the other struct's prop.
        // First-non-empty-wins keeps the first decl's set intact — NO cross-contamination.
        const string docA = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "Tag", "children": [
                { "kind": "Var", "name": "a1", "hasStorage": true },
                { "kind": "Var", "name": "a2", "hasStorage": true } ]}
            ]}}
            """;
        const string docB = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "Tag", "children": [
                { "kind": "Var", "name": "b1", "hasStorage": true } ]}
            ]}}
            """;
        var props = ArtifactParityGate.ParseAbiStoredInstanceProps("[" + docA + "," + docB + "]");
        Assert.Equal(new[] { "a1", "a2" }, props["Tag"]);

        // A C# "Tag" buffer that leaks `b1` (the OTHER Tag's stored prop) is still flagged
        // as extra — the masking the union would have allowed does not happen.
        const string cs = """
            public partial class Tag : ISwiftObject, ISwiftStruct, IDisposable
            {
                public struct Buffer
                {
                    private int a1_;
                    private int a2_;
                    private int b1_;
                }
            }
            """;
        var findings = ArtifactParityGate.ComputeFindings(cs, "", "[" + docA + "," + docB + "]", Libs(), Set());
        var f = Assert.Single(findings.StructArity);
        Assert.Equal("Tag", f.Struct);
        Assert.Equal(new[] { "b1" }, f.BufferExtra);
    }

    [Fact]
    public void StructArity_CrossModuleSplitStruct_NoFalseViolation()
    {
        // End-to-end of the first-non-empty-wins fix: the C# direct struct's backing fields
        // [x, y] match the home-module storage even though the importer's re-emission (merged
        // first) lists only a computed member. The gate must NOT flag DependencyPoint.
        const string cs = """
            public unsafe partial struct DependencyPoint : ISwiftObject, IDisposable
            {
                private double x_;  // Note: Do not access this field directly - use the property accessors
                private double y_;  // Note: Do not access this field directly - use the property accessors
            }
            """;
        const string importer = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "DependencyPoint", "children": [
                { "kind": "Var", "name": "manhattanDistance", "hasStorage": false } ]}
            ]}}
            """;
        const string home = """
            { "ABIRoot": { "children": [
              { "declKind": "Struct", "name": "DependencyPoint", "children": [
                { "kind": "Var", "name": "x", "hasStorage": true },
                { "kind": "Var", "name": "y", "hasStorage": true } ]}
            ]}}
            """;
        var findings = ArtifactParityGate.ComputeFindings(cs, "", "[" + importer + "," + home + "]", Libs(), Set());
        Assert.Empty(findings.StructArity);
    }

    // ===================================================================
    //  Gate 3 — vtable parity (Finding 8 + Defect C)
    // ===================================================================

    [Fact]
    public void ParseCsVtables_ReadsSwiftVTableMirror_NotLocalVTable()
    {
        const string cs = """
            private struct VariadicItemSwiftVTable
            {
                public IntPtr csVTHandle;
                public IntPtr func_itemName_get;
            }
            private struct VariadicItemLocalVTable
            {
                public delegate* unmanaged[Cdecl]<void> slot0;
            }
            """;
        var vt = ArtifactParityGate.ParseCsVtables(cs);
        Assert.Equal(new[] { "csVTHandle", "func_itemName_get" }, vt["VariadicItem"]);
        Assert.DoesNotContain("VariadicItemLocal", vt.Keys); // Local mirror not picked up
    }

    [Fact]
    public void ParseSwiftVtables_ReadsOrderedVarFields()
    {
        const string sw = """
            fileprivate struct VariadicItem_vtable {
                var csVTHandle: OpaquePointer? = nil
                var func_itemName_get: (@convention(c)(OpaquePointer?, UnsafeRawPointer) -> UnsafeRawPointer)?
            }
            """;
        var vt = ArtifactParityGate.ParseSwiftVtables(sw);
        Assert.Equal(new[] { "csVTHandle", "func_itemName_get" }, vt["VariadicItem"]);
    }

    [Fact]
    public void Vtable_ExtraTrailingSlot_IsMismatch_Finding8()
    {
        // Finding 8: C# mirror over-emits a slot the Swift vtable lacks (Self-typed
        // requirement that can't dispatch to a C# conformer).
        const string cs = "private struct SummableSwiftVTable\n{ public IntPtr csVTHandle; public IntPtr func_add_0; }";
        const string sw = "fileprivate struct Summable_vtable {\n var csVTHandle: OpaquePointer? = nil\n}";

        var findings = ArtifactParityGate.ComputeFindings(cs, sw, EmptyAbi, Libs(), Set());
        var f = Assert.Single(findings.VtableFieldMismatches);
        Assert.Equal("Summable", f.Protocol);
        Assert.Equal(new[] { "csVTHandle", "func_add_0" }, f.CsFields);
        Assert.Equal(new[] { "csVTHandle" }, f.SwiftFields);
    }

    [Fact]
    public void Vtable_OptionalBeforeRequired_SkewsSlots_DefectC()
    {
        // Defect C: an @objc optional member emits a C# slot the Swift vtable omits, so the
        // required member lands one index higher in C# than in Swift → wrong-slot dispatch.
        const string cs = "private struct OptionalCallbackDelegateSwiftVTable\n{ public IntPtr csVTHandle; public IntPtr func_optionalLabel_get; public IntPtr func_didFireRequired_0; }";
        const string sw = "fileprivate struct OptionalCallbackDelegate_vtable {\n var csVTHandle: OpaquePointer? = nil\n var func_didFireRequired_0: (@convention(c)(OpaquePointer?) -> Void)?\n}";

        var findings = ArtifactParityGate.ComputeFindings(cs, sw, EmptyAbi, Libs(), Set());
        var f = Assert.Single(findings.VtableFieldMismatches);
        // The required slot occupies a different index in each language — the corruption signature.
        Assert.NotEqual(f.CsFields.ToList().IndexOf("func_didFireRequired_0"),
                        f.SwiftFields.ToList().IndexOf("func_didFireRequired_0"));
    }

    [Fact]
    public void Vtable_IdenticalFieldLists_NoMismatch()
    {
        const string cs = "private struct PSwiftVTable\n{ public IntPtr csVTHandle; public IntPtr func_x_get; }";
        const string sw = "fileprivate struct P_vtable {\n var csVTHandle: OpaquePointer? = nil\n var func_x_get: (() -> Void)?\n}";
        var findings = ArtifactParityGate.ComputeFindings(cs, sw, EmptyAbi, Libs(), Set());
        Assert.Empty(findings.VtableFieldMismatches);
    }

    [Fact]
    public void Vtable_SwiftOnly_IsAlwaysAViolation_EvenWhenBaselined()
    {
        // A Swift {P}_vtable with no C# mirror means reverse dispatch is unbindable — never
        // baselineable, so it fails the gate even if a (mis-authored) baseline tried to list it.
        const string sw = "fileprivate struct Lonely_vtable {\n var csVTHandle: OpaquePointer? = nil\n}";
        var findings = ArtifactParityGate.ComputeFindings(EmptyCs, sw, EmptyAbi, Libs(), Set());
        Assert.Equal(new[] { "Lonely" }, findings.VtableSwiftOnly);

        // Even a baseline that names every other category cannot suppress a swift-only.
        var baseline = new ArtifactParityGate.ParityBaseline { VtableCsOnlyKnown = new() { "Lonely" } };
        var v = Assert.Single(ArtifactParityGate.DiffAgainstBaseline(findings, baseline), x => x.Gate == "vtable-swift-only");
        Assert.Contains("Lonely", v.Detail);
    }

    [Fact]
    public void Vtable_CsOnly_IsBaselineable()
    {
        const string cs = "private struct MarkerSwiftVTable\n{ public IntPtr csVTHandle; }";
        var findings = ArtifactParityGate.ComputeFindings(cs, "", EmptyAbi, Libs(), Set());
        Assert.Equal(new[] { "Marker" }, findings.VtableCsOnly);

        Assert.Single(ArtifactParityGate.DiffAgainstBaseline(findings, EmptyBaseline), v => v.Gate == "vtable-cs-only");
        var baseline = new ArtifactParityGate.ParityBaseline { VtableCsOnlyKnown = new() { "Marker" } };
        Assert.DoesNotContain(ArtifactParityGate.DiffAgainstBaseline(findings, baseline), v => v.Gate == "vtable-cs-only");
    }

    [Fact]
    public void Vtable_BaselinedMismatchThatGetsWorse_RetripsGate()
    {
        // A baselined protocol whose field set changes (e.g. an extra bogus slot appears)
        // produces a NEW Key → the baseline no longer absorbs it.
        const string cs = "private struct SummableSwiftVTable\n{ public IntPtr csVTHandle; public IntPtr func_add_0; public IntPtr func_extra_1; }";
        const string sw = "fileprivate struct Summable_vtable {\n var csVTHandle: OpaquePointer? = nil\n}";
        var findings = ArtifactParityGate.ComputeFindings(cs, sw, EmptyAbi, Libs(), Set());

        // Baseline recorded the OLD (2-field) shape.
        var baseline = new ArtifactParityGate.ParityBaseline
        {
            VtableFieldKnownMismatches = new()
            {
                new ArtifactParityGate.ParityBaseline.VtableBaselineEntry
                {
                    Protocol = "Summable",
                    CsFields = new() { "csVTHandle", "func_add_0" },
                    SwiftFields = new() { "csVTHandle" },
                },
            },
        };
        Assert.Single(ArtifactParityGate.DiffAgainstBaseline(findings, baseline), v => v.Gate == "vtable-parity");
    }

    // ===================================================================
    //  Baseline ratchet: seed → green; round-trip
    // ===================================================================

    [Fact]
    public void Seed_ThenDiff_IsGreen_ExceptSwiftOnly()
    {
        // Compose every baselineable divergence plus a swift-only one.
        const string cs = """
            [LibraryImport("L", EntryPoint = "miss")]
            private static partial int A(int x);
            public int Use() => A(1);
            private struct MarkerSwiftVTable
            { public IntPtr csVTHandle; }
            private struct SummableSwiftVTable
            { public IntPtr csVTHandle; public IntPtr func_add_0; }
            """;
        const string sw = """
            fileprivate struct Summable_vtable {
                var csVTHandle: OpaquePointer? = nil
            }
            fileprivate struct Lonely_vtable {
                var csVTHandle: OpaquePointer? = nil
            }
            """;
        var findings = ArtifactParityGate.ComputeFindings(cs, sw, EmptyAbi,
            Libs(("L", NoSyms)), Set("SBW_orphan"));

        var baseline = ArtifactParityGate.ParityBaseline.Seed(findings, "deadbeef", "test");
        var remaining = ArtifactParityGate.DiffAgainstBaseline(findings, baseline);

        // Everything baselineable is absorbed; only the never-baselineable swift-only remains.
        Assert.All(remaining, v => Assert.Equal("vtable-swift-only", v.Gate));
        Assert.Single(remaining);
    }

    [Fact]
    public void Baseline_JsonRoundTrip_PreservesAllCategories()
    {
        var baseline = new ArtifactParityGate.ParityBaseline
        {
            GitSha = "abc123",
            Description = "round-trip",
            SymbolForwardKnownMissing = new() { ["L"] = new() { "s1", "s2" } },
            SymbolReverseKnownOrphans = new() { "SBW_orphan" },
            StructArityKnownMismatches = new() { "S|extra=version|missing=" },
            VtableFieldKnownMismatches = new()
            {
                new ArtifactParityGate.ParityBaseline.VtableBaselineEntry
                {
                    Protocol = "Summable", CsFields = new() { "csVTHandle", "func_add_0" }, SwiftFields = new() { "csVTHandle" },
                },
            },
            VtableCsOnlyKnown = new() { "Marker" },
        };

        var round = ArtifactParityGate.ParityBaseline.Parse(baseline.ToJson());

        Assert.Equal("abc123", round.GitSha);
        Assert.Equal(new[] { "s1", "s2" }, round.SymbolForwardKnownMissing["L"]);
        Assert.Equal(new[] { "SBW_orphan" }, round.SymbolReverseKnownOrphans);
        Assert.Equal("S|extra=version|missing=", Assert.Single(round.StructArityKnownMismatches));
        Assert.Equal("Marker", Assert.Single(round.VtableCsOnlyKnown));
        var vt = Assert.Single(round.VtableFieldKnownMismatches);
        Assert.Equal("Summable", vt.Protocol);
        // Key recomputes identically after round-trip (seed/diff agreement invariant).
        Assert.Equal("Summable|cs=csVTHandle,func_add_0|swift=csVTHandle", vt.Key);
    }

    [Fact]
    public void Parse_EmptyOrWhitespace_ReturnsEmptyBaseline()
    {
        Assert.Empty(ArtifactParityGate.ParityBaseline.Parse("").SymbolReverseKnownOrphans);
        Assert.Empty(ArtifactParityGate.ParityBaseline.Parse("   ").VtableCsOnlyKnown);
    }

    [Fact]
    public void MatchingBrace_HandlesNesting()
    {
        const string s = "a { b { c } d } e";
        var open = s.IndexOf('{');
        var close = ArtifactParityGate.MatchingBrace(s, open);
        Assert.Equal(s.LastIndexOf('}'), close);
    }

    // ===================================================================
    //  Test helpers
    // ===================================================================

    private const string EmptyCs = "// no externs";
    private const string EmptyAbi = "{ \"ABIRoot\": { \"children\": [] } }";

    private static ArtifactParityGate.ParityFindings Compute(
        string cs, IReadOnlyDictionary<string, IReadOnlySet<string>> symbolsByLibrary)
        => ArtifactParityGate.ComputeFindings(cs, "", EmptyAbi, symbolsByLibrary, Set());

    // Build a library→symbol-set map. Each tuple is (library, [its exported symbols]).
    // `Libs()` yields an empty map.
    private static IReadOnlyDictionary<string, IReadOnlySet<string>> Libs(params (string Lib, string[] Syms)[] entries)
        => entries.ToDictionary(
            e => e.Lib,
            e => (IReadOnlySet<string>)new HashSet<string>(e.Syms, System.StringComparer.Ordinal),
            System.StringComparer.Ordinal);

    private static readonly string[] NoSyms = System.Array.Empty<string>();

    private static IReadOnlySet<string> Set(params string[] s)
        => new HashSet<string>(s, System.StringComparer.Ordinal);
}
