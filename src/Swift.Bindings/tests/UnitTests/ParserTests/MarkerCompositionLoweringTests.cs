// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// A class composed only with marker protocols (<c>any SomeClass &amp; Sendable</c>) is the class
/// at runtime, so it projects as the class. These cover which positions take the rewrite and which
/// must keep the composition because Swift would not accept the class there.
/// </summary>
public class MarkerCompositionLoweringTests
{
    private const string Module = TestDecls.DefaultModule;
    private const string Composition = "any TestModule.Asset & Swift.Sendable";

    private static TypeDatabase Db()
    {
        var db = new TypeDatabase();
        var module = new ModuleTypeDatabase(Module, $"/tmp/{Module}.dylib");
        void Register(string name, TypeRecordKind kind)
        {
            var swiftName = SwiftTypeName.FromModuleQualifiedName($"{Module}.{name}");
            module.RegisterType(swiftName, new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName(Module, name),
                SwiftTypeName = swiftName,
                MetadataAccessor = string.Empty,
                Flags = TypeRecordFlags.None,
                Kind = kind,
            });
        }
        Register("Asset", TypeRecordKind.Class);
        Register("Clip", TypeRecordKind.Struct);
        Register("Describable", TypeRecordKind.Protocol);
        Register("Box", TypeRecordKind.Struct);
        Register("Shelf", TypeRecordKind.Class);
        db.AddModuleDatabase(module);
        return db;
    }

    private static TypeSpec Spec(string text) => TypeSpecParser.Parse(text)!;

    private static ModuleDecl ModuleWith(params BaseDecl[] members)
    {
        var module = new ModuleDecl
        {
            Name = Module,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null,
        };
        foreach (var member in members)
        {
            switch (member)
            {
                case MethodDecl method: module.Methods.Add(method); break;
                case PropertyDecl property: module.Properties.Add(property); break;
                case ProtocolDecl protocol: module.Protocols.Add(protocol); break;
                case TypeDecl type: module.Types.Add(type); break;
            }
        }
        return module;
    }

    private static TypeSpec LowerParameter(string type, out int rewritten)
    {
        var method = TestDecls.Method("take", parameters: [TestDecls.Param("asset", Spec(type))]);
        rewritten = MarkerCompositionLowering.Apply(ModuleWith(method), Db());
        return method.CSSignature[1].SwiftTypeSpec;
    }

    [Theory]
    [InlineData(Composition, "TestModule.Asset")]
    [InlineData("Swift.Optional<" + Composition + ">", "Swift.Optional<TestModule.Asset>")]
    [InlineData("Swift.Array<" + Composition + ">", "Swift.Array<TestModule.Asset>")]
    [InlineData("Swift.Dictionary<Swift.String, " + Composition + ">", "Swift.Dictionary<Swift.String, TestModule.Asset>")]
    [InlineData("Swift.Set<" + Composition + ">", "Swift.Set<TestModule.Asset>")]
    // A specialized generic class is erased by a marker exactly as a plain one is.
    [InlineData("any TestModule.Shelf<Swift.Int> & Swift.Sendable", "TestModule.Shelf<Swift.Int>")]
    public void ClassWithMarkerProtocols_ProjectsAsTheClass(string type, string expected)
    {
        var lowered = LowerParameter(type, out int rewritten);

        Assert.Equal(Spec(expected).ToString(), lowered.ToString());
        Assert.True(rewritten > 0);
    }

    [Theory]
    // A protocol with a witness table makes the existential two words, not the bare reference.
    [InlineData("any TestModule.Asset & TestModule.Describable")]
    // Only a class participant collapses to a single reference.
    [InlineData("any TestModule.Clip & Swift.Sendable")]
    // Swift's own generic types are invariant: Box<Asset> is not Box<any Asset & Sendable>.
    [InlineData("TestModule.Box<" + Composition + ">")]
    public void CompositionThatIsNotTheBareClass_IsLeftAlone(string type)
    {
        var original = Spec(type).ToString();

        var lowered = LowerParameter(type, out int rewritten);

        Assert.Equal(original, lowered.ToString());
        Assert.Equal(0, rewritten);
    }

    [Fact]
    public void ClosureParameterAndReturn_AreRewritten()
    {
        var lowered = LowerParameter("(" + Composition + ") -> (" + Composition + ")", out int rewritten);

        var closure = Assert.IsType<ClosureTypeSpec>(lowered);
        // A single closure argument is carried bare, a list of them as a tuple.
        var argument = closure.Arguments is TupleTypeSpec list ? Assert.Single(list.Elements) : closure.Arguments;
        Assert.Equal("TestModule.Asset", Assert.IsType<NamedTypeSpec>(argument).Name);
        Assert.Equal("TestModule.Asset", Assert.IsType<NamedTypeSpec>(closure.ReturnType).Name);
        Assert.Equal(2, rewritten);
    }

    [Fact]
    public void OpaqueComposition_IsLeftAlone()
    {
        var opaque = Assert.IsType<ProtocolListTypeSpec>(Spec(Composition));
        opaque.IsOpaque = true;
        var method = TestDecls.Method("take", parameters: [TestDecls.Param("asset", opaque)]);

        Assert.Equal(0, MarkerCompositionLowering.Apply(ModuleWith(method), Db()));
        Assert.Same(opaque, method.CSSignature[1].SwiftTypeSpec);
    }

    [Fact]
    public void ReturnAndProperty_AreRewritten()
    {
        var method = TestDecls.Method("echo", returnType: Spec(Composition));
        var property = TestDecls.Property("current", Spec(Composition));

        MarkerCompositionLowering.Apply(ModuleWith(method, property), Db());

        Assert.Equal("TestModule.Asset", method.CSSignature[0].SwiftTypeSpec.ToString());
        Assert.Equal("TestModule.Asset", property.SwiftTypeSpec.ToString());
    }

    [Fact]
    public void PropertyAndSubscriptAccessors_AreRewrittenWithTheirMember()
    {
        // The getter and setter are methods of their own carrying the member's type; the
        // emitter reads them, so they must agree with the member they implement.
        var getter = TestDecls.Method("current_get", returnType: Spec(Composition));
        var setter = TestDecls.Method("current_set", parameters: [TestDecls.Param("newValue", Spec(Composition))]);
        var property = new PropertyDecl
        {
            Name = "current",
            SwiftTypeSpec = Spec(Composition),
            IsStatic = false,
            HasStorage = true,
            Accessors = [new GetAccessorDecl { Method = getter }, new SetAccessorDecl { Method = setter }],
            ParentDecl = null,
            ModuleDecl = null,
        };
        var subscriptGetter = TestDecls.Method("subscript_get", returnType: Spec(Composition),
            parameters: [TestDecls.Param("slot", Spec("Swift.Int"))]);
        var subscript = new SubscriptDecl
        {
            Name = "subscript",
            ReturnTypeSpec = Spec(Composition),
            IndexParameters = [TestDecls.Param("slot", Spec("Swift.Int"))],
            IsStatic = false,
            Accessors = [new GetAccessorDecl { Method = subscriptGetter }],
            MangledName = "$s10TestModule4SlotCyAA5AssetCSicig",
            ParentDecl = null,
            ModuleDecl = null,
        };
        var holder = new ClassDecl
        {
            Name = "Slot",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{Module}.Slot"),
            MangledName = "$s10TestModule4SlotCN",
            Properties = [property],
            Methods = [],
            Types = [],
            Operators = [],
            Subscripts = [subscript],
            GenericParameters = [],
            Conformances = [],
            ParentDecl = null,
            ModuleDecl = null,
        };

        MarkerCompositionLowering.Apply(ModuleWith(holder), Db());

        Assert.Equal("TestModule.Asset", getter.CSSignature[0].SwiftTypeSpec.ToString());
        Assert.Equal("TestModule.Asset", setter.CSSignature[1].SwiftTypeSpec.ToString());
        Assert.Equal("TestModule.Asset", subscriptGetter.CSSignature[0].SwiftTypeSpec.ToString());
        Assert.Equal("TestModule.Asset", subscript.ReturnTypeSpec.ToString());
    }

    [Fact]
    public void InOutParameter_IsLeftAlone()
    {
        var parameter = TestDecls.Param("asset", Spec(Composition));
        parameter.IsInOut = true;
        var method = TestDecls.Method("swap", parameters: [parameter]);

        Assert.Equal(0, MarkerCompositionLowering.Apply(ModuleWith(method), Db()));
        Assert.IsType<ProtocolListTypeSpec>(method.CSSignature[1].SwiftTypeSpec);
    }

    [Fact]
    public void ProtocolRequirement_IsLeftAlone()
    {
        var requirement = TestDecls.Method("accept", parameters: [TestDecls.Param("asset", Spec(Composition))]);
        var protocol = TestDecls.Protocol("Consumer", requirement);

        Assert.Equal(0, MarkerCompositionLowering.Apply(ModuleWith(protocol), Db()));
        Assert.IsType<ProtocolListTypeSpec>(requirement.CSSignature[1].SwiftTypeSpec);
    }

    [Fact]
    public void ObjectiveCRootClass_IsRecognizedWithoutATypeRecord()
    {
        var lowered = LowerParameter("any Foundation.NSObject & Swift.Sendable", out int rewritten);

        Assert.Equal("Foundation.NSObject", lowered.ToString());
        Assert.Equal(1, rewritten);
    }

    [Fact]
    public void Rewrite_KeepsThePositionsTupleLabel()
    {
        var lowered = LowerParameter("(first: " + Composition + ", count: Swift.Int)", out _);

        var tuple = Assert.IsType<TupleTypeSpec>(lowered);
        Assert.Equal("first", tuple.Elements[0].TypeLabel);
        Assert.Equal("TestModule.Asset", Assert.IsType<NamedTypeSpec>(tuple.Elements[0]).Name);
    }
}
