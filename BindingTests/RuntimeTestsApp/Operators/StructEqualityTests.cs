// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Operators;

/// <summary>
/// Tests for struct equality operators: Tag struct with Key/Value string properties
/// and overloaded == / != operators.
/// </summary>
public class StructEqualityTests : TestBase
{
    public StructEqualityTests(TestResults results) : base(results) { }

    #region Tier 2 — Construction and Property Access

    public void TestTagConstruction()
    {
        var tag = new Tag("env", "production");
        var key = tag.Key.ToString();
        var value = tag.Value.ToString();
        AssertEqual("env", key, "Tag.Key");
        AssertEqual("production", value, "Tag.Value");
        TestLogger.Info($"Tag: Key=\"{key}\", Value=\"{value}\"");
    }

    public void TestTagKeyProperty()
    {
        var tag = new Tag("version", "1.0");
        AssertEqual("version", tag.Key.ToString(), "Tag.Key getter");
        TestLogger.Info("Tag.Key property access passed");
    }

    public void TestTagValueProperty()
    {
        var tag = new Tag("version", "1.0");
        AssertEqual("1.0", tag.Value.ToString(), "Tag.Value getter");
        TestLogger.Info("Tag.Value property access passed");
    }

    #endregion

    #region Tier 2 — Equality Operators

    public void TestTagEqualitySameValues()
    {
        var a = new Tag("env", "prod");
        var b = new Tag("env", "prod");
        AssertTrue(a == b, "Tags with same key+value are equal");
        AssertFalse(a != b, "Tags with same key+value are not unequal");
        TestLogger.Info("Tag equality (same key+value) passed");
    }

    public void TestTagInequalityDifferentKey()
    {
        var a = new Tag("env", "prod");
        var b = new Tag("stage", "prod");
        AssertTrue(a != b, "Tags with different keys are unequal");
        AssertFalse(a == b, "Tags with different keys are not equal");
        TestLogger.Info("Tag inequality (different key) passed");
    }

    public void TestTagInequalityDifferentValue()
    {
        var a = new Tag("env", "prod");
        var b = new Tag("env", "dev");
        AssertTrue(a != b, "Tags with different values are unequal");
        AssertFalse(a == b, "Tags with different values are not equal");
        TestLogger.Info("Tag inequality (different value) passed");
    }

    public void TestTagInequalityBothDifferent()
    {
        var a = new Tag("env", "prod");
        var b = new Tag("region", "us-east");
        AssertTrue(a != b, "Tags with both key+value different are unequal");
        AssertFalse(a == b, "Tags with both key+value different are not equal");
        TestLogger.Info("Tag inequality (both different) passed");
    }

    public void TestTagEqualsMethod()
    {
        var a = new Tag("key", "value");
        var b = new Tag("key", "value");
        AssertTrue(a.Equals(b), "Tag.Equals with same key+value");
        TestLogger.Info("Tag.Equals method passed");
    }

    public void TestTagEqualsMethodInequality()
    {
        var a = new Tag("key", "value1");
        var b = new Tag("key", "value2");
        AssertFalse(a.Equals(b), "Tag.Equals with different values");
        TestLogger.Info("Tag.Equals method inequality passed");
    }

    /// <summary>
    /// Equatable Defect 1: an Equatable type's GetHashCode previously returned
    /// a constant 0 and broke the
    /// Equals/GetHashCode contract in any hash-based collection. The
    /// SwiftHashable runtime helper now folds the Swift hash (when the
    /// witness resolves) or falls back to a stable structural hash —
    /// either way the key invariant must hold: byte-equal payloads share a
    /// hash and a Dictionary lookup completes successfully.
    /// </summary>
    public void TestTagGetHashCodeContract()
    {
        var a = new Tag("env", "prod");
        var b = new Tag("env", "prod");
        var c = new Tag("env", "dev");

        AssertTrue(a.Equals(b), "baseline: Equals contract");
        AssertEqual(a.GetHashCode(), b.GetHashCode(),
            "Equal Tags must produce the same hash code");

        // The 0-stub regression also degraded Dictionary<Tag, V> to a linear scan
        // that could still happen to work — assert the value is non-zero so the
        // SwiftHashable bridge is actually contributing entropy.
        AssertTrue(a.GetHashCode() != 0,
            "GetHashCode must not be the legacy 0-stub");

        var bag = new Dictionary<Tag, string>();
        bag[a] = "found";
        AssertTrue(bag.TryGetValue(b, out var via),
            "Hash-based lookup must find an Equals-equivalent key");
        AssertEqual("found", via!, "Dictionary value matches the inserted entry");
        AssertTrue(!bag.ContainsKey(c), "Different Tags must not collide");
    }

    /// <summary>
    /// Equatable Defect 1, MusicItemID-shape: Hashable is declared via
    /// `extension Tag: Hashable { ... }` rather than on the primary type
    /// declaration. The generator must still recognise the conformance and
    /// emit a real GetHashCode (no 0-stub) so the Dictionary contract holds.
    /// </summary>
    public void TestTagExtensionHashableContract()
    {
        var a = new Tag("layer", "A");
        var b = new Tag("layer", "A");
        var c = new Tag("layer", "B");

        // Sanity: the Hashable conformance must not collapse to the legacy stub.
        AssertTrue(a.GetHashCode() != 0,
            "extension-declared Hashable must produce a non-zero GetHashCode");
        AssertEqual(a.GetHashCode(), b.GetHashCode(),
            "Equal Tags must hash equally even when Hashable is from an extension");

        var set = new HashSet<Tag> { a };
        AssertTrue(set.Contains(b),
            "HashSet<Tag> must locate an Equals-equivalent value via the extension Hashable");
        AssertTrue(!set.Contains(c),
            "HashSet<Tag> must not match a different Tag");
    }

    /// <summary>
    /// Frozen struct whose Hashable conformance lives entirely in an
    /// extension. Confirms the predicate widening reaches the frozen-struct
    /// emit path, not just the non-frozen one above.
    /// </summary>
    public void TestLabeledScoreExtensionHashableContract()
    {
        var a = new LabeledScore("alpha", 42);
        var b = new LabeledScore("alpha", 42);
        var c = new LabeledScore("alpha", 99);

        AssertTrue(a.GetHashCode() != 0,
            "frozen extension-Hashable must not return the 0-stub");
        AssertEqual(a.GetHashCode(), b.GetHashCode(),
            "Equal LabeledScores must share a hash code");

        var bag = new Dictionary<LabeledScore, string> { [a] = "alpha-42" };
        AssertTrue(bag.TryGetValue(b, out var via),
            "frozen-struct Hashable extension must support Dictionary lookup");
        AssertEqual("alpha-42", via!, "Dictionary returns the inserted value");
        AssertTrue(!bag.ContainsKey(c), "Different scores must not collide");
    }

    /// <summary>
    /// Frozen struct whose Hashable conformance is added via an
    /// <c>extension PointKey: Hashable {}</c> with NO body — Swift
    /// auto-synthesises <c>hash(into:)</c> from the stored properties.
    /// Pairs with <c>TestLabeledScoreExtensionHashableContract</c> (manual
    /// hash) to lock both extension shapes. The predicate widening MUST
    /// cover both — silently regressing the synthesised form to the
    /// 0-stub would leave the manual-form coverage as false comfort.
    /// </summary>
    public void TestPointKeySynthesisedExtensionHashableContract()
    {
        var a = new PointKey(3, 4);
        var b = new PointKey(3, 4);
        var c = new PointKey(3, 5);

        AssertTrue(a.GetHashCode() != 0,
            "synthesised extension-Hashable must not return the 0-stub");
        AssertEqual(a.GetHashCode(), b.GetHashCode(),
            "Equal PointKeys must hash equally (synthesised hash(into:))");

        var bag = new Dictionary<PointKey, string> { [a] = "origin-ish" };
        AssertTrue(bag.TryGetValue(b, out var via),
            "synthesised frozen-struct Hashable extension must support Dictionary lookup");
        AssertEqual("origin-ish", via!, "Dictionary returns the inserted value");
        AssertTrue(!bag.ContainsKey(c), "Distinct PointKeys must not collide via synthesised hash");
    }

    /// <summary>
    /// Non-frozen counterpart to <c>PointKey</c>: synthesised-Hashable
    /// extension on a non-frozen struct that surfaces as a SafeHandle-backed
    /// reference type. Pairs with <c>TestTagExtensionHashableContract</c>
    /// (manual hash on non-frozen) to lock the non-frozen synthesised path.
    /// </summary>
    public void TestLabelKeySynthesisedExtensionHashableContract()
    {
        var a = new LabelKey("env", 1);
        var b = new LabelKey("env", 1);
        var c = new LabelKey("env", 2);

        AssertTrue(a.GetHashCode() != 0,
            "synthesised non-frozen extension-Hashable must not return the 0-stub");
        AssertEqual(a.GetHashCode(), b.GetHashCode(),
            "Equal LabelKeys must hash equally (synthesised hash(into:))");

        var lookup = new Dictionary<LabelKey, string> { [a] = "env-1" };
        AssertTrue(lookup.TryGetValue(b, out var via),
            "non-frozen synthesised Hashable extension must support Dictionary lookup");
        AssertEqual("env-1", via!, "Dictionary returns the inserted value");
        AssertTrue(!lookup.ContainsKey(c),
            "Distinct LabelKeys must not collide via synthesised hash");
    }

    /// <summary>
    /// Equatable Defect 1, intra-tree synthetic for the MusicItemID
    /// SafeHandle-backed shape: a Swift `class HashedHandle: Hashable` that
    /// surfaces in C# as a SafeHandle-style reference type. GetHashCode
    /// previously returned 0 for these, breaking Dictionary/HashSet — assert
    /// the contract holds end-to-end without reaching into MusicKit.
    /// </summary>
    public void TestHashedHandleSafeHandleHashableContract()
    {
        var a = new HashedHandle("sku-001");
        var b = new HashedHandle("sku-001");
        var c = new HashedHandle("sku-002");

        AssertTrue(a.Equals(b), "baseline: HashedHandle Equals on same identifier");
        AssertEqual(a.GetHashCode(), b.GetHashCode(),
            "Equal SafeHandle-backed Hashables must share a hash code");
        AssertTrue(a.GetHashCode() != 0,
            "SafeHandle-backed Hashable must not collapse to the 0-stub");

        var lookup = new Dictionary<HashedHandle, string> { [a] = "owner-A" };
        AssertTrue(lookup.TryGetValue(b, out var owner),
            "Dictionary<HashedHandle, V> must find an Equals-equivalent key");
        AssertEqual("owner-A", owner!, "Dictionary returns the inserted value");
        AssertTrue(!lookup.ContainsKey(c),
            "Different SafeHandle-backed Hashables must not collide");
    }

    #endregion
}
