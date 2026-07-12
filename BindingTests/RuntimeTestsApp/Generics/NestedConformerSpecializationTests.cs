// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using Swift.Runtime;
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
        using var sealed_ = SealedKey.FromSwiftBindingsTestLibFlatKeyMaterial(key);
        AssertEqual("sealed[flat:f]", sealed_.Descriptor,
            "SealedKey.From(FlatKeyMaterial) factory");
    }

    public void TestSealedKey_FromOneLevelNestedConformer()
    {
        using var key = new SwiftBindingsTestLib.KeyVault.VaultKey(tag: "v");
        using var sealed_ = SealedKey.FromSwiftBindingsTestLibKeyVaultVaultKey(key);
        AssertEqual("sealed[vault:v]", sealed_.Descriptor,
            "SealedKey.From(KeyVault.VaultKey) factory");
    }

    public void TestSealedKey_FromTwoLevelNestedConformer()
    {
        // Regression witness: this factory only exists post-fix.
        using var key = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "p");
        using var sealed_ = SealedKey.FromSwiftBindingsTestLibKeyVaultAgreementPublicKey(key);
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
            .FromSwiftBindingsTestLibFlatKeyMaterial(seed);
        AssertEqual("box[flat:f]", box.Describe(),
            "KeyVaultBox<FlatKeyMaterial>.Describe extension");
    }

    public void TestKeyVaultBox_OneLevelNestedConformer_Describe()
    {
        using var seed = new SwiftBindingsTestLib.KeyVault.VaultKey(tag: "v");
        using var box = KeyVaultBoxSwiftBindingsTestLib_KeyVault_VaultKeyCsmExtensions
            .FromSwiftBindingsTestLibKeyVaultVaultKey(seed);
        AssertEqual("box[vault:v]", box.Describe(),
            "KeyVaultBox<KeyVault.VaultKey>.Describe extension");
    }

    public void TestKeyVaultBox_TwoLevelNestedConformer_Describe()
    {
        // Regression witness: this factory + extension only exist post-fix.
        using var seed = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "p");
        using var box = KeyVaultBoxSwiftBindingsTestLib_KeyVault_Agreement_PublicKeyCsmExtensions
            .FromSwiftBindingsTestLibKeyVaultAgreementPublicKey(seed);
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
            .FromSwiftBindingsTestLibKeyVaultAgreementPublicKey(seed);
        using var recovered = box.Seed;
        AssertEqual("agree-pub:rt", recovered.Material,
            "KeyVaultBox<KeyVault.Agreement.PublicKey>.Seed payload round-trip");
    }

    // --- Collision-renamed conformer: CollisionVault.Entry is the property/type-name clash that
    // renames the nested *type* to EntryInfo while the property keeps its name. The conformer's
    // C# name is cached at conformance-index time (pre-rename, "Entry"), so every CSM type
    // reference must re-resolve the live name (EntryInfo) at emission. The synthetic factory and
    // extension-class names still derive from the cached Swift name (..._CollisionVault_Entry),
    // so these tests pin BOTH halves of the split: name-by-cache, type-by-live-lookup. Without
    // the re-resolution they would name the non-existent CollisionVault.Entry and fail to compile.

    public void TestCollisionConformer_Construct_AndReadMaterial()
    {
        using var key = new SwiftBindingsTestLib.CollisionVault.EntryInfo(tag: "c");
        AssertEqual("collision-entry:c", key.Material,
            "CollisionVault.EntryInfo.material round-trip");
    }

    public void TestRegisterKey_CollisionConformer()
    {
        using var registrar = new KeyRegistrar(realm: "realm");
        using var key = new SwiftBindingsTestLib.CollisionVault.EntryInfo(tag: "c");
        AssertEqual("realm/collision-entry:c", registrar.RegisterKey(key),
            "RegisterKey(CollisionVault.EntryInfo) overload");
    }

    public void TestSealedKey_FromCollisionConformer()
    {
        using var key = new SwiftBindingsTestLib.CollisionVault.EntryInfo(tag: "c");
        using var sealed_ = SealedKey.FromSwiftBindingsTestLibCollisionVaultEntry(key);
        AssertEqual("sealed[collision-entry:c]", sealed_.Descriptor,
            "SealedKey.From(CollisionVault.EntryInfo) factory");
    }

    public void TestKeyVaultBox_CollisionConformer_Describe()
    {
        using var seed = new SwiftBindingsTestLib.CollisionVault.EntryInfo(tag: "c");
        using var box = KeyVaultBoxSwiftBindingsTestLib_CollisionVault_EntryCsmExtensions
            .FromSwiftBindingsTestLibCollisionVaultEntry(seed);
        AssertEqual("box[collision-entry:c]", box.Describe(),
            "KeyVaultBox<CollisionVault.EntryInfo>.Describe extension");
    }

    // --- Shape 2 (throwing): generic THROWING initializer — the exact CryptoKit HPKE
    // Sender/Recipient init shape (`init<K: …>(…) throws`). ThrowingSealedBox is NON-frozen so
    // it projects as a C# class with an opaque payload, matching HPKE.Sender/Recipient — the
    // throwing-init factory returns an opaque handle (Unmanaged.passRetained) and writes a
    // sentinel on the error path, rather than the frozen-struct indirect-result path SealedKey
    // uses. The specializer historically dropped `IsConstructor && Throws` in the CSM dispatcher,
    // so every throwing generic init fell back to a generic-only stub and these factories did not
    // exist (HPKE construction was unreachable). `shouldSucceed` makes the throw deterministic:
    // the success path round-trips the descriptor; the false path must surface the Swift error as
    // a C# SwiftException, with the constructed handle never escaping.

    // `info` is the concrete `Foundation.Data` param that mirrors HPKE's
    // `init(recipientKey:ciphersuite:info:)`. It crosses the factory as the public `byte[]`
    // surface; the Swift side folds its bytes back into the descriptor as hex, so asserting the
    // descriptor contains the round-tripped hex proves the concrete Data param survived the
    // two-Int-word @_cdecl boundary intact alongside the specializable generic key.
    public void TestThrowingSealedBox_FromFlatConformer_Success()
    {
        using var key = new FlatKeyMaterial(tag: "f");
        using var box = ThrowingSealedBox.FromSwiftBindingsTestLibFlatKeyMaterial(key, new byte[] { 0xAB, 0xCD }, shouldSucceed: true);
        AssertEqual("throwing-sealed[flat:f|info:abcd]", box.Descriptor,
            "ThrowingSealedBox.From(FlatKeyMaterial) throwing factory — success round-trip carries concrete Data info");
    }

    public void TestThrowingSealedBox_FromTwoLevelNestedConformer_Success()
    {
        // Regression witness: this throwing factory only exists once the CSM throwing-ctor skip
        // is lifted, and at exactly HPKE's two-level nesting depth — now also carrying the
        // concrete Data `info` param that blocked HPKE construction.
        using var key = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "p");
        using var box = ThrowingSealedBox.FromSwiftBindingsTestLibKeyVaultAgreementPublicKey(key, new byte[] { 0x01, 0x02, 0x03 }, shouldSucceed: true);
        AssertEqual("throwing-sealed[agree-pub:p|info:010203]", box.Descriptor,
            "ThrowingSealedBox.From(KeyVault.Agreement.PublicKey) throwing factory — success round-trip carries concrete Data info");
    }

    public void TestThrowingSealedBox_FromCollisionConformer_Success()
    {
        using var key = new SwiftBindingsTestLib.CollisionVault.EntryInfo(tag: "c");
        using var box = ThrowingSealedBox.FromSwiftBindingsTestLibCollisionVaultEntry(key, new byte[] { 0xFF }, shouldSucceed: true);
        AssertEqual("throwing-sealed[collision-entry:c|info:ff]", box.Descriptor,
            "ThrowingSealedBox.From(CollisionVault.EntryInfo) throwing factory — success round-trip carries concrete Data info");
    }

    public void TestThrowingSealedBox_FromFlatConformer_Throws()
    {
        using var key = new FlatKeyMaterial(tag: "f");
        AssertThrows<SwiftException>(
            () => ThrowingSealedBox.FromSwiftBindingsTestLibFlatKeyMaterial(key, new byte[] { 0xAB, 0xCD }, shouldSucceed: false),
            "ThrowingSealedBox.From(FlatKeyMaterial) error path surfaces the Swift error as SwiftException");
    }

    public void TestThrowingSealedBox_FromTwoLevelNestedConformer_Throws()
    {
        using var key = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "p");
        AssertThrows<SwiftException>(
            () => ThrowingSealedBox.FromSwiftBindingsTestLibKeyVaultAgreementPublicKey(key, new byte[] { 0x01, 0x02, 0x03 }, shouldSucceed: false),
            "ThrowingSealedBox.From(KeyVault.Agreement.PublicKey) error path surfaces the Swift error as SwiftException");
    }

    // --- Shape 2 (throwing, CLASS host): the sibling ABI branch. ThrowingSealedRef is a Swift
    // `class`, so its throwing CSM init factory takes the class-pointer return path
    // (Unmanaged.passRetained on success, a non-null sentinel on the error path) rather than the
    // struct indirect-result path above. HPKE only needs the struct branch, but lifting the
    // throwing-ctor skip makes this branch reachable too — both are pinned so a future ABI
    // regression in either return shape is caught.

    public void TestThrowingSealedRef_FromFlatConformer_Success()
    {
        using var key = new FlatKeyMaterial(tag: "f");
        using var box = ThrowingSealedRef.FromSwiftBindingsTestLibFlatKeyMaterial(key, new byte[] { 0xDE, 0xAD }, shouldSucceed: true);
        AssertEqual("throwing-ref[flat:f|info:dead]", box.Descriptor,
            "ThrowingSealedRef.From(FlatKeyMaterial) throwing class factory — success round-trip carries concrete Data info");
    }

    public void TestThrowingSealedRef_FromTwoLevelNestedConformer_Success()
    {
        using var key = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "p");
        using var box = ThrowingSealedRef.FromSwiftBindingsTestLibKeyVaultAgreementPublicKey(key, new byte[] { 0xBE, 0xEF }, shouldSucceed: true);
        AssertEqual("throwing-ref[agree-pub:p|info:beef]", box.Descriptor,
            "ThrowingSealedRef.From(KeyVault.Agreement.PublicKey) throwing class factory — success round-trip carries concrete Data info");
    }

    public void TestThrowingSealedRef_FromFlatConformer_Throws()
    {
        using var key = new FlatKeyMaterial(tag: "f");
        AssertThrows<SwiftException>(
            () => ThrowingSealedRef.FromSwiftBindingsTestLibFlatKeyMaterial(key, new byte[] { 0xDE, 0xAD }, shouldSucceed: false),
            "ThrowingSealedRef.From(FlatKeyMaterial) error path surfaces the Swift error as SwiftException (sentinel not consumed)");
    }

    public void TestThrowingSealedRef_FromTwoLevelNestedConformer_Throws()
    {
        using var key = new SwiftBindingsTestLib.KeyVault.Agreement.PublicKey(tag: "p");
        AssertThrows<SwiftException>(
            () => ThrowingSealedRef.FromSwiftBindingsTestLibKeyVaultAgreementPublicKey(key, new byte[] { 0xBE, 0xEF }, shouldSucceed: false),
            "ThrowingSealedRef.From(KeyVault.Agreement.PublicKey) error path surfaces the Swift error as SwiftException (sentinel not consumed)");
    }
}
