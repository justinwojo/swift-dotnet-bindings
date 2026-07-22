// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A <c>Foundation.Data</c> element inside a method's *parameter* tuple surfaces
/// <c>Swift.Foundation.Data</c> in both the public wrapper signature and the P/Invoke
/// declaration through the tuple fallback path, which deliberately bypasses
/// <see cref="TypeProjectionFactory"/> (the P/Invoke expects ABI types and there is no
/// per-element conversion in the wrapper body). The factory is where projection-keyed
/// paths record the Apple-supplement dependency, so the tuple-parameter arms must record
/// it themselves — a signature that emits supplement text without a Record leaves the
/// generated csproj missing the SwiftBindings.Apple PackageReference and the binding's
/// own C# verify build fails CS0234 on 'Swift.Foundation'. Return tuples are unaffected:
/// their elements ARE factory-projected (MethodSignature's return handling), which records.
///
/// These tests drive <see cref="SignatureHandler"/> directly to pin the arm-level
/// invariant: whenever a tuple arm emits supplement type text, it records. In full
/// generation the member validator currently fails closed on non-buffer-marshallable
/// tuple parameters (the @_cdecl tuple buffer cannot carry a frozen-blittable struct
/// element yet), so this exact shape is skipped upstream today; the recording keeps the
/// csproj sound for the buffer-marshallable tuple arms now and for this shape the day
/// the buffer path admits frozen-blittable elements.
/// </summary>
public class TupleDataElementParamEmitterTests
{
    [Fact]
    public void WrapperSignature_TupleParamWithFoundationData_EmitsSupplementTypeAndRecords()
    {
        var signatureHandler = CreateSignatureHandler(
            new TupleTypeSpec(new TypeSpec[]
            {
                new NamedTypeSpec("Foundation.Data"),
                new NamedTypeSpec("Swift.Int")
            }));

        AppleSupplementReferences.Reset();
        try
        {
            var wrapperParams = signatureHandler.GetWrapperSignature().ParametersString();
            var pinvokeParams = signatureHandler.GetPInvokeSignature().ParametersString();

            // The tuple arms did surface the supplement type — this is what makes the
            // recorded reference load-bearing.
            Assert.Contains("Swift.Foundation.Data", wrapperParams);
            Assert.Contains("Swift.Foundation.Data", pinvokeParams);
            // The supplement dependency must be recorded so the consumer csproj
            // references SwiftBindings.Apple.
            Assert.Contains("Foundation.Data", AppleSupplementReferences.Current);
        }
        finally
        {
            // The collector is [ThreadStatic]; leave the xunit worker thread clean so a
            // later test on the same thread doesn't observe this test's recorded state.
            AppleSupplementReferences.Reset();
        }
    }

    [Fact]
    public void WrapperSignature_TupleParamWithoutSupplementTypes_RecordsNothing()
    {
        // Guard against over-recording: a tuple of non-supplement elements must not pull
        // in the SwiftBindings.Apple PackageReference.
        var signatureHandler = CreateSignatureHandler(
            new TupleTypeSpec(new TypeSpec[]
            {
                new NamedTypeSpec("Swift.Int"),
                new NamedTypeSpec("Swift.Int")
            }));

        AppleSupplementReferences.Reset();
        try
        {
            signatureHandler.GetWrapperSignature();
            signatureHandler.GetPInvokeSignature();

            Assert.Empty(AppleSupplementReferences.Current);
        }
        finally
        {
            AppleSupplementReferences.Reset();
        }
    }

    /// <summary>
    /// Builds a <see cref="SignatureHandler"/> over a void free function taking a single
    /// tuple parameter of the given spec — the exact shape whose signature construction
    /// runs the tuple-parameter fallback arms in MethodSignature and PInvokeEmitter.
    /// </summary>
    private static SignatureHandler CreateSignatureHandler(TupleTypeSpec tupleParam)
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
            // Return type: void
            new ArgumentDecl
            {
                SwiftTypeSpec = TupleTypeSpec.Empty,
                Name = string.Empty,
                PrivateName = string.Empty,
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl
            },
            new ArgumentDecl
            {
                SwiftTypeSpec = tupleParam,
                Name = "payload",
                PrivateName = "payload",
                IsInOut = false,
                IsGeneric = false,
                ParentDecl = moduleDecl,
                ModuleDecl = moduleDecl
            }
        };

        var methodDecl = new MethodDecl
        {
            Name = "send",
            MangledName = "$s10TestModule4sendyyF",
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
        typeDatabase.AddModuleDatabase(swiftModule);

        // Foundation.Data as the shipped FoundationDatabase.xml registers it: a frozen
        // struct whose managed projection is Swift.Foundation.Data (the Apple supplement).
        var foundationModule = new ModuleTypeDatabase(
            "Foundation", "/System/Library/Frameworks/Foundation.framework/Foundation");
        var dataTypeName = SwiftTypeName.FromModuleQualifiedName("Foundation.Data");
        foundationModule.RegisterType(dataTypeName, new TypeRecord
        {
            CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "Data"),
            SwiftTypeName = dataTypeName,
            MetadataAccessor = "$s10Foundation4DataVMa",
            Flags = TypeRecordFlags.Frozen,
            Kind = TypeRecordKind.Struct
        });
        typeDatabase.AddModuleDatabase(foundationModule);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = (MethodEnvironment)handler.Marshal(methodDecl, typeDatabase);
        return new SignatureHandler(env);
    }
}
