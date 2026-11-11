// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Generic types whose parameters carry multi-letter source names (<c>Value</c>, <c>Key</c>).
/// The generator must treat a generic parameter as one by its declaration, not by how short its
/// name is; a stored property typed by such a parameter was dropped as an unresolvable type when
/// the ABI spelled it by source name. These read every such property back through a concrete
/// instantiation.
/// </summary>
public class SourceNamedGenericParameterTests : TestBase
{
    public SourceNamedGenericParameterTests(TestResults results) : base(results) { }

    public void TestValueProperty_IntAndString()
    {
        using var intBox = Functions.MakeSourceNamedIntBox(64);
        AssertEqual(64L, (long)intBox.Value, "SourceNamedBox<Int>.value");
        using var stringBox = Functions.MakeSourceNamedStringBox("cadenza");
        using var stringValue = stringBox.Value;
        AssertEqual("cadenza", stringValue.ToString(), "SourceNamedBox<String>.value");
    }

    public void TestTwoParameterStruct()
    {
        using var pair = Functions.MakeSourceNamedPair("bars", 32);
        using var first = pair.First;
        AssertEqual("bars", first.ToString(), "SourceNamedPair<Key, Value>.first");
        AssertEqual(32L, (long)pair.Second, "SourceNamedPair<Key, Value>.second");
    }
}
