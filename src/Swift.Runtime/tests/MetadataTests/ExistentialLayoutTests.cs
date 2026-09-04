// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Unit coverage for the existential-layout tripwire (<see cref="ExistentialLayout"/>).
/// A generated protocol proxy picks its existential-container shape at emission time from parsed
/// ABI facts; the Swift wrapper reports the shape Swift actually uses. This helper compares the
/// two and rejects an ARM mismatch — opaque against class-bound in either direction — while
/// tolerating the pure-<c>@objc</c> narrowing of the class-bound arm, where the proxy still fills
/// word 0 with the class reference the reader consumes.
/// </summary>
public class ExistentialLayoutTests
{
    // --- Agreement: each arm accepts the size its own layout choice implies ---

    [Theory]
    [InlineData(5)] // opaque: three payload words + metadata + witness table
    [InlineData(2)] // class-bound: class reference + witness table
    [InlineData(1)] // pure @objc: a bare object pointer
    public void Verify_SizesAgree_DoesNotThrow(int words)
    {
        var size = words * IntPtr.Size;
        ExistentialLayout.Verify("SomeProtocol", size, size);
    }

    [Fact]
    public void Verify_ClassBoundExpectedObjCReported_DoesNotThrow()
    {
        // A class-bound proxy handed a pure-@objc existential still writes the class reference into
        // word 0, which is the whole container Swift reads — a narrowing, not a shape confusion.
        ExistentialLayout.Verify(
            "NumberProvider", ExistentialLayout.ClassBoundSize, ExistentialLayout.ObjCSize);
    }

    // --- Disagreement: the arm confusion that produces the null-witness-table crash ---

    [Fact]
    public void Verify_OpaqueExpectedClassBoundReported_Throws()
    {
        // The issue-46 shape: the parser missed `: AnyObject`, so the proxy builds a five-word
        // container while Swift reads two.
        var ex = Assert.Throws<InvalidOperationException>(() => ExistentialLayout.Verify(
            "DataScannerViewControllerDelegate",
            ExistentialLayout.OpaqueSize,
            ExistentialLayout.ClassBoundSize));

        Assert.Contains("DataScannerViewControllerDelegate", ex.Message);
        Assert.Contains(ExistentialLayout.OpaqueSize.ToString(), ex.Message);
        Assert.Contains(ExistentialLayout.ClassBoundSize.ToString(), ex.Message);
        Assert.Contains("Regenerate", ex.Message);
    }

    [Fact]
    public void Verify_OpaqueExpectedObjCReported_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => ExistentialLayout.Verify(
            "SomeObjCProtocol", ExistentialLayout.OpaqueSize, ExistentialLayout.ObjCSize));
    }

    [Fact]
    public void Verify_ClassBoundExpectedOpaqueReported_Throws()
    {
        // The reverse direction: the proxy fills two words but Swift reads five, so the metadata
        // and witness words Swift consumes are whatever happened to be beyond the container.
        Assert.Throws<InvalidOperationException>(() => ExistentialLayout.Verify(
            "SomeProtocol", ExistentialLayout.ClassBoundSize, ExistentialLayout.OpaqueSize));
    }

    [Fact]
    public void Verify_UnrecognizedReportedSize_Throws()
    {
        // Neither arm — a size nobody emits means the accessor is not reporting what we think it is.
        var ex = Assert.Throws<InvalidOperationException>(() => ExistentialLayout.Verify(
            "SomeProtocol", ExistentialLayout.OpaqueSize, 3 * IntPtr.Size));

        Assert.Contains("SomeProtocol", ex.Message);
    }

    // --- Fail-closed: an unverifiable pairing is reported, never treated as agreement ---

    [Fact]
    public void MissingSizeAccessor_NamesProtocolAndPreservesInner()
    {
        var inner = new EntryPointNotFoundException("Get_EveryProtocol_SomeProtocol_ExistentialSize");

        var ex = ExistentialLayout.MissingSizeAccessor("SomeProtocol", inner);

        Assert.Contains("SomeProtocol", ex.Message);
        Assert.Contains("Rebuild", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }

    // --- The arm sizes are pointer-derived, not hard-coded for 64-bit ---

    [Fact]
    public void ArmSizes_DeriveFromPointerWidth()
    {
        Assert.Equal(5 * IntPtr.Size, ExistentialLayout.OpaqueSize);
        Assert.Equal(2 * IntPtr.Size, ExistentialLayout.ClassBoundSize);
        Assert.Equal(IntPtr.Size, ExistentialLayout.ObjCSize);
    }
}
