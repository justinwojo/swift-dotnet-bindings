// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

#pragma warning disable SB0001 // CallConvSwift P/Invoke warning

namespace RuntimeTestsApp.Protocols;

/// <summary>
/// A protocol requirement satisfied by an unconstrained extension default, on a protocol whose
/// stored-property requirement one conformer also declares statically. The conformer that declares
/// the name only per-instance keeps <c>: IKeyedCipher</c>; the one that declares it both ways emits
/// the name as a static member, which cannot implement an instance interface requirement, so it has
/// to be turned away rather than claim a conformance that would not compile.
/// </summary>
public class StaticInstanceNameCollisionConformanceTests : TestBase
{
    public StaticInstanceNameCollisionConformanceTests(TestResults results) : base(results) { }

    public void TestInstanceOnlyConformerFlowsThroughInterface()
    {
        using var cipher = SwiftBindingsTestLib.Functions.MakeInstanceKeySizeCipher(16);

        IKeyedCipher asInterface = cipher;
        AssertEqual(16, asInterface.KeySize, "InstanceKeySizeCipher.KeySize via IKeyedCipher");
        AssertEqual(16, SwiftBindingsTestLib.Functions.CipherKeySize(cipher), "CipherKeySize(InstanceKeySizeCipher)");
    }

    public void TestInstanceOnlyConformerEncryptsThroughSwiftExistential()
    {
        using var cipher = SwiftBindingsTestLib.Functions.MakeInstanceKeySizeCipher(16);

        // Swift resolves `encrypt` to the extension default and runs it against the witness:
        // bytes * keySize + rounds.
        AssertEqual(53, SwiftBindingsTestLib.Functions.CipherEncrypt(cipher, 3, 5), "CipherEncrypt(InstanceKeySizeCipher, 3, 5)");
    }

    public void TestDualDeclarationConformerDoesNotClaimTheInterface()
    {
        using var cipher = SwiftBindingsTestLib.Functions.MakeDualKeySizeCipher(8);

        // The C# name belongs to the static declaration, so the conformance is honestly absent
        // rather than emitted and uncompilable.
        AssertFalse(cipher is IKeyedCipher, "DualKeySizeCipher does not implement IKeyedCipher");
        AssertEqual(16, DualKeySizeCipher.KeySize, "DualKeySizeCipher.KeySize is the static declaration");
    }
}
