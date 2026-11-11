// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Static overloads of <c>TaggedKeyDeriver&lt;Item: Tagger&gt;.deriveKey</c> that differ only by an
/// argument label (<c>info:</c> vs <c>salt:</c>), each generic over <c>DataProtocol</c>. Both close
/// to <c>(int, Data|byte[], int)</c> per parent conformer and have no open-generic form, so both
/// reach the closed surface: the first-declared <c>info:</c> overload under the plain
/// <c>DeriveKey</c> name (CryptoKit's HKDF shipped that way) and the <c>salt:</c> overload under
/// its label-derived name. Every call checks the Swift body that ran — each overload returns a
/// distinct base — so a name bound to the wrong entry point fails. The overload generic over
/// <c>Tagger</c> closes over different types and keeps the plain name too.
/// </summary>
public class CsmLabelOnlySiblingsTests : TestBase
{
    public CsmLabelOnlySiblingsTests(TestResults results) : base(results) { }

    public void TestInfoOverload_KeepsPlainName()
    {
        // 1000 + material + info.count * 10 + outputByteCount * 100
        AssertEqual(1235, TaggedKeyDeriverStringTaggerCsmExtensions.DeriveKey(5, new byte[] { 1, 2, 3 }, 2), "String parent, info byte[]");
        var info = global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2, 3 });
        AssertEqual(1235, TaggedKeyDeriverIntTaggerCsmExtensions.DeriveKey(5, info, 2), "Int parent, info Data");
    }

    public void TestSaltOverload_ByteArrayAndData()
    {
        // 2000 + material + salt.count * 10 + outputByteCount * 100
        AssertEqual(2245, TaggedKeyDeriverIntTaggerCsmExtensions.DeriveKeyMaterialSaltOutputByteCount(5, new byte[] { 9, 9, 9, 9 }, 2), "Int parent, salt byte[]");
        var salt = global::Swift.Foundation.Data.FromByteArray(new byte[] { 9, 9, 9, 9 });
        AssertEqual(2245, TaggedKeyDeriverStringTaggerCsmExtensions.DeriveKeyMaterialSaltOutputByteCount(5, salt, 2), "String parent, salt Data");
    }

    public void TestSaltAndInfoOverload_KeepsPlainName()
    {
        // 3000 + material + salt.count * 10 + info.count * 10 + outputByteCount * 100
        AssertEqual(3255, TaggedKeyDeriverStringTaggerCsmExtensions.DeriveKey(5, new byte[] { 1, 2 }, new byte[] { 1, 2, 3 }, 2), "salt+info byte[]");
        var salt = global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2 });
        var info = global::Swift.Foundation.Data.FromByteArray(new byte[] { 1, 2, 3 });
        AssertEqual(3255, TaggedKeyDeriverIntTaggerCsmExtensions.DeriveKey(5, salt, info, 2), "salt+info Data");
    }

    public void TestTaggerOverload_KeepsPlainName()
    {
        // 4000 + material + (StringTagger ? 1 : 2) * 10 + outputByteCount * 100
        using var stringTag = new StringTagger();
        using var intTag = new IntTagger();
        AssertEqual(4215, TaggedKeyDeriverStringTaggerCsmExtensions.DeriveKey(5, stringTag, 2), "Tagger info StringTagger");
        AssertEqual(4225, TaggedKeyDeriverIntTaggerCsmExtensions.DeriveKey(5, intTag, 2), "Tagger info IntTagger");
    }
}
