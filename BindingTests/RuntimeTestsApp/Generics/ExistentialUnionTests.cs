// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Tests for existential union projection — PAT protocols with known conformers
/// are returned as ExistentialUnion with try-cast to each concrete conformer.
/// </summary>
public class ExistentialUnionTests : TestBase
{
    public ExistentialUnionTests(TestResults results) : base(results) { }

    public void TestAttributeHolder_ColorAttribute_Label()
    {
        var holder = new AttributeHolder(color: "red");
        AssertEqual("color", holder.AttributeLabel, "ColorAttribute label");
    }

    public void TestAttributeHolder_SizeAttribute_Label()
    {
        var holder = new AttributeHolder(size: 42);
        AssertEqual("size", holder.AttributeLabel, "SizeAttribute label");
    }

    public void TestAttributeHolder_FlagAttribute_Label()
    {
        var holder = new AttributeHolder(flag: true);
        AssertEqual("flag", holder.AttributeLabel, "FlagAttribute label");
    }

    // Finding 21 / Session 12: the PAT existential `any AttributeKind` has known conformers
    // (ColorAttribute / SizeAttribute / FlagAttribute), so `holder.Attribute` (a get-only property in a
    // pure-read position) projects to Swift.Runtime.ExistentialUnion — the read-only forward try-cast
    // wrapper — instead of degrading to `object` with a SWIFTBIND023 marker. The strongly-typed
    // `ExistentialUnion union = holder.Attribute;` is itself a compile-time projection assertion: if the
    // property ever regressed to `object`, this would fail to convert and the compile gate would go red.

    public void TestExistentialUnion_TryCast_ColorAttribute()
    {
        var holder = new AttributeHolder(color: "blue");
        ExistentialUnion union = holder.Attribute;
        AssertNotNull(union, "Attribute should project to ExistentialUnion");
        var color = union.As<ColorAttribute>();
        AssertNotNull(color, "TryCast to ColorAttribute should succeed");
    }

    public void TestExistentialUnion_TryCast_SizeAttribute()
    {
        var holder = new AttributeHolder(size: 100);
        ExistentialUnion union = holder.Attribute;
        AssertNotNull(union, "Attribute should project to ExistentialUnion");
        var size = union.As<SizeAttribute>();
        AssertNotNull(size, "TryCast to SizeAttribute should succeed");
    }

    public void TestExistentialUnion_TryCast_WrongType_ReturnsNull()
    {
        var holder = new AttributeHolder(color: "green");
        ExistentialUnion union = holder.Attribute;
        var size = union.As<SizeAttribute>();
        AssertNull(size, "TryCast to wrong type should return null");
    }

    // Regression guard: ExistentialUnion.As<T> against a RESILIENT (non-@frozen) struct conformer.
    // ResilientAttribute is non-@frozen, so under library evolution it is resilient and the generator
    // projects it as a non-frozen "ClassWithOpaquePayload" whose NewFromPayload ADOPTS the incoming
    // pointer into a SwiftSafeHandle (freed via NativeMemory.Free on dispose). Its four String fields
    // exceed the 3-word inline existential buffer, so the value lives OUT-OF-LINE in a swift_allocBox.
    // The pre-fix As<T> out-of-line branch handed the borrowed swift_projectBox interior pointer (owned
    // by the box, +1) straight to that adopt-semantics constructor — so disposing the projected value
    // ran a value-witness Destroy over box-owned storage and then NativeMemory.Free'd a box interior
    // that C# never allocated: an invalid free / use-after-free. The read must round-trip the value out
    // of the existential AND Dispose must not crash.
    public void TestExistentialUnion_As_ResilientNonFrozenConformer_OutOfLine_NoInvalidFree()
    {
        // Pin the branch this test exercises so it can never silently degrade into duplicate
        // coverage of the inline path: ResilientAttribute (four String fields, size > 24) must be
        // stored OUT-OF-LINE in the existential.
        AssertFalse(IsStoredInline<ResilientAttribute>(),
            "ResilientAttribute (4 String fields) must be stored OUT-OF-LINE — this test guards As<T>'s out-of-line branch");

        ExistentialUnion union = Functions.MakeResilientAttribute("blue");
        AssertNotNull(union, "Resilient PAT free-function return should project to ExistentialUnion");
        var attr = union.As<ResilientAttribute>();
        AssertNotNull(attr, "TryCast to ResilientAttribute should succeed");
        AssertEqual("blue", attr!.Value.ToString(), "ResilientAttribute value round-trips out of the existential");
        AssertEqual("resilient", attr.Label.ToString(), "ResilientAttribute label round-trips out of the existential");
        attr.Dispose();   // pre-fix: invalid free of the swift_projectBox box interior
    }

    // Coverage of the INLINE branch under adopt semantics: SmallResilientAttribute ({ String label,
    // Int32 value }) has value-witness size 20 (≤ the 3-word, 24-byte inline buffer) and is bitwise-
    // takable (a String moves by memcpy), so IsNonInline is clear and the existential stores it INLINE.
    // The pre-fix inline branch passed the address of a stack-local container copy to the same adopt-
    // semantics NewFromPayload, so disposing the projected wrapper would NativeMemory.Free a stack
    // address. The IsStoredInline assertion pins this to the inline branch so it stays distinct from the
    // out-of-line ResilientAttribute test; if Swift's existential layout ever changed it would fail
    // loudly here rather than silently duplicate out-of-line coverage.
    public void TestExistentialUnion_As_SmallResilientNonFrozenConformer_Inline_NoInvalidFree()
    {
        AssertTrue(IsStoredInline<SmallResilientAttribute>(),
            "SmallResilientAttribute (String + Int32, size 20, bitwise-takable) must be stored INLINE — this test guards As<T>'s inline branch");

        ExistentialUnion union = Functions.MakeSmallResilientAttribute(7);
        AssertNotNull(union, "Small resilient PAT free-function return should project to ExistentialUnion");
        var attr = union.As<SmallResilientAttribute>();
        AssertNotNull(attr, "TryCast to SmallResilientAttribute should succeed");
        AssertEqual(7, attr!.Value, "SmallResilientAttribute value round-trips out of the existential");
        AssertEqual("smallResilient", attr.Label.ToString(), "SmallResilientAttribute label round-trips out of the existential");
        attr.Dispose();   // pre-fix: invalid free of a stack-local address
    }

    // The fix routes BOTH As<T> branches (inline / out-of-line) through SwiftMarshal.ExtractCopiedValue<T>,
    // and ExtractCopiedValue's behaviour keys on the conformer SHAPE (adopt / copy / class), independently
    // of which branch produced the borrowed source pointer. That is a 2x2 matrix — {inline, out-of-line}
    // storage x {adopt, copy} extraction — and each test below pins exactly one cell with IsStoredInline<T>:
    //   * inline  ADOPT  -> SmallResilientAttribute (AssertTrue  IsStoredInline)
    //   * outline ADOPT  -> ResilientAttribute      (AssertFalse IsStoredInline)
    //   * outline COPY   -> ColorAttribute          (AssertFalse IsStoredInline)  [this test]
    //   * inline  COPY   -> SizeAttribute           (AssertTrue  IsStoredInline)  [next test]
    // The COPY shape is the frozen-struct-projected-as-class case: @frozen with a String field ->
    // ClassWithBufferStruct, whose NewFromPayload allocates its own buffer + InitializeWithCopy. The
    // pre-existing TryCast tests read but never disposed; this locks the COPY extraction's ownership
    // balance (extracted wrapper owns its own buffer, no double-free).

    // M2 guard, out-of-line COPY cell: ColorAttribute ({ String, String }, size 32 > 24) is stored
    // OUT-OF-LINE and projects to the frozen COPY shape.
    public void TestExistentialUnion_As_FrozenConformer_Copy_DisposesCleanly()
    {
        AssertFalse(IsStoredInline<ColorAttribute>(),
            "ColorAttribute (2 String fields, size 32) must be stored OUT-OF-LINE — this test guards the out-of-line COPY cell");

        var holder = new AttributeHolder(color: "teal");
        ExistentialUnion union = holder.Attribute;
        var color = union.As<ColorAttribute>();
        AssertNotNull(color, "TryCast to ColorAttribute should succeed");
        AssertEqual("teal", color!.Value.ToString(), "ColorAttribute value round-trips out of the existential");
        color.Dispose();   // frozen COPY shape: extracted wrapper owns its buffer; dispose must be clean
    }

    // Inline COPY cell — the last matrix cell: SizeAttribute ({ String label, Int32 value }, size 20 <= 24,
    // bitwise-takable) is stored INLINE, and being @frozen with a String field it projects to the same COPY
    // shape as ColorAttribute (NewFromPayload NativeMemory.Alloc + InitializeWithCopy). This is the only
    // cell exercising the INLINE branch's stack-local source feeding a COPY extraction; its Dispose locks
    // that the wrapper owns its own buffer (no free of the existential's inline payload bytes).
    public void TestExistentialUnion_As_FrozenInlineConformer_Copy_DisposesCleanly()
    {
        AssertTrue(IsStoredInline<SizeAttribute>(),
            "SizeAttribute (String + Int32, size 20) must be stored INLINE — this test guards the inline COPY cell");

        var holder = new AttributeHolder(size: 42);
        ExistentialUnion union = holder.Attribute;
        var size = union.As<SizeAttribute>();
        AssertNotNull(size, "TryCast to SizeAttribute should succeed");
        AssertEqual(42, size!.Value, "SizeAttribute value round-trips out of the existential");
        size.Dispose();   // frozen COPY shape via the inline branch: extracted wrapper owns its buffer; dispose must be clean
    }

    // Finding 21 / Session 12 finding #1: the SETTABLE PAT property MutableAttributeHolder.Current
    // keeps BOTH its public type and its backing getter at `object` (ExistentialUnion is return-only,
    // no input marshalling). The strongly-typed `object current = holder.Current;` plus the
    // `holder.Current = current;` round-trip is the runtime hazard the review flagged: if the getter
    // had projected to ExistentialUnion under an `object` property, this round-trip would feed an
    // ExistentialUnion back into the setter's input marshalling. Assert it round-trips without crashing.
    public void TestMutableAttributeHolder_ObjectRoundTrip_DoesNotCrash()
    {
        var holder = new MutableAttributeHolder(color: "blue");
        object current = holder.Current;
        AssertNotNull(current, "Settable PAT property getter should return a non-null object");
        holder.Current = current;   // round-trip back through the setter — must not crash
        object again = holder.Current;
        AssertNotNull(again, "Settable PAT property should still read back after a round-trip set");
    }

    /// <summary>
    /// Mirrors <c>ExistentialUnion.As&lt;T&gt;</c>'s inline-vs-out-of-line criterion: a value is stored
    /// inline in an existential only when it fits the 3-word payload buffer AND its value-witness
    /// IsNonInline flag is clear. Lets the resilient-conformer tests assert WHICH storage branch they
    /// actually exercise, keeping the inline and out-of-line guards distinct.
    /// </summary>
    private static unsafe bool IsStoredInline<T>() where T : class, ISwiftObject
    {
        var vwt = TypeMetadata.GetTypeMetadataOrThrow<T>().ValueWitnessTable;
        var size = (int)vwt->Size;
        var isNonInline = (vwt->Flags & ValueWitnessFlags.IsNonInline) != 0;
        return size <= ExistentialContainerFactory.MaxInlinePayloadSize && !isNonInline;
    }
}
