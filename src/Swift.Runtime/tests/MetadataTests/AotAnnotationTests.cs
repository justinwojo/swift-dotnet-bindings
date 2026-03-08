// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Swift.Runtime;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Verifies that [SuppressGCTransition] attributes are correctly applied to
/// safe leaf P/Invoke declarations in Swift.Runtime, and NOT applied to
/// release operations that can trigger deinit/managed callbacks.
/// </summary>
public class AotAnnotationTests
{
    private static MethodInfo GetPrivateStaticMethod([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)] Type type, string name)
    {
        return type.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException($"Method {name} not found on {type.Name}");
    }

    #region SuppressGCTransition on safe leaf Arc P/Invokes

    [Theory]
    [InlineData("swift_retain")]
    [InlineData("swift_isDeallocating")]
    [InlineData("swift_retainCount")]
    [InlineData("swift_unownedRetain")]
    [InlineData("swift_unownedRetainCount")]
    public void ArcPInvoke_SafeLeaf_HasSuppressGCTransition(string methodName)
    {
        var method = GetPrivateStaticMethod(typeof(Arc), methodName);
        var attr = method.GetCustomAttribute<SuppressGCTransitionAttribute>();
        Assert.NotNull(attr);
    }

    #endregion

    #region Release operations must NOT have SuppressGCTransition

    [Theory]
    [InlineData("swift_release")]
    [InlineData("swift_unownedRelease")]
    public void ArcPInvoke_Release_DoesNotHaveSuppressGCTransition(string methodName)
    {
        // Release operations can trigger deinit which may call back into managed code
        // via closures/@_cdecl, so they must NOT suppress the GC transition.
        var method = GetPrivateStaticMethod(typeof(Arc), methodName);
        var attr = method.GetCustomAttribute<SuppressGCTransitionAttribute>();
        Assert.Null(attr);
    }

    #endregion

    #region All Arc P/Invokes have DllImport with Cdecl

    [Theory]
    [InlineData("swift_retain")]
    [InlineData("swift_release")]
    [InlineData("swift_isDeallocating")]
    [InlineData("swift_retainCount")]
    [InlineData("swift_unownedRetain")]
    [InlineData("swift_unownedRelease")]
    [InlineData("swift_unownedRetainCount")]
    public void ArcPInvoke_HasDllImport(string methodName)
    {
        var method = GetPrivateStaticMethod(typeof(Arc), methodName);
        var attr = method.GetCustomAttribute<DllImportAttribute>();
        Assert.NotNull(attr);
        Assert.Equal(CallingConvention.Cdecl, attr!.CallingConvention);
    }

    #endregion

    #region Existing AOT annotations preserved

    [Fact]
    public void TypeMetadata_TryGetTypeMetadataUncached_HasAotSuppression()
    {
        var method = typeof(TypeMetadata)
            .GetMethod("TryGetTypeMetadataUncached", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var suppressions = method!.GetCustomAttributes<UnconditionalSuppressMessageAttribute>();
        Assert.Contains(suppressions, s => s.Category == "AOT" && s.CheckId == "IL3050");
    }

    #endregion
}
