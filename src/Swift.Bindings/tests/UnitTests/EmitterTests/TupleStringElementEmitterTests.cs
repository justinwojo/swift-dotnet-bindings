// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A <c>Swift.String</c> element inside a RETURNED tuple must be read from the indirect-result
/// buffer as the ADDRESS of its 16-byte inline value (P/Invoke element type <c>IntPtr</c>) and
/// materialized via <c>SwiftMarshal.MarshalFromSwiftObject&lt;SwiftString&gt;(addr).ToString()</c>.
///
/// The historical divergence this pins against: if the tuple's P/Invoke element type were the
/// blittable value form <c>Swift.SwiftString.Buffer</c> while the marshalling statement expected a
/// <c>SwiftString</c>, the generated body assigned a <c>SwiftString.Buffer</c> where a
/// <c>SwiftString</c> was required — a CS0029 that killed the whole binding, which is why the
/// original named-tuple-with-String fixture was deleted instead of the mechanism being fixed. The
/// element label on a named tuple is irrelevant to marshalling — the buffer read is positional — so
/// these assertions equally cover the named and positional shapes.
///
/// Exercises the tuple-return seams in <c>WrapperEmitter.Return.cs</c> directly:
///   * <c>GetCSharpTypeForTupleElement</c> — the element's public C# type.
///   * <c>GetPInvokeTypeForTupleElement</c> — the buffer-read P/Invoke type.
///   * <c>GetTupleElementMarshalCode</c> — the per-element marshalling statement.
/// </summary>
public class TupleStringElementEmitterTests
{
    [Fact]
    public void TupleElementType_SwiftString_IsIdiomaticString()
    {
        var emitter = CreateWrapperEmitter();
        var str = new NamedTypeSpec("Swift.String");

        Assert.Equal("string", emitter.GetCSharpTypeForTupleElement(str));
    }

    [Fact]
    public void TuplePInvokeType_SwiftString_IsIntPtrNotBuffer()
    {
        // The buffer reader takes the ADDRESS of the inline 16-byte value (IntPtr), then
        // MarshalFromSwiftObject reads the full value from that address. It must NOT be the
        // blittable "Swift.SwiftString.Buffer" value form — that is the type mismatch that made
        // the returned tuple fail to compile (SwiftString.Buffer where a SwiftString was expected).
        var emitter = CreateWrapperEmitter();
        var str = new NamedTypeSpec("Swift.String");

        var pinvoke = emitter.GetPInvokeTypeForTupleElement(str);
        Assert.Equal("IntPtr", pinvoke);
        Assert.DoesNotContain(".Buffer", pinvoke);
    }

    [Fact]
    public void TupleElementMarshal_SwiftString_MaterializesViaMarshalFromSwiftObject()
    {
        var emitter = CreateWrapperEmitter();
        var str = new NamedTypeSpec("Swift.String");

        var code = emitter.GetTupleElementMarshalCode(str, "_raw0", "elem0", "string");

        Assert.NotNull(code);
        // Reads the inline value from the buffer address and projects to a managed string.
        Assert.Contains("MarshalFromSwiftObject<SwiftString>(_raw0)", code!);
        Assert.Contains(".ToString()", code);
        // Must NOT touch the blittable Buffer form nor assign a bare SwiftString.Buffer — the
        // CS0029 shape the deleted fixture surfaced.
        Assert.DoesNotContain(".Buffer", code);
    }

    [Fact]
    public void TuplePInvokeType_FoundationData_IsIntPtrNotBuffer()
    {
        // Sibling buffer-backed element family: Foundation.Data is a 16-byte inline value read the
        // same address-of way, never as a ".Buffer" value form.
        var emitter = CreateWrapperEmitter();
        var data = new NamedTypeSpec("Foundation.Data");

        var pinvoke = emitter.GetPInvokeTypeForTupleElement(data);
        Assert.Equal("IntPtr", pinvoke);
        Assert.DoesNotContain(".Buffer", pinvoke);
    }

    [Fact]
    public void TupleElementMarshal_BlittablePrimitive_Unaffected()
    {
        // Guard: the buffer-backed handling is specific to inline value types. A frozen blittable
        // Int element is read by value and passed straight through — no MarshalFromSwiftObject.
        var emitter = CreateWrapperEmitter();
        var intSpec = new NamedTypeSpec("Swift.Int");

        var code = emitter.GetTupleElementMarshalCode(intSpec, "_raw0", "elem0", "long");
        Assert.NotNull(code);
        Assert.DoesNotContain("MarshalFromSwiftObject", code!);
        Assert.DoesNotContain(".Buffer", code);
    }

    /// <summary>
    /// Builds a minimal <see cref="WrapperEmitter"/> over a free function returning Swift.Int, with
    /// Swift.String and Foundation.Data registered as frozen, memory-managed value types (the flags
    /// that drive the buffer-backed element handling). The tuple-element helpers under test operate
    /// on a supplied <see cref="TypeSpec"/>, so the host method's own return shape is irrelevant.
    /// </summary>
    private static WrapperEmitter CreateWrapperEmitter()
    {
        var moduleDecl = new ModuleDecl
        {
            Name = "TestModule",
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };

        var csSignature = new List<ArgumentDecl>
        {
            new ArgumentDecl
            {
                SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "now",
            MangledName = "$s10TestModule3nowSiyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = csSignature,
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };

        var typeDatabase = new TypeDatabase();
        var module = new ModuleTypeDatabase("TestModule", "/fake/path");
        typeDatabase.AddModuleDatabase(module);

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        var intTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int");
        swiftModule.RegisterType(intTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Int64"),
            SwiftTypeName = intTypeName,
            MetadataAccessor = "$sSiMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        var stringTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String");
        swiftModule.RegisterType(stringTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
            SwiftTypeName = stringTypeName,
            MetadataAccessor = "$sSSMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(swiftModule);

        var foundationModule = new ModuleTypeDatabase("Foundation", "/fake/foundation");
        var dataTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Data");
        foundationModule.RegisterType(dataTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "Data"),
            SwiftTypeName = dataTypeName,
            MetadataAccessor = "$s10Foundation4DataVMa",
            Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(foundationModule);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = (MethodEnvironment)handler.Marshal(methodDecl, typeDatabase);
        var signatureHandler = new SignatureHandler(env);
        return new WrapperEmitter(env, signatureHandler);
    }
}
