// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace Swift.Runtime.Tests;

/// <summary>
/// Layout pins for the Optional carriers that transport a wider-than-a-word
/// <c>Optional&lt;T&gt;</c> across a direct CallConvSwift P/Invoke.
///
/// <para>These structs are pure transport: their only job is to declare a slot shaped exactly like
/// the registers Swift actually uses, so every byte of the value crosses. Deciding Some vs None
/// from those bytes stays with the Optional's value-witness table, which is the only ABI-stable
/// reader of an extra-inhabitant tag — nothing here infers a spare-bit encoding.</para>
///
/// <para>What makes them worth pinning is that the wrong version of each still compiles, still
/// looks reasonable, and fails only as silently corrupted values at runtime.</para>
/// </summary>
public class SwiftOptionalCarrierTests
{
    [Fact]
    public void Carrier9_FieldsAreIntegerTyped()
    {
        // THE trap. Swift lowers an enum payload as OPAQUE INTEGER storage, so Optional<Double>
        // travels in x0 + w1 — integer registers — and the callee moves the payload out with
        // `fmov d0, x0` before using it as a Double. Declaring the payload field as `double` here
        // would be the natural-looking choice and would be wrong: .NET faithfully lowers a
        // floating-point field into a floating-point register, so the value would be passed in d0
        // while Swift read x0. Nothing would fail to compile; the number would simply be wrong.
        //
        // Two independent reviewers proposed the double-typed carrier during design, so this is a
        // mistake that survives review — which is exactly why it is pinned as a test rather than
        // left to the comment on the struct.
        foreach (var field in typeof(SwiftOptionalCarrier9).GetFields())
        {
            Assert.False(
                field.FieldType == typeof(double) || field.FieldType == typeof(float),
                $"SwiftOptionalCarrier9.{field.Name} is floating-point typed. Swift passes an "
                + "Optional payload in integer registers even when the payload is a Double; a "
                + "floating-point field silently disagrees about which register carries the value.");
        }
    }

    [Fact]
    public void Carrier16_FieldsAreIntegerTyped()
    {
        // Same reasoning as the 9-byte carrier. This one currently carries Optional<String>, whose
        // payload is already integer-shaped, but the constraint is a property of how Swift lowers
        // enum payloads rather than of which types happen to use this carrier today.
        foreach (var field in typeof(SwiftOptionalCarrier16).GetFields())
        {
            Assert.False(
                field.FieldType == typeof(double) || field.FieldType == typeof(float),
                $"SwiftOptionalCarrier16.{field.Name} is floating-point typed.");
        }
    }

    [Fact]
    public void Carrier9_IsNineBytesWithNoTrailingPadding()
    {
        // An 8-byte payload word plus a separate tag byte. The Pack = 1 is load-bearing: the
        // default packing would round this to 16 and add a padding element to the lowering, and
        // .NET lowers a struct by its *elements*, so an extra element changes which registers the
        // call uses. Nine is also what the value witness expects to copy.
        Assert.Equal(9, Marshal.SizeOf<SwiftOptionalCarrier9>());
    }

    [Fact]
    public void Carrier16_IsTwoMachineWords()
    {
        // The extra-inhabitant shape: two integer words, no separate tag byte. Optional<String> is
        // the case that occurs, and nil-ness is decided by bytes spread across BOTH words — which
        // is precisely why transferring only the first produced a nil that read as a value.
        Assert.Equal(16, Marshal.SizeOf<SwiftOptionalCarrier16>());
    }

    [Fact]
    public void Carriers_AreBlittable()
    {
        // The carriers exist to be legal in a CallConvSwift P/Invoke signature. GCHandle.Alloc with
        // Pinned succeeds only for a blittable type, so this fails if a field is ever changed to
        // something with a managed representation.
        var handle9 = GCHandle.Alloc(default(SwiftOptionalCarrier9), GCHandleType.Pinned);
        handle9.Free();

        var handle16 = GCHandle.Alloc(default(SwiftOptionalCarrier16), GCHandleType.Pinned);
        handle16.Free();
    }
}
