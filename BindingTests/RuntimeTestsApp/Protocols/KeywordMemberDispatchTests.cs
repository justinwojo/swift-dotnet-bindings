// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// Reverse-dispatch tests for a protocol whose member NAMES are Swift keywords
/// (<c>repeat</c> property, <c>class()</c> method).
///
/// A C# class implementing <c>IKeywordMemberDelegate</c> is wrapped in an
/// EveryProtocol conformance whose witness members must be declared
/// <c>public var `repeat`</c> / <c>public func `class`()</c> — the declaration
/// sites need backtick-escaping or the generated Swift fails to parse, and the
/// conformance must match the original Swift requirement (a mangled <c>_class</c>
/// would not conform). This exercises the full round-trip: C# value → Swift
/// existential read through the keyword-named members → back to C#.
///
/// Distinct from <c>SiblingPropertyDispatchTests</c>, which covers keyword
/// argument LABELS; here the member names themselves are the keywords.
/// </summary>
public class KeywordMemberDispatchTests : TestBase
{
    public KeywordMemberDispatchTests(TestResults results) : base(results) { }

    /// <summary>
    /// Read the keyword-named property (<c>repeat</c>) through a Swift existential.
    /// The router dispatches into the C# proxy's <c>Repeat</c> getter via the
    /// witness <c>public var `repeat`</c>; without the escape the conformance
    /// would not compile.
    /// </summary>
    public void TestReadKeywordNamedProperty()
    {
        var router = new KeywordMemberRouter();
        router.Delegate = new KeywordMemberDelegateImpl(repeatValue: 42, classValue: 7);
        var result = router.ReadRepeat();
        AssertEqual(42, result, "Keyword-named property `repeat` round-trips through Swift existential");
    }

    /// <summary>
    /// Invoke the keyword-named method (<c>class()</c>) through a Swift existential.
    /// Dispatch routes through the witness <c>public func `class`()</c>.
    /// </summary>
    public void TestCallKeywordNamedMethod()
    {
        var router = new KeywordMemberRouter();
        router.Delegate = new KeywordMemberDelegateImpl(repeatValue: 5, classValue: 99);
        var result = router.CallClass();
        AssertEqual(99, result, "Keyword-named method `class()` round-trips through Swift existential");
    }

    /// <summary>
    /// No delegate set: the Swift router's <c>delegate?.`repeat` ?? -1</c> and
    /// <c>delegate?.`class`() ?? -1</c> sentinels must surface as -1. Confirms the
    /// optional-existential path is wired and the keyword members are reached only
    /// when a delegate is present.
    /// </summary>
    public void TestNilDelegateReturnsSentinel()
    {
        var router = new KeywordMemberRouter();
        AssertEqual(-1, router.ReadRepeat(), "Nil delegate yields -1 sentinel for `repeat`");
        AssertEqual(-1, router.CallClass(), "Nil delegate yields -1 sentinel for `class()`");
    }
}

internal class KeywordMemberDelegateImpl : IKeywordMemberDelegate
{
    private readonly int _repeat;
    private readonly int _class;

    public KeywordMemberDelegateImpl(int repeatValue, int classValue)
    {
        _repeat = repeatValue;
        _class = classValue;
    }

    public int Repeat => _repeat;

    public int Get_class() => _class;
}
