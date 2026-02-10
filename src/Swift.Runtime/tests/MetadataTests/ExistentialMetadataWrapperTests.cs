// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Swift;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

public class ExistentialMetadataWrapperTests
{
    /// <summary>
    /// Probes whether the SwiftBindingsRuntime native library can be loaded.
    /// Returns true when the dylib is deployed alongside the test assembly.
    /// </summary>
    private static bool IsRuntimeLibraryAvailable()
    {
        try
        {
            return NativeLibrary.TryLoad("SwiftBindingsRuntime", out _);
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public void GetExistentialTypeMetadata_ZeroProtocols_ReturnsValidMetadata()
    {
        if (!IsRuntimeLibraryAvailable())
        {
            // Skip: dylib not deployed in this test environment.
            // The runtime test (ExistentialMetadataTests on iOS Simulator) covers this path.
            return;
        }

        var metadata = TypeMetadata.GetExistentialTypeMetadata(0);
        Assert.True(metadata.IsValid, "Zero-protocol existential metadata should be valid");
        Assert.Equal(TypeMetadataKind.Existential, metadata.Kind);
    }

    [Fact]
    public void TryGetTypeMetadata_ExistentialContainer0_Succeeds()
    {
        if (!IsRuntimeLibraryAvailable())
        {
            // Skip: dylib not deployed in this test environment.
            return;
        }

        var success = TypeMetadata.TryGetTypeMetadata<ExistentialContainer0>(out var result);
        Assert.True(success, "TryGetTypeMetadata<ExistentialContainer0> should succeed");
        Assert.True(result!.Value.IsValid, "ExistentialContainer0 metadata should be valid");
    }

    [Fact]
    public void GetExistentialTypeMetadata_NonZeroProtocols_Throws()
    {
        // Non-zero protocol counts are not yet supported by the Swift wrapper.
        // The wrapper returns nil for numProtocols > 0, causing SwiftRuntimeException.
        var ex = Assert.Throws<SwiftRuntimeException>(
            () => TypeMetadata.GetExistentialTypeMetadata(1));
        Assert.Contains("1 protocol(s)", ex.Message);
    }

    [Fact]
    public void GetExistentialTypeMetadata_NonZeroProtocols_ErrorMentionsProtocolDescriptors()
    {
        // Non-zero protocol counts fail because protocol descriptor pointers aren't
        // implemented yet — error should say so, not blame a missing library.
        var ex = Assert.Throws<SwiftRuntimeException>(
            () => TypeMetadata.GetExistentialTypeMetadata(1));
        Assert.Contains("protocol descriptor", ex.Message);
    }
}
