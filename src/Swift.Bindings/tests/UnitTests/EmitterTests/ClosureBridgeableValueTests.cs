// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// ObjC-bridgeable Swift value types (<c>URL</c>, <c>URLRequest</c>, …) crossing a C#-supplied
/// closure. Their C# projection is the Foundation class peer, but inside the closure the slot holds
/// the Swift struct, so the value has to be bridged to the peer (<c>as AnyObject</c>) on the Swift
/// side before the managed handler reads it as an object, and a returned peer has to be bridged back.
/// Only the <c>@_cdecl</c> closure adapter carries that code, so these shapes must route there, and
/// the direct <c>CallConvSwift</c> lane must refuse them rather than read the struct as an object.
/// </summary>
public class ClosureBridgeableValueTests
{
    private static NamedTypeSpec Url => new("Foundation.URL");
    private static NamedTypeSpec UrlRequest => new("Foundation.URLRequest");

    private static NamedTypeSpec Optional(TypeSpec inner)
    {
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(inner);
        return optional;
    }

    private static ClosureTypeSpec Closure(TypeSpec returnType, bool throws, params TypeSpec[] args)
    {
        var closure = new ClosureTypeSpec(
            args.Length == 0 ? TupleTypeSpec.Empty : new TupleTypeSpec(args), returnType);
        closure.Attributes.Add(new TypeSpecAttribute("escaping"));
        if (throws)
            closure.Attributes.Add(new TypeSpecAttribute("throws"));
        return closure;
    }

    #region Classification

    [Fact]
    public void IsObjCBridgeableValue_SeparatesBridgeableStructsFromClassesAndPlainStructs()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.True(handler.IsObjCBridgeableValue(Url));
        Assert.True(handler.IsObjCBridgeableValue(UrlRequest));
        // An ObjC class is already an object pointer; it needs no bridging.
        Assert.False(handler.IsObjCBridgeableValue(new NamedTypeSpec("Foundation.NSError")));
        // A struct with no Foundation peer has nothing to bridge to.
        Assert.False(handler.IsObjCBridgeableValue(new NamedTypeSpec("TestModule.Plain")));
        Assert.False(handler.IsObjCBridgeableValue(Optional(Url)));

        Assert.True(handler.IsOptionalObjCBridgeableValue(Optional(Url)));
        Assert.False(handler.IsOptionalObjCBridgeableValue(Url));
        Assert.False(handler.IsOptionalObjCBridgeableValue(Optional(new NamedTypeSpec("Foundation.NSError"))));
    }

    [Theory]
    [InlineData("bare")]
    [InlineData("optional")]
    [InlineData("tuple")]
    [InlineData("double-optional")]
    public void ClassifyDirectClosureArg_InlineBridgeableValue_IsUnmodelled(string shape)
    {
        // The direct trampoline has no Swift-side code to bridge the value to its peer, so any
        // position that carries the struct inline must refuse rather than read it as an object.
        var handler = new ClosureHandler(CreateTypeDatabase());
        TypeSpec spec = shape switch
        {
            "bare" => Url,
            "optional" => Optional(UrlRequest),
            "tuple" => new TupleTypeSpec(new TypeSpec[] { new NamedTypeSpec("Swift.Int32"), Url }),
            _ => Optional(Optional(Url)),
        };

        Assert.Equal(DirectClosureArgAbi.Unmodelled, handler.ClassifyDirectClosureArg(spec).Abi);
    }

    [Fact]
    public void ClassifyDirectClosureArg_ObjCClass_IsNotRefusedByTheBridgeableRule()
    {
        var handler = new ClosureHandler(CreateTypeDatabase());

        Assert.False(handler.ContainsInlineObjCBridgeableValue(new NamedTypeSpec("Foundation.NSError")));
        Assert.False(handler.ContainsInlineObjCBridgeableValue(new NamedTypeSpec("Swift.Int32")));
    }

    [Theory]
    [InlineData("arg", false)]
    [InlineData("optional-arg", false)]
    [InlineData("return", false)]
    [InlineData("optional-return", false)]
    [InlineData("return", true)]
    [InlineData("optional-return", true)]
    [InlineData("arg", true)]
    public void IsClosureCdeclCompatible_BridgeableValueShapes_RouteToTheAdapter(string shape, bool throws)
    {
        var handler = new ClosureHandler(CreateTypeDatabase());
        var closure = shape switch
        {
            "arg" => Closure(TupleTypeSpec.Empty, throws, UrlRequest),
            "optional-arg" => Closure(TupleTypeSpec.Empty, throws, Optional(Url)),
            "return" => Closure(Url, throws),
            _ => Closure(Optional(Url), throws),
        };

        Assert.True(handler.IsSupportedClosure(closure));
        Assert.True(ClosureEmitter.IsClosureCdeclCompatible(closure, handler));
    }

    [Theory]
    [InlineData("bare", false, true, false)]
    [InlineData("optional", false, true, false)]
    [InlineData("bare", true, true, true)]
    [InlineData("bare", false, false, true)]
    public void PropertySetter_BridgeableValueClosure_SkipsOnlyWhereTheWrapperCannotCarryIt(
        string shape, bool genericParent, bool xcframework, bool expectSkip)
    {
        // A settable closure property hands its closure to Swift through the property wrapper's
        // @_cdecl adapter wherever that wrapper takes the setter — a bare closure only on a
        // nongeneric class — and through the direct trampoline everywhere else, which must refuse a
        // bridgeable value.
        var db = CreateTypeDatabase();
        if (xcframework)
            db.AsyncLibraryName = "TestModuleSwiftBindings";
        var module = CreateModuleDecl("TestModule");
        var parent = CreateClassDecl("Loader", module);
        if (genericParent)
        {
            parent.GenericParameters = new List<GenericArgumentDecl>
            {
                new("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>())
            };
        }
        var closure = Closure(TupleTypeSpec.Empty, false, UrlRequest);
        var property = TestDecls.Property("onRequest",
            shape == "bare" ? closure : Optional(closure), hasSetter: true, module: "TestModule");
        property.ParentDecl = parent;
        property.ModuleDecl = module;

        var skipped = ClosureEmitter.HasUnmodelledDirectLaneClosureAccessorArg(
            property, new ClosureHandler(db), db, out var refusedShape);

        Assert.Equal(expectSkip, skipped);
        if (expectSkip)
            Assert.Contains("URLRequest", refusedShape);
    }

    #endregion

    #region Emission

    [Fact]
    public void Emit_BridgeableArgument_BridgesToThePeerInSwiftAndReadsAnObjectInCSharp()
    {
        var (cs, swift) = EmitWithClosure(Closure(TupleTypeSpec.Empty, false, new NamedTypeSpec("Swift.Int32"), UrlRequest));

        // The callback is the @_cdecl one (an IntPtr context), not the direct SwiftSelf trampoline.
        Assert.Contains("IntPtr contextPtr", cs);
        Assert.DoesNotContain("SwiftSelf context", cs);
        // The Swift adapter bridges the struct to its Foundation object and keeps it alive across
        // the call; the C# side then reads an object pointer.
        Assert.Contains("as Swift.AnyObject", swift);
        Assert.Contains("Swift.Unmanaged.passUnretained(", swift);
        Assert.Contains("withExtendedLifetime", swift);
        Assert.Matches(@"GetNSObject<(global::)?Foundation\.NSUrlRequest>", cs);
    }

    [Fact]
    public void Emit_OptionalBridgeableArgument_PassesNilAsANullPointer()
    {
        var (cs, swift) = EmitWithClosure(Closure(TupleTypeSpec.Empty, false, Optional(Url)));

        Assert.Contains("Swift.UnsafeMutableRawPointer?", swift);
        Assert.Contains(".map { $0 as Swift.AnyObject }", swift);
        Assert.Matches(@"GetNSObject<(global::)?Foundation\.NSUrl>", cs);
        Assert.Contains("!= null ?", cs);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Emit_BridgeableReturn_HandsThePeerOverAtPlusOneAndBridgesItBack(bool optional, bool throws)
    {
        TypeSpec ret = optional ? Optional(Url) : Url;
        var (cs, swift) = EmitWithClosure(Closure(ret, throws));

        Assert.Contains("CallConvCdecl", cs);
        // C# retains the peer's handle before handing it to Swift, and Swift takes that +1.
        Assert.Contains("global::Swift.Runtime.Arc.UnknownObjectRetain(", cs);
        Assert.Contains("GC.KeepAlive(", cs);
        Assert.Contains("takeRetainedValue() as! Foundation.URL", swift);
        if (optional)
            Assert.Contains(".map {", swift);
    }

    #endregion

    #region Helpers

    private static (string cs, string swift) EmitWithClosure(ClosureTypeSpec closure)
    {
        var db = CreateTypeDatabase();
        var module = CreateModuleDecl("TestModule");
        var parent = CreateClassDecl("Loader", module);
        var method = new MethodDecl
        {
            Name = "handle",
            MangledName = "$s10TestModule6LoaderC6handleyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl> { CreateArgument(string.Empty, TupleTypeSpec.Empty, module) },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parent,
            ModuleDecl = module,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parent.Methods.Add(method);
        method.CSSignature.Add(CreateArgument("handler", closure, module));

        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(method, db);
        handler.Emit(new CSharpWriter(csOutput), new SwiftWriter(swiftOutput), env,
            new Conductor(new NullLoggerFactory()), TypeHandlerContext.Empty);
        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static void Register(ModuleTypeDatabase module, string swiftName, CSharpTypeName csName,
        TypeRecordFlags flags, TypeRecordKind kind, CSharpTypeName? nativeType = null)
    {
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName(swiftName),
            new TypeRecord
            {
                CSharpTypeName = csName,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(swiftName),
                MetadataAccessor = "$sMa",
                Flags = flags,
                Kind = kind,
                NativeTypeName = nativeType
            });
    }

    private static TypeDatabase CreateTypeDatabase()
    {
        var db = new TypeDatabase();

        var swift = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        Register(swift, "Swift.Int32", CSharpTypeName.FromNamespaceAndName("System", "Int32"),
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);
        Register(swift, "Swift.Bool", CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
            TypeRecordFlags.Frozen, TypeRecordKind.Struct);
        Register(swift, "Swift.Optional", CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Struct);
        Register(swift, "Swift.String", CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
            TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Struct);
        db.AddModuleDatabase(swift);

        var foundation = new ModuleTypeDatabase("Foundation", "/usr/lib/swift/libswiftFoundation.dylib");
        var bridgeable = TypeRecordFlags.RequiresMemoryManagement | TypeRecordFlags.ObjCBridgeable;
        Register(foundation, "Foundation.URL", CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "URL"),
            bridgeable, TypeRecordKind.Struct, CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrl"));
        Register(foundation, "Foundation.URLRequest", CSharpTypeName.FromNamespaceAndName("Swift.Foundation", "URLRequest"),
            bridgeable, TypeRecordKind.Struct, CSharpTypeName.FromNamespaceAndName("Foundation", "NSUrlRequest"));
        Register(foundation, "Foundation.NSError", CSharpTypeName.FromNamespaceAndName("Foundation", "NSError"),
            TypeRecordFlags.ObjCBridged | TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        db.AddModuleDatabase(foundation);

        var test = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        Register(test, "TestModule.Loader", CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
            TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Class);
        Register(test, "TestModule.Plain", CSharpTypeName.FromNamespaceAndName("TestModule", "Plain"),
            TypeRecordFlags.RequiresMemoryManagement, TypeRecordKind.Struct);
        db.AddModuleDatabase(test);

        return db;
    }

    private static ModuleDecl CreateModuleDecl(string name) => new()
    {
        Name = name,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
        ParentDecl = null,
        ModuleDecl = null
    };

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);
        return classDecl;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl) => new()
    {
        SwiftTypeSpec = typeSpec,
        Name = name,
        PrivateName = string.Empty,
        IsInOut = false,
        IsGeneric = false,
        ParentDecl = null,
        ModuleDecl = moduleDecl
    };

    #endregion
}
