// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Swift.Runtime;
using Swift.Runtime.InteropServices;
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

    #region ILLink.Descriptors.xml preserves closure infrastructure

    [Fact]
    public void ILLinkDescriptors_PreservesSwiftClosureMarshaller()
    {
        // ILLink.Descriptors.xml must preserve SwiftClosureMarshaller for NativeAOT.
        // Generated closure callbacks call GetDelegateFromContext<T> which would be
        // trimmed without the descriptor entry.
        var assembly = typeof(Swift.Runtime.SwiftClosureMarshaller).Assembly;
        var resourceNames = assembly.GetManifestResourceNames();
        var descriptorName = resourceNames.FirstOrDefault(n => n.Contains("ILLink.Descriptors"));
        Assert.NotNull(descriptorName);

        using var stream = assembly.GetManifestResourceStream(descriptorName!);
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream!);
        var content = reader.ReadToEnd();
        Assert.Contains("SwiftClosureMarshaller", content);
    }

    #endregion

    #region ILLink.Descriptors.xml reconciles with the trim/AOT suppressions

    // The IL2087/IL2070/IL2072/IL2026 suppressions across ISwiftObject,
    // TypeMetadata, ProtocolConformanceDescriptor, ExistentialContainer and
    // SwiftMarshal justify themselves by claiming the reached types/members are
    // preserved by the shipped Swift.Runtime ILLink.Descriptors.xml — NOT the
    // BindingTests app's TrimmerRoots.xml, which consumers never receive. These
    // tests reconcile that claim against the descriptor's actual content, so a
    // suppression and its preservation entry cannot silently drift apart.

    private static string ReadEmbeddedRuntimeDescriptor()
    {
        var assembly = typeof(Swift.Runtime.SwiftClosureMarshaller).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.Contains("ILLink.Descriptors"));
        Assert.NotNull(name);
        using var stream = assembly.GetManifestResourceStream(name!);
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream!);
        return reader.ReadToEnd();
    }

    [Fact]
    public void ILLinkDescriptors_AssemblyName_MatchesRuntimeAssembly()
    {
        // Root-cause invariant (Defect J): a trimmer descriptor is INERT unless its
        // <assembly fullname> equals the .NET assembly the preserved types actually
        // compile into. If the Runtime assembly were ever renamed without updating the
        // descriptor, ILC would root nothing and every suppression above would become a
        // silent trim hazard while still compiling clean. Pin the match.
        var runtimeAssemblyName = typeof(Swift.Runtime.SwiftClosureMarshaller).Assembly.GetName().Name;
        Assert.Equal("Swift.Runtime", runtimeAssemblyName);

        var doc = XDocument.Parse(ReadEmbeddedRuntimeDescriptor());
        var assemblyFullnames = doc.Descendants("assembly")
            .Select(a => a.Attribute("fullname")?.Value)
            .ToList();
        Assert.Contains(runtimeAssemblyName, assemblyFullnames);
    }

    [Theory]
    // Runtime-owned ISwiftObject types whose static NewFromPayload / GetTypeMetadata /
    // GetProtocolConformanceDescriptor the suppressions reach via reflection:
    [InlineData("Swift.Runtime", "Swift.SwiftString")]
    [InlineData("Swift.Runtime", "Swift.SwiftArray`1")]
    [InlineData("Swift.Runtime", "Swift.SwiftOptional`1")]
    [InlineData("Swift.Runtime", "Swift.KeyPath`2")]
    // Reflection infrastructure the suppressed call paths route through:
    [InlineData("Swift.Runtime", "Swift.Runtime.SwiftObjectReflectionHelper")]
    [InlineData("Swift.Runtime", "Swift.Runtime.InteropServices.NewFromPayloadDispatcher")]
    [InlineData("Swift.Runtime", "Swift.Runtime.ProtocolConformanceDescriptor")]
    [InlineData("Swift.Runtime", "Swift.Runtime.ProtocolWitnessTable")]
    // The dynamic ValueTuple path (CreateValueTuple's IL2026/IL2087 suppression):
    [InlineData("System.Private.CoreLib", "System.ValueTuple`2")]
    [InlineData("System.Private.CoreLib", "System.ValueTuple`8")]
    public void ILLinkDescriptors_PreservesSuppressionDependency(string assemblyName, string typeFullname)
    {
        var doc = XDocument.Parse(ReadEmbeddedRuntimeDescriptor());
        var asm = doc.Descendants("assembly")
            .FirstOrDefault(a => a.Attribute("fullname")?.Value == assemblyName);
        Assert.NotNull(asm);
        var type = asm!.Elements("type")
            .FirstOrDefault(t => t.Attribute("fullname")?.Value == typeFullname);
        Assert.NotNull(type);
        // Each reconciled entry must keep enough to satisfy the reflective lookup the
        // suppression performs: preserve="all" (members + reflection metadata) or
        // preserve="methods". Anything weaker would not back the suppression's claim.
        var preserve = type!.Attribute("preserve")?.Value;
        Assert.True(preserve == "all" || preserve == "methods",
            $"{typeFullname} must preserve all/methods to back its trim suppression; found '{preserve}'.");
    }

    #endregion

    #region Metadata resolution stays callable from NativeAOT

    /// <summary>
    /// The by-Type metadata resolvers must stay free of RequiresDynamicCode. They run per tuple
    /// element underneath reverse-dispatch receivers, whose UnmanagedCallersOnly frames turn any
    /// escaping exception into a process abort — and closing TryGetTypeMetadata&lt;T&gt; reflectively
    /// (MakeGenericMethod) throws NotSupportedException on every NativeAOT call. Reintroducing that
    /// shape means either an IL3050 build error (the runtime is IsAotCompatible with warnings as
    /// errors) or annotating these methods, which this test refuses.
    /// </summary>
    [Theory]
    [InlineData("TryGetTypeMetadataUncached")]
    [InlineData("TryGetTupleTypeMetadata")]
    [UnconditionalSuppressMessage("Trimming", "IL2111",
        Justification = "The looked-up method is only inspected for attributes, never invoked, so its DynamicallyAccessedMembers requirements are not exercised")]
    public void TypeMetadata_ByTypeResolvers_AreCallableWithoutDynamicCode(string methodName)
    {
        var method = typeof(TypeMetadata)
            .GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        Assert.False(method!.IsGenericMethodDefinition,
            $"{methodName} must resolve by Type, not by a generic parameter a caller would have to close reflectively.");
        Assert.Null(method.GetCustomAttribute<RequiresDynamicCodeAttribute>());
    }

    [Fact]
    public void SwiftMarshal_TupleElementMetadataLookup_IsCallableWithoutDynamicCode()
    {
        var method = typeof(SwiftMarshal)
            .GetMethod("GetTypeMetadataForType", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        Assert.Null(method!.GetCustomAttribute<RequiresDynamicCodeAttribute>());
    }

    #endregion
}
