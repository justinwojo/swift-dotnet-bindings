// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Concrete-specialization coverage for nested-type conformers — the HPKE shape where a
/// protocol's conformers are types nested two levels inside their module
/// (<c>Curve25519.KeyAgreement.PublicKey</c>). The specializer historically rejected any
/// conformer whose module-qualified name had more than two dot segments, so those
/// initializers fell back to generic-only stubs. <see cref="SwiftBindingsTestLib.NestedKeyMaterial"/>
/// has three conformers spanning the depth range — <c>FlatKeyMaterial</c> (flat baseline),
/// <c>KeyVault.VaultKey</c> (one level), and <c>KeyVault.Agreement.PublicKey</c> (two levels,
/// exactly HPKE's nesting) — exercised through all three sync CSM emission shapes:
/// a generic method (<c>KeyRegistrar.RegisterKey</c>), a generic initializer
/// (<c>SealedKey.From*</c> factories), and a generic parent type (<c>KeyVaultBox&lt;T&gt;</c>
/// <c>From*</c> + <c>Describe</c> extensions). The two-level conformer is the regression
/// witness: before the structural-gate relaxation + post-rename name re-resolution, its
/// overload was never emitted.
/// </summary>
public class NestedConformerSpecializationTests : TestBase
{
    public NestedConformerSpecializationTests(TestResults results) : base(results) { }

    // --- Nested-type construction + property read, independent of any CSM path. Proves the
    // nested conformer types themselves marshal correctly before we trust the specialized
    // overloads that consume them.

    public void TestFlatConformer_Construct_AndReadMaterial()
    {
        using var key = new FlatKeyMaterial(tag: "f");
        AssertEqual("flat:f", key.Material, "FlatKeyMaterial.material round-trip");
    }

    public void TestOneLevelNestedConformer_Construct_AndReadMaterial()
    {
        using var key = new SwiftBindingsTestLib.KeyVault.VaultKey(tag: "v");
        AssertEqual("vault:v", key.Material, "KeyVault.VaultKey.material round-trip");
    }

    public void TestTwoLevelNestedConformer_Construct_AndReadMaterial()
    {
        using var key = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "p");
        AssertEqual("agree-pub:p", key.Material, "KeyVault.Agreement.PublicKey.material round-trip");
    }

    // --- Shape 1: generic method on a non-generic host. The specializer emits one concrete
    // RegisterKey overload per conformer. The concrete String return isolates the
    // nested-conformer dimension from any generic-return concern.

    public void TestRegisterKey_FlatConformer()
    {
        using var registrar = new KeyRegistrar(realm: "realm");
        using var key = new FlatKeyMaterial(tag: "f");
        AssertEqual("realm/flat:f", registrar.RegisterKey(key),
            "RegisterKey(FlatKeyMaterial) overload");
    }

    public void TestRegisterKey_OneLevelNestedConformer()
    {
        using var registrar = new KeyRegistrar(realm: "realm");
        using var key = new SwiftBindingsTestLib.KeyVault.VaultKey(tag: "v");
        AssertEqual("realm/vault:v", registrar.RegisterKey(key),
            "RegisterKey(KeyVault.VaultKey) overload");
    }

    public void TestRegisterKey_TwoLevelNestedConformer()
    {
        // Regression witness: this overload only exists post-fix.
        using var registrar = new KeyRegistrar(realm: "realm");
        using var key = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "p");
        AssertEqual("realm/agree-pub:p", registrar.RegisterKey(key),
            "RegisterKey(KeyVault.Agreement.PublicKey) overload");
    }

    // --- Shape 2: generic initializer. The specializer emits one From{Conformer} static
    // factory per conformer — the HPKE Sender/Recipient init shape.

    public void TestSealedKey_FromFlatConformer()
    {
        using var key = new FlatKeyMaterial(tag: "f");
        using var sealed_ = SealedKey.FromSwiftBindingsTestLib_FlatKeyMaterial(key);
        AssertEqual("sealed[flat:f]", sealed_.Descriptor,
            "SealedKey.From(FlatKeyMaterial) factory");
    }

    public void TestSealedKey_FromOneLevelNestedConformer()
    {
        using var key = new SwiftBindingsTestLib.KeyVault.VaultKey(tag: "v");
        using var sealed_ = SealedKey.FromSwiftBindingsTestLib_KeyVault_VaultKey(key);
        AssertEqual("sealed[vault:v]", sealed_.Descriptor,
            "SealedKey.From(KeyVault.VaultKey) factory");
    }

    public void TestSealedKey_FromTwoLevelNestedConformer()
    {
        // Regression witness: this factory only exists post-fix.
        using var key = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "p");
        using var sealed_ = SealedKey.FromSwiftBindingsTestLib_KeyVault_Agreement_PublicKey(key);
        AssertEqual("sealed[agree-pub:p]", sealed_.Descriptor,
            "SealedKey.From(KeyVault.Agreement.PublicKey) factory");
    }

    // --- Shape 3: generic parent type. Each closed receiver (KeyVaultBox<Conformer>) gets a
    // From{Conformer} factory plus a Describe extension method, emitted per conformer into a
    // dedicated {Parent}{Conformer}CsmExtensions static class.

    public void TestKeyVaultBox_FlatConformer_Describe()
    {
        using var seed = new FlatKeyMaterial(tag: "f");
        using var box = KeyVaultBoxSwiftBindingsTestLib_FlatKeyMaterialCsmExtensions
            .FromSwiftBindingsTestLib_FlatKeyMaterial(seed);
        AssertEqual("box[flat:f]", box.Describe(),
            "KeyVaultBox<FlatKeyMaterial>.Describe extension");
    }

    public void TestKeyVaultBox_OneLevelNestedConformer_Describe()
    {
        using var seed = new SwiftBindingsTestLib.KeyVault.VaultKey(tag: "v");
        using var box = KeyVaultBoxSwiftBindingsTestLib_KeyVault_VaultKeyCsmExtensions
            .FromSwiftBindingsTestLib_KeyVault_VaultKey(seed);
        AssertEqual("box[vault:v]", box.Describe(),
            "KeyVaultBox<KeyVault.VaultKey>.Describe extension");
    }

    public void TestKeyVaultBox_TwoLevelNestedConformer_Describe()
    {
        // Regression witness: this factory + extension only exist post-fix.
        using var seed = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "p");
        using var box = KeyVaultBoxSwiftBindingsTestLib_KeyVault_Agreement_PublicKeyCsmExtensions
            .FromSwiftBindingsTestLib_KeyVault_Agreement_PublicKey(seed);
        AssertEqual("box[agree-pub:p]", box.Describe(),
            "KeyVaultBox<KeyVault.Agreement.PublicKey>.Describe extension");
    }

    // --- Seed round-trip through the generic parent: reads the stored conformer back out and
    // re-reads its material, witnessing that the parent's T payload survives the closed-generic
    // factory + Seed getter for a two-level-nested T.
    public void TestKeyVaultBox_TwoLevelNestedConformer_SeedRoundTrip()
    {
        using var seed = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "rt");
        using var box = KeyVaultBoxSwiftBindingsTestLib_KeyVault_Agreement_PublicKeyCsmExtensions
            .FromSwiftBindingsTestLib_KeyVault_Agreement_PublicKey(seed);
        using var recovered = box.Seed;
        AssertEqual("agree-pub:rt", recovered.Material,
            "KeyVaultBox<KeyVault.Agreement.PublicKey>.Seed payload round-trip");
    }

    // --- Collision-renamed conformer: CollisionVault.Entry is the property/type-name clash that
    // renames the nested *type* to EntryType while the property keeps its name. The conformer's
    // C# name is cached at conformance-index time (pre-rename, "Entry"), so every CSM type
    // reference must re-resolve the live name (EntryType) at emission. The synthetic factory and
    // extension-class names still derive from the cached Swift name (..._CollisionVault_Entry),
    // so these tests pin BOTH halves of the split: name-by-cache, type-by-live-lookup. Without
    // the re-resolution they would name the non-existent CollisionVault.Entry and fail to compile.

    public void TestCollisionConformer_Construct_AndReadMaterial()
    {
        using var key = new SwiftBindingsTestLib.CollisionVault.EntryType(tag: "c");
        AssertEqual("collision-entry:c", key.Material,
            "CollisionVault.EntryType.material round-trip");
    }

    public void TestRegisterKey_CollisionConformer()
    {
        using var registrar = new KeyRegistrar(realm: "realm");
        using var key = new SwiftBindingsTestLib.CollisionVault.EntryType(tag: "c");
        AssertEqual("realm/collision-entry:c", registrar.RegisterKey(key),
            "RegisterKey(CollisionVault.EntryType) overload");
    }

    public void TestSealedKey_FromCollisionConformer()
    {
        using var key = new SwiftBindingsTestLib.CollisionVault.EntryType(tag: "c");
        using var sealed_ = SealedKey.FromSwiftBindingsTestLib_CollisionVault_Entry(key);
        AssertEqual("sealed[collision-entry:c]", sealed_.Descriptor,
            "SealedKey.From(CollisionVault.EntryType) factory");
    }

    public void TestKeyVaultBox_CollisionConformer_Describe()
    {
        using var seed = new SwiftBindingsTestLib.CollisionVault.EntryType(tag: "c");
        using var box = KeyVaultBoxSwiftBindingsTestLib_CollisionVault_EntryCsmExtensions
            .FromSwiftBindingsTestLib_CollisionVault_Entry(seed);
        AssertEqual("box[collision-entry:c]", box.Describe(),
            "KeyVaultBox<CollisionVault.EntryType>.Describe extension");
    }
}
