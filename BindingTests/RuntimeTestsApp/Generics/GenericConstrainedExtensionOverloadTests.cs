// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Regression lock for a 0.11.0 wrapper-compile failure and
/// the orthogonal Optional&lt;Any&gt; @_cdecl ABI fix:
///
/// (1) Constrained-extension overload: the GSM wrapper for a method declared
///     in `extension Mapper where N: Narrower` was being emitted without the
///     where-clause, so swiftc rejected the inferred result type. The fix
///     skips wrapper emission for methods whose conformances narrow the
///     parent's. The constrained sibling is correctly absent here; the
///     unconstrained body sibling on the class survives and round-trips.
///     Both siblings label their parameter <c>JSONObject</c> and erase to one
///     projected key, so the overload group is named from each member's own
///     labels + types — the survivor emits as
///     <c>MapJSONObjectWithOptionalAny</c>. It keeps that name even though its
///     collision partner is dropped later: making the name depend on which
///     siblings ultimately survive is exactly the instability the label/type
///     scheme exists to remove.
///
/// (2) Optional&lt;Any&gt; @_cdecl boundary: the body method's `JSONObject: Any?`
///     parameter exercises the path that previously read the buffer as a
///     single AnyObject pointer. The string payload below boxes through the
///     bare-Any projection as a Swift String *value* (not an object
///     reference), which is exactly the value-type case that crashed under
///     the old path. The fix reads the buffer as `Optional&lt;Any&gt;` via
///     `load(as:)`, which handles all bare-Any payload shapes uniformly.
///
/// The compile-gate side of (1) is also pinned by MethodWrapperEmitterTests.
/// </summary>
public class GenericConstrainedExtensionOverloadTests : TestBase
{
    public GenericConstrainedExtensionOverloadTests(TestResults results) : base(results) { }

    public void TestUnconstrainedMap_NonNilAny_RoundTrips()
    {
        using var mapper = Functions.MakeGenericConstrainedExtensionMapper(label: "alpha");
        // String boxes through bare-Any projection as a Swift String value-type payload
        // (inline in the ExistentialContainer), exercising the case the old wrapper crashed on.
        var result = mapper.MapJSONObjectWithOptionalAny(JSONObject: "anything");
        AssertNotNull(result, "MapJSONObjectWithOptionalAny(non-nil value-type Any) returns stored");
        AssertEqual("alpha", result!.Label, "stored label round-trips with value-type Any payload");
    }

    public void TestUnconstrainedMap_NilAny_ReturnsNone()
    {
        using var mapper = Functions.MakeGenericConstrainedExtensionMapper(label: "beta");
        var result = mapper.MapJSONObjectWithOptionalAny(JSONObject: null);
        AssertNull(result, "MapJSONObjectWithOptionalAny(nil) returns nil per source semantics");
    }
}
