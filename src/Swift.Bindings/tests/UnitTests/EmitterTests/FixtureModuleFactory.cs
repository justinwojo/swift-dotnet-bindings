// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;

namespace BindingsGeneration.Tests;

/// <summary>
/// Builds a deliberately broad in-memory module plus a matching <see cref="TypeDatabase"/>, for
/// tests that need the whole <see cref="StringEmitter.EmitModule"/> path to actually run rather
/// than a single handler in isolation.
///
/// <para>Breadth is the point: each additional shape brings another dedup registry, name allocator
/// and emitter family into the render, so a whole-render property gate only covers the machinery
/// the fixture forces to run. Shared by the determinism gate and the fragment interval-map gate so
/// both keep exercising the same surface as it grows.</para>
/// </summary>
internal static class FixtureModuleFactory
{
    /// <summary>
    /// A protocol with method and property requirements plus an extension default, a frozen and a
    /// non-frozen struct, a class with overloaded members, closures, async and throwing members,
    /// and free functions.
    /// </summary>
    public static ModuleDecl BuildModule(string moduleName)
    {
        var moduleDecl = new ModuleDecl
        {
            Name = moduleName,
            Dependencies = new List<string>(),
            Types = new List<TypeDecl>(),
            Methods = new List<MethodDecl>(),
            Properties = new List<PropertyDecl>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };

        var intSpec = new NamedTypeSpec("Swift.Int");
        var stringSpec = new NamedTypeSpec("Swift.String");
        var boolSpec = new NamedTypeSpec("Swift.Bool");
        var doubleSpec = new NamedTypeSpec("Swift.Double");

        // Protocol: requirements (vtable slots) + an extension default (no slot), and a
        // label-only overload pair so the disambiguation/collision machinery participates.
        var sink = TestDecls.Protocol(
            "ShapeSink", moduleName,
            TestDecls.Method("accept", parameters: new[] { TestDecls.Param("value", intSpec) }, module: moduleName),
            TestDecls.Method("accept", parameters: new[] { TestDecls.Param("text", stringSpec) }, module: moduleName),
            TestDecls.Method("total", returnType: intSpec, module: moduleName),
            TestDecls.Property("label", stringSpec, module: moduleName),
            TestDecls.Property("isEmpty", boolSpec, module: moduleName),
            TestDecls.ExtensionDefault("describe", returnType: stringSpec, module: moduleName));
        moduleDecl.Protocols.Add(sink);
        moduleDecl.Types.Add(sink);

        // Frozen struct: value-type marshalling + a throwing and an async member.
        var point = Struct("Point", moduleName, moduleDecl, isFrozen: true);
        point.Properties.Add(TestDecls.Property("x", doubleSpec, module: moduleName));
        point.Properties.Add(TestDecls.Property("y", doubleSpec, hasSetter: true, module: moduleName));
        point.Methods.Add(TestDecls.Method("magnitude", returnType: doubleSpec, module: moduleName));
        point.Methods.Add(TestDecls.Method("scaled", parameters: new[] { TestDecls.Param("by", doubleSpec) }, returnType: doubleSpec, module: moduleName));
        point.Methods.Add(TestDecls.Method("validate", throws: true, module: moduleName));
        moduleDecl.Types.Add(point);

        // Non-frozen struct: opaque-payload class projection — a different marshalling family.
        var box = Struct("Box", moduleName, moduleDecl, isFrozen: false);
        box.Properties.Add(TestDecls.Property("contents", stringSpec, hasSetter: true, module: moduleName));
        box.Methods.Add(TestDecls.Method("clear", module: moduleName));
        box.Methods.Add(TestDecls.Method("load", isAsync: true, returnType: stringSpec, module: moduleName));
        moduleDecl.Types.Add(box);

        // Class: reference-type ARC path plus an overload set whose projected C# names collide,
        // which is what drives the collision-suffix allocator.
        var registry = Class("Registry", moduleName, moduleDecl);
        registry.Properties.Add(TestDecls.Property("count", intSpec, module: moduleName));
        // Swift lets a static and an instance property share a name; C# does not (CS0102), so these
        // two contend for one `Count` and the loser is dropped as a duplicate. That contention is the
        // point: it is the smallest shape in which one member's fate depends on another member having
        // claimed the name first, which is what a denied declaration must not be able to do.
        registry.Properties.Add(TestDecls.Property("count", intSpec, isStatic: true, module: moduleName));
        registry.Properties.Add(TestDecls.Property("name", stringSpec, hasSetter: true, module: moduleName));
        registry.Methods.Add(TestDecls.Method("register", parameters: new[] { TestDecls.Param("first", intSpec) }, module: moduleName));
        registry.Methods.Add(TestDecls.Method("register", parameters: new[] { TestDecls.Param("second", intSpec) }, module: moduleName));
        registry.Methods.Add(TestDecls.Method("register", parameters: new[] { TestDecls.Param("third", stringSpec) }, module: moduleName));
        registry.Methods.Add(TestDecls.Method("reset", methodType: MethodType.Static, module: moduleName));
        registry.Methods.Add(TestDecls.Method("fetch", isAsync: true, returnType: intSpec, module: moduleName));
        moduleDecl.Types.Add(registry);

        // Free functions, including a closure parameter (callback thunk emission) and a throwing one.
        moduleDecl.Methods.Add(
            TestDecls.Method("makeDefaultPoint", methodType: MethodType.Static, returnType: doubleSpec, module: moduleName));
        moduleDecl.Methods.Add(
            TestDecls.Method(
                "transform",
                methodType: MethodType.Static,
                parameters: new[] { TestDecls.Param("using", ClosureSpec(intSpec, intSpec)) },
                returnType: intSpec,
                module: moduleName));
        moduleDecl.Methods.Add(
            TestDecls.Method("riskyOperation", methodType: MethodType.Static, throws: true, returnType: boolSpec, module: moduleName));

        // The decl factories leave ownership unset (they build free-standing decls), so stitch the
        // whole tree's ParentDecl/ModuleDecl in one pass — including each property accessor's
        // backing method, which the emitter dereferences to resolve the declaring type.
        foreach (var type in moduleDecl.Types)
            Reparent(type, moduleDecl);
        foreach (var method in moduleDecl.Methods)
            Own(method, moduleDecl, moduleDecl);
        foreach (var property in moduleDecl.Properties)
            OwnProperty(property, moduleDecl, moduleDecl);

        return moduleDecl;
    }

    /// <summary>An escaping <c>(In) -&gt; Out</c> closure parameter spec.</summary>
    private static ClosureTypeSpec ClosureSpec(TypeSpec argument, TypeSpec returnType)
    {
        var spec = new ClosureTypeSpec(argument, returnType);
        spec.Attributes.Add(new TypeSpecAttribute("escaping"));
        return spec;
    }

    private static StructDecl Struct(string name, string moduleName, ModuleDecl moduleDecl, bool isFrozen) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
        MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VN",
        MetadataAccessor = $"$s{moduleName.Length}{moduleName}{name.Length}{name}VMa",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        IsFrozen = isFrozen,
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };

    private static ClassDecl Class(string name, string moduleName, ModuleDecl moduleDecl) => new()
    {
        Name = name,
        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleName}.{name}"),
        MangledName = $"$s{moduleName.Length}{moduleName}{name.Length}{name}CN",
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Operators = new List<OperatorDecl>(),
        Subscripts = new List<SubscriptDecl>(),
        GenericParameters = new List<GenericArgumentDecl>(),
        Conformances = new List<TypeConformance>(),
        IsFinal = true,
        ParentDecl = moduleDecl,
        ModuleDecl = moduleDecl,
    };

    private static void Reparent(TypeDecl type, ModuleDecl moduleDecl)
    {
        Own(type, moduleDecl, moduleDecl);
        foreach (var method in type.Methods)
            Own(method, type, moduleDecl);
        foreach (var property in type.Properties)
            OwnProperty(property, type, moduleDecl);
        foreach (var nested in type.Types)
            Reparent(nested, moduleDecl);
    }

    private static void OwnProperty(PropertyDecl property, BaseDecl parent, ModuleDecl moduleDecl)
    {
        Own(property, parent, moduleDecl);
        foreach (var accessor in property.Accessors)
            Own(accessor.Method, parent, moduleDecl);
    }

    private static void Own(BaseDecl decl, BaseDecl parent, ModuleDecl moduleDecl)
    {
        decl.ParentDecl = parent;
        decl.ModuleDecl = moduleDecl;
    }

    public static TypeDatabase BuildTypeDatabase(ModuleDecl moduleDecl)
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        RegisterStdlibType(swiftModule, "Swift.Int", CSharpTypeName.NIntType, "$sSiMa");
        RegisterStdlibType(swiftModule, "Swift.Bool", CSharpTypeName.FromNamespaceAndName("System", "Boolean"), "$sSbMa");
        RegisterStdlibType(swiftModule, "Swift.Double", CSharpTypeName.FromNamespaceAndName("System", "Double"), "$sSdMa");
        RegisterStdlibType(swiftModule, "Swift.String", CSharpTypeName.FromNamespaceAndName("Swift.Runtime", "SwiftString"), "$sSSMa");
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase(moduleDecl.Name, $"/fake/{moduleDecl.Name}.dylib");
        foreach (var type in moduleDecl.Types)
        {
            var kind = type switch
            {
                ProtocolDecl => TypeRecordKind.Protocol,
                ClassDecl => TypeRecordKind.Class,
                _ => TypeRecordKind.Struct,
            };
            var flags = type is StructDecl { IsFrozen: true } ? TypeRecordFlags.Frozen : TypeRecordFlags.None;

            module.RegisterType(
                type.SwiftTypeName,
                new TypeRecord
                {
                    CSharpTypeName = CSharpTypeName.FromNamespaceAndName(moduleDecl.Name, type.Name),
                    SwiftTypeName = type.SwiftTypeName,
                    MetadataAccessor = (type as StructDecl)?.MetadataAccessor ?? string.Empty,
                    Flags = flags,
                    Kind = kind,
                });
        }
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static void RegisterStdlibType(
        ModuleTypeDatabase module, string qualifiedName, CSharpTypeName csharpName, string metadataAccessor)
    {
        var swiftName = SwiftTypeName.FromModuleQualifiedName(qualifiedName);
        module.RegisterType(
            swiftName,
            new TypeRecord
            {
                CSharpTypeName = csharpName,
                SwiftTypeName = swiftName,
                MetadataAccessor = metadataAccessor,
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct,
            });
    }
}
