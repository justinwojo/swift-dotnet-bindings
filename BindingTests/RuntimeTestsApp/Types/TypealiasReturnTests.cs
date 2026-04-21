// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Types;

/// <summary>
/// Verifies that methods whose return type is a nested typealias (e.g.
/// <c>SHA256.Digest = SHA256Digest</c>, <c>HMAC&lt;H&gt;.MAC =
/// HashedAuthenticationCode&lt;H&gt;</c>) are emitted and round-trip
/// correctly. Before the parser learned the <c>TypeNameAlias</c> node
/// kind these methods were silently dropped at parse time.
/// </summary>
public class TypealiasReturnTests : TestBase
{
    public TypealiasReturnTests(TestResults results) : base(results) { }

    public void TestNonGenericTypealiasReturn()
    {
        using var producer = new AliasProducer(seed: 21);
        using var payload = producer.MakePayload();
        AssertEqual(42, payload.Value, "AliasProducer.makePayload should return seed * 2");
    }

    /// <summary>
    /// Generic alias case (HMAC&lt;H&gt;.MAC = HashedAuthenticationCode&lt;H&gt; pattern).
    /// <c>AliasGenericProducer&lt;T&gt;.makeWrapped()</c> returns <c>Wrapped</c>, which is
    /// a nested typealias resolving to <c>AliasGenericPayload&lt;T&gt;</c>. The parser
    /// must unwrap both the nested alias AND propagate the parent's generic argument
    /// through to the underlying bound-generic return. Round-trip confirms the method
    /// was bound and the payload reaches managed code intact.
    /// </summary>
    public void TestGenericTypealiasReturn()
    {
        using var seed = new SongItem();
        using var producer = new AliasGenericProducer<SongItem>(seed);
        using var wrapped = producer.MakeWrapped();
        using var element = wrapped.Element;
        AssertNotNull(element, "AliasGenericProducer<SongItem>.makeWrapped should return a payload whose element unwraps to SongItem");
    }

    /// <summary>
    /// Class-T case of <c>AliasGenericPayload&lt;T&gt;.element</c>: Swift writes the
    /// class instance pointer into the indirect-result buffer, so the generated getter
    /// must dereference the buffer to recover the class handle. The struct case above
    /// doesn't exercise this because <c>SwiftSafeHandle</c> treats the buffer as the
    /// handle directly.
    /// </summary>
    public void TestGenericTypealiasReturn_ClassConformer()
    {
        using var seed = new AliasClassItem(tag: 7);
        using var producer = new AliasGenericProducer<AliasClassItem>(seed);
        using var wrapped = producer.MakeWrapped();
        using var element = wrapped.Element;
        AssertEqual(7, element.Tag, "AliasGenericProducer<AliasClassItem>.makeWrapped should round-trip the class instance");
    }
}
