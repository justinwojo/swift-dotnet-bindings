// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Initializers;

/// <summary>
/// Runtime gate for a trimmed constructor over an existential parameter beside a closure sibling
/// with the same label (<c>ExistentialClosureInitSiblings</c>).
///
/// The one-argument <c>(IProvidedCountSource)</c> constructor calls Swift as
/// <c>init(entries: provider)</c>. No existential converts to a function, so the closure
/// initializer cannot take that call; the constructor was once declined as if it could, and this
/// file then failed to compile. It must reach the existential initializer and leave
/// <c>version</c> to Swift's default.
/// </summary>
public class ExistentialClosureInitSiblingsTests : TestBase
{
    public ExistentialClosureInitSiblingsTests(TestResults results) : base(results) { }

    public void TestTrimmedProviderConstructorReachesTheExistentialInitializer()
    {
        using var provider = new FixedProvidedCountSource(providedCount: 4);
        using var value = new ExistentialClosureInitSiblings(provider);
        AssertEqual("provider", value.Source, "(provider) reaches init(version:entries:) over the existential");
        AssertEqual(1, (int)value.Version, "(provider) leaves version to Swift's default (1)");
        AssertEqual(4, (int)value.Count, "(provider) passes the provider");
    }

    sealed class ManagedCountSource : IProvidedCountSource
    {
        public int ProvidedCount => 6;
    }

    public void TestTrimmedProviderConstructorTakesACSharpConformer()
    {
        using var value = new ExistentialClosureInitSiblings(new ManagedCountSource());
        AssertEqual("provider", value.Source, "(C# conformer) reaches the existential initializer");
        AssertEqual(1, (int)value.Version, "(C# conformer) leaves version to Swift's default (1)");
        AssertEqual(6, (int)value.Count, "Swift reads the C# conformer's providedCount");
    }

    public void TestEmptyConstructorReachesTheArrayInitializer()
    {
        using var value = new ExistentialClosureInitSiblings();
        AssertEqual("array", value.Source, "() reaches init(version:entries:) over the array");
        AssertEqual(1, (int)value.Version, "() leaves version to Swift's default (1)");
        AssertEqual(0, (int)value.Count, "() leaves entries to Swift's default ([])");
    }
}
