// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Xunit;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

public class ApiDefinitionEmitterTests
{
    static string EmitAndRead(ObjCModule module, string ns = "TestNamespace") =>
        EmitApiDefinition(module, ns);

    // --- Simple class emission ---

    [Fact]
    public void Emit_SimpleClass_HasBaseType()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl { Name = "MyClass" }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[BaseType(typeof(NSObject))]", result);
        Assert.Contains("partial interface MyClass", result);
    }

    [Fact]
    public void Emit_ClassWithSuperclass_UsesSuperclassInBaseType()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl { Name = "MyView", SuperclassName = "UIView" }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[BaseType(typeof(UIView))]", result);
        Assert.Contains("partial interface MyView", result);
    }

    [Fact]
    public void Emit_ClassWithProtocolAdoption_EmitsInterfaceList()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                ProtocolNames = ["UITableViewDelegate", "UIScrollViewDelegate"]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface MyClass : IUITableViewDelegate, IUIScrollViewDelegate", result);
    }

    // --- Constructor ---

    [Fact]
    public void Emit_InitSelector_SuppressedByDisableDefaultCtor()
    {
        // Fix #6: When DisableDefaultCtor is emitted (because init is declared),
        // the explicit [Export("init")] Constructor() is suppressed to avoid contradiction.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "init",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[DisableDefaultCtor]", result);
        Assert.DoesNotContain("[Export(\"init\")]", result);
        Assert.DoesNotContain("Constructor()", result);
    }

    [Fact]
    public void Emit_InitWithSelector_EmitsConstructorWithParams()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "initWithFoo:bar:",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    Parameters =
                    [
                        new ObjCParameterDecl { Name = "foo", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } },
                        new ObjCParameterDecl { Name = "bar", Type = new ObjCTypeRef { Name = "NSInteger" } }
                    ]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"initWithFoo:bar:\")]", result);
        Assert.Contains("NativeHandle Constructor(string foo, nint bar);", result);
    }

    // --- Instance method ---

    [Fact]
    public void Emit_InstanceMethod_HasExportAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "doSomething",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"doSomething\")]", result);
        Assert.Contains("void DoSomething();", result);
    }

    // --- Static method ---

    [Fact]
    public void Emit_ClassMethod_HasStaticAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "sharedInstance",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = false
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Static]", result);
        Assert.Contains("[Export(\"sharedInstance\")]", result);
        Assert.Contains("MyClass SharedInstance();", result);
    }

    // --- Properties ---

    [Fact]
    public void Emit_ReadonlyProperty_HasGetterOnly()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "count",
                    Type = new ObjCTypeRef { Name = "NSInteger" },
                    IsReadonly = true,
                    GetterSelector = "count"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"count\")]", result);
        Assert.Contains("nint Count { get; }", result);
    }

    [Fact]
    public void Emit_ReadwriteProperty_HasGetterAndSetter()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "name",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = false,
                    GetterSelector = "name"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("get;", result);
        Assert.Contains("[Export(\"setName:\")] set;", result);
    }

    [Fact]
    public void Emit_ReadwritePropertyWithCustomSetter_EmitsSetterSelector()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "hidden",
                    Type = new ObjCTypeRef { Name = "BOOL" },
                    IsReadonly = false,
                    GetterSelector = "isHidden",
                    SetterSelector = "setHidden:"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"isHidden\")]", result);
        Assert.Contains("[Export(\"setHidden:\")] set;", result);
    }

    [Fact]
    public void Emit_ClassProperty_HasStaticAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "version",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                    IsClass = true,
                    GetterSelector = "version"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Static]", result);
        Assert.Contains("string Version { get; }", result);
    }

    // --- NullAllowed ---

    [Fact]
    public void Emit_NullableParam_HasNullAllowed()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "setName:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "name",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true, Nullability = ObjCNullability.Nullable }
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[NullAllowed] string name", result);
    }

    [Fact]
    public void Emit_NullableReturn_HasReturnNullAllowed()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "getName",
                    ReturnType = new ObjCTypeRef { Name = "NSString", IsPointer = true, Nullability = ObjCNullability.Nullable },
                    IsInstanceMethod = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[return: NullAllowed]", result);
    }

    [Fact]
    public void Emit_NullableProperty_HasNullAllowed()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "title",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true, Nullability = ObjCNullability.Nullable },
                    IsReadonly = false,
                    GetterSelector = "title"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[NullAllowed]", result);
        Assert.Contains("get;", result);
        Assert.Contains("[Export(\"setTitle:\")] set;", result);
    }

    // --- NSError out parameter ---

    [Fact]
    public void Emit_NSErrorDoublePointer_EmitsOutParam()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "saveWithError:",
                    ReturnType = new ObjCTypeRef { Name = "BOOL" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "error",
                        Type = new ObjCTypeRef
                        {
                            Name = "NSError",
                            IsPointer = true,
                            PointeeType = new ObjCTypeRef { Name = "NSError", IsPointer = true }
                        }
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[NullAllowed] out NSError error", result);
    }

    // --- Block parameter ---

    [Fact]
    public void Emit_BlockParam_EmitsActionOrFunc()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "fetchWithCompletion:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "completion",
                        Type = new ObjCTypeRef
                        {
                            Name = "block",
                            IsBlock = true,
                            BlockReturnType = new ObjCTypeRef { Name = "void" },
                            BlockParams = [new ObjCTypeRef { Name = "BOOL" }]
                        }
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("Action<bool> completion", result);
    }

    [Fact]
    public void Emit_ProtocolMethod_AbsentTypeInsideBlockParam_IsDropped()
    {
        // Mirrors a real-world shape: an [Abstract] protocol method whose block parameter
        // carries a cross-module third-party class that this binding neither declares
        // nor resolves via a using. The outer Action<…> is a known pattern, so without recursing into
        // its arguments the absent inner name leaks into the api-definition contract compile (CS0246).
        // The whole method must be dropped; a sibling whose block argument resolves still emits.
        var absentInsideBlock = new ObjCMethodDecl
        {
            Selector = "resolveAppLinkFromURL:handler:",
            ReturnType = new ObjCTypeRef { Name = "void" },
            IsInstanceMethod = true,
            Parameters =
            [
                new ObjCParameterDecl { Name = "url", Type = new ObjCTypeRef { Name = "NSURL", IsPointer = true } },
                new ObjCParameterDecl
                {
                    Name = "handler",
                    Type = new ObjCTypeRef
                    {
                        Name = "block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams =
                        [
                            new ObjCTypeRef { Name = "ZZThirdPartyType", IsPointer = true },
                            new ObjCTypeRef { Name = "NSError", IsPointer = true },
                        ],
                    },
                },
            ],
        };
        var resolvableSibling = new ObjCMethodDecl
        {
            Selector = "observeWithHandler:",
            ReturnType = new ObjCTypeRef { Name = "void" },
            IsInstanceMethod = true,
            Parameters =
            [
                new ObjCParameterDecl
                {
                    Name = "handler",
                    Type = new ObjCTypeRef
                    {
                        Name = "block",
                        IsBlock = true,
                        BlockReturnType = new ObjCTypeRef { Name = "void" },
                        BlockParams = [new ObjCTypeRef { Name = "NSError", IsPointer = true }],
                    },
                },
            ],
        };

        var module = ObjCModuleBuilder.Create("Test")
            .WithProtocol("AppLinkResolving", p => { p.Method(absentInsideBlock); p.Method(resolvableSibling); })
            .WithAppleSdkTypeNames("NSError")
            .Build();

        var result = EmitApiDefinition(module, "TestNamespace");

        Assert.DoesNotContain("ZZThirdPartyType", result);
        Assert.DoesNotContain("resolveAppLinkFromURL", result);
        Assert.Contains("Action<NSError>", result);
    }

    [Fact]
    public void Emit_ProtocolMethod_AbsentTypeInsideNamedBlockTypedefParam_IsDropped()
    {
        // Mirrors a real-world shape: the block parameter is a *named*
        // block typedef resolved through the block-typedef map — not an inline block — whose expansion
        // carries a cross-module class the binding neither declares nor resolves (in a mixed binding
        // the class is filtered out as Swift-owned, leaving the typedef pointing at an absent name).
        // The named-typedef path is distinct from an inline block in MapType, so it must funnel through
        // the same recursive resolvability gate or the absent inner name leaks into Action<…> (CS0246).
        var absentViaTypedef = new ObjCMethodDecl
        {
            Selector = "resolveAppLinkFromURL:handler:",
            ReturnType = new ObjCTypeRef { Name = "void" },
            IsInstanceMethod = true,
            Parameters =
            [
                new ObjCParameterDecl { Name = "url", Type = new ObjCTypeRef { Name = "NSURL", IsPointer = true } },
                new ObjCParameterDecl { Name = "handler", Type = new ObjCTypeRef { Name = "ZZAppLinkBlock" } },
            ],
        };
        var resolvableViaTypedef = new ObjCMethodDecl
        {
            Selector = "observeWithHandler:",
            ReturnType = new ObjCTypeRef { Name = "void" },
            IsInstanceMethod = true,
            Parameters =
            [
                new ObjCParameterDecl { Name = "handler", Type = new ObjCTypeRef { Name = "ZZErrorBlock" } },
            ],
        };

        var absentBlock = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "void" },
            BlockParams =
            [
                new ObjCTypeRef { Name = "ZZThirdPartyType", IsPointer = true },
                new ObjCTypeRef { Name = "NSError", IsPointer = true },
            ],
        };
        var errorBlock = new ObjCTypeRef
        {
            Name = "block",
            IsBlock = true,
            BlockReturnType = new ObjCTypeRef { Name = "void" },
            BlockParams = [new ObjCTypeRef { Name = "NSError", IsPointer = true }],
        };

        var module = ObjCModuleBuilder.Create("Test")
            .WithTypedef(new ObjCTypedefDecl { Name = "ZZAppLinkBlock", UnderlyingType = absentBlock })
            .WithTypedef(new ObjCTypedefDecl { Name = "ZZErrorBlock", UnderlyingType = errorBlock })
            .WithProtocol("AppLinkResolving", p => { p.Method(absentViaTypedef); p.Method(resolvableViaTypedef); })
            .WithAppleSdkTypeNames("NSError")
            .Build();

        var result = EmitApiDefinition(module, "TestNamespace");

        Assert.DoesNotContain("ZZThirdPartyType", result);
        Assert.DoesNotContain("resolveAppLinkFromURL", result);
        Assert.Contains("Action<NSError>", result);
    }

    // --- Protocol ---

    [Fact]
    public void Emit_Protocol_HasProtocolAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MyDelegate" }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Protocol]", result);
        Assert.Contains("[BaseType(typeof(NSObject))]", result);
        Assert.Contains("partial interface MyDelegate", result);
    }

    [Fact]
    public void Emit_ProtocolRequiredMethod_HasAbstractAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "didFinish",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    IsOptional = false
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Abstract]", result);
        Assert.Contains("[Export(\"didFinish\")]", result);
    }

    [Fact]
    public void Emit_ProtocolOptionalMethod_NoAbstractAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "willStart",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    IsOptional = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[Abstract]", result);
        Assert.Contains("[Export(\"willStart\")]", result);
    }

    [Fact]
    public void Emit_ProtocolWithInheritedProtocols_EmitsInheritanceList()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                InheritedProtocolNames = ["NSCoding", "NSCopying"]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface MyDelegate : INSCoding, INSCopying", result);
    }

    [Fact]
    public void Emit_OwnProtocolReference_DeclarationAndConformanceBare_MemberTypeUsesInterface()
    {
        // B2: positional protocol spelling.
        //  * DECLARATION (`partial interface Foo`) and inheritance/conformance lists use the BARE
        //    name for an own protocol — bgen synthesizes `IFoo` from the bare `[Protocol] interface
        //    Foo` and converts the bare conformance to `: IFoo` in its output.
        //  * MEMBER types (parameter/return/property) use the INTERFACE `IFoo`, so bgen binds them
        //    to the protocol interface; a bare member reference makes bgen pick the generated Model
        //    class and a conforming subclass throws InvalidCastException at runtime.
        //  * An empty `interface IFoo {}` forward declaration is emitted per own protocol so those
        //    `IFoo` member references resolve in the plain-csc api-definition contract compile.
        // A protocol from the platform SDK keeps its `I` prefix everywhere — its interface already
        // ships in the platform assembly. The pre-prefixed declaration bug produced `IIFoo`.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl { Name = "MLNAnnotation" },
                new ObjCProtocolDecl
                {
                    Name = "MLNFeature",
                    // own (MLNAnnotation) + SDK (NSCopying) mixed in one inheritance list
                    InheritedProtocolNames = ["MLNAnnotation", "NSCopying"]
                }
            ],
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MLNShape",
                    // own (MLNFeature) + SDK (NSCoding) mixed in one conformance list
                    ProtocolNames = ["MLNFeature", "NSCoding"],
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "primaryFeature",
                            // member typed id<MLNAnnotation> — own protocol, must map to interface
                            Type = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["MLNAnnotation"] },
                            IsReadonly = true,
                            GetterSelector = "primaryFeature"
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);

        // Empty forward declarations for each own protocol's interface.
        Assert.Contains("interface IMLNAnnotation { }", result);
        Assert.Contains("interface IMLNFeature { }", result);
        // Protocol declaration is bare (NOT pre-prefixed to `interface IMLNAnnotation` as [Protocol]).
        Assert.Contains("partial interface MLNAnnotation", result);
        Assert.DoesNotContain("partial interface IMLN", result);
        // Protocol inheritance list: own bare, SDK I-prefixed.
        Assert.Contains("partial interface MLNFeature : MLNAnnotation, INSCopying", result);
        // Class conformance list: own bare, SDK I-prefixed.
        Assert.Contains("partial interface MLNShape : MLNFeature, INSCoding", result);
        // Member type id<MLNAnnotation> → the own-protocol INTERFACE.
        Assert.Contains("IMLNAnnotation PrimaryFeature { get; }", result);
        // The double-I bug shapes must never appear.
        Assert.DoesNotContain("IIMLNFeature", result);
        Assert.DoesNotContain("IIMLNAnnotation", result);
    }

    // --- Class/protocol name clash (Class 1: duplicate-emission de-dup) ---

    [Fact]
    public void Emit_ClassAndProtocolSameName_ProtocolRenamed_ClassKeepsBareName()
    {
        // When a name exists as BOTH a class and a protocol, bgen previously emitted two
        // `partial interface Foo` blocks — one for the class (with its real [BaseType]) and one for
        // the protocol — colliding on [BaseType] (CS0579), members (CS0102/CS0111), and the class's
        // own conformance listing `Foo` in `Foo`'s inheritance list (CS0529 self-cycle). The class
        // keeps the bare name; the protocol's managed interface is renamed `FooProtocol` with
        // `[Protocol(Name = "Foo")]` (the dotnet/macios convention), so both entities survive.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "Bridge",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "openURL:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        IsOptional = false,
                        Parameters = [new ObjCParameterDecl
                        {
                            Name = "url",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                        }]
                    }]
                }
            ],
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "Bridge",
                    SuperclassName = "NSObject",
                    // class conforms to the same-named protocol AND to an SDK protocol
                    ProtocolNames = ["Bridge", "NSCopying"],
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "scheme",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                            IsReadonly = true,
                            GetterSelector = "scheme"
                        },
                        // a member typed id<Bridge> — must resolve to the renamed protocol interface
                        new ObjCPropertyDecl
                        {
                            Name = "peer",
                            Type = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["Bridge"] },
                            IsReadonly = true,
                            GetterSelector = "peer"
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);

        // Protocol renamed to FooProtocol with the Name= form that preserves native registration.
        Assert.Contains("[Protocol(Name = \"Bridge\")]", result);
        Assert.Contains("partial interface BridgeProtocol", result);
        // Renamed forward declaration.
        Assert.Contains("interface IBridgeProtocol { }", result);
        // Exactly ONE bare class declaration `partial interface Bridge ` (trailing space excludes
        // the `BridgeProtocol` decl). The double-emission bug produced a second one.
        var classDeclCount = result.Split("partial interface Bridge ").Length - 1;
        Assert.Equal(1, classDeclCount);
        // Class keeps the bare name and conforms to the RENAMED protocol (own bare, SDK I-prefixed).
        Assert.Contains("partial interface Bridge : BridgeProtocol, INSCopying", result);
        // No self-cycle: the class never lists its own bare name as a conformance token (boundary-
        // aware so the legitimate `BridgeProtocol` prefix doesn't false-match).
        Assert.DoesNotContain(": Bridge,", result);
        Assert.DoesNotContain(": Bridge\n", result);
        Assert.DoesNotContain(", Bridge\n", result);
        Assert.DoesNotContain(", Bridge,", result);
        // Member typed id<Bridge> resolves to the renamed protocol interface.
        Assert.Contains("IBridgeProtocol Peer { get; }", result);
        // The double-I bug shape must never appear.
        Assert.DoesNotContain("IIBridge", result);
    }

    [Fact]
    public void Emit_ClassAndModelProtocolSameName_ProtocolRenamedCarriesModel()
    {
        // Same clash, but the protocol is a delegate/data-source protocol → it carries [Model].
        // The rename must compose with [Model]: `[Protocol(Name = "Foo"), Model]` on the renamed
        // `partial interface FooProtocol`, so the generated Model class becomes FooProtocol, not Foo.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "Loader",
                    IsDelegateProtocol = true,
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "didLoad",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        IsOptional = true
                    }]
                }
            ],
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "Loader",
                    SuperclassName = "NSObject",
                    ProtocolNames = ["Loader"]
                }
            ]
        };

        var result = EmitAndRead(module);

        // [Model] composes with the Name= rename.
        Assert.Contains("[Protocol(Name = \"Loader\"), Model]", result);
        Assert.Contains("partial interface LoaderProtocol", result);
        // Class keeps the bare name and conforms to the renamed protocol.
        Assert.Contains("partial interface Loader : LoaderProtocol", result);
        // Exactly one bare class declaration.
        var classDeclCount = result.Split("partial interface Loader ").Length - 1;
        Assert.Equal(1, classDeclCount);
    }

    [Fact]
    public void Emit_ProtocolWithoutClassClash_KeepsBareNameNoRename()
    {
        // Gating proof: a protocol whose name does NOT also exist as a class is unaffected — it keeps
        // the bare `[Protocol]` form and the bare `partial interface Foo` declaration (no Protocol
        // suffix, no Name= argument).
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "Standalone" }],
            Classes = [new ObjCClassDecl { Name = "Other", ProtocolNames = ["Standalone"] }]
        };

        var result = EmitAndRead(module);

        Assert.Contains("[Protocol]", result);
        Assert.DoesNotContain("[Protocol(Name", result);
        Assert.Contains("partial interface Standalone", result);
        Assert.DoesNotContain("StandaloneProtocol", result);
        // Class conformance uses the bare own-protocol name.
        Assert.Contains("partial interface Other : Standalone", result);
    }

    [Fact]
    public void Emit_ProtocolInheritingNSObject_FiltersOutINSObject()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                InheritedProtocolNames = ["NSObject", "NSCoding"]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface MyDelegate : INSCoding", result);
        Assert.DoesNotContain("INSObject", result);
    }

    [Fact]
    public void Emit_ProtocolInheritingOnlyNSObject_NoInheritanceList()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                InheritedProtocolNames = ["NSObject"]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface MyDelegate\n", result);
        Assert.DoesNotContain("INSObject", result);
    }

    [Fact]
    public void Emit_ProtocolRequiredProperty_HasAbstract()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "title",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                    IsOptional = false,
                    GetterSelector = "title"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Abstract]", result);
        Assert.Contains("string Title { get; }", result);
    }

    // --- Availability (Finding 22, recovery option a2) ---

    [Fact]
    public void Emit_NoRecoveredAvailability_EmitsNoAvailabilityAttributes()
    {
        // When no availability was recovered from header source (the common case — clang's JSON
        // AvailabilityAttr carries no platform data, and the source offset yielded nothing), the
        // API-definition emitter emits NO availability attributes for any decl kind. This guards
        // the "annotate only what we actually recovered" contract and pins that the dead bgen-era
        // [Introduced]/[Deprecated] names are never emitted.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "doStuff",
                    IsInstanceMethod = true,
                    ReturnType = new ObjCTypeRef { Name = "void" },
                }],
                Properties = [new ObjCPropertyDecl
                {
                    Name = "title",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                }],
            }],
            Protocols = [new ObjCProtocolDecl { Name = "MyDelegate" }],
        };

        var result = EmitAndRead(module);

        // The decls themselves still emit...
        Assert.Contains("partial interface MyClass", result);
        Assert.Contains("partial interface MyDelegate", result);
        // ...with no availability attributes of any kind.
        Assert.DoesNotContain("[Introduced(", result);
        Assert.DoesNotContain("[Deprecated(", result);
        Assert.DoesNotContain("[Obsoleted(", result);
        Assert.DoesNotContain("SupportedOSPlatform", result);
        Assert.DoesNotContain("ObsoletedOSPlatform", result);
        Assert.DoesNotContain("UnsupportedOSPlatform", result);
    }

    [Fact]
    public void Emit_RecoveredAvailability_OnClassAndMethodAndProperty()
    {
        // With availability recovered (Finding 22 a2), the emitter writes the same
        // [SupportedOSPlatform]/[ObsoletedOSPlatform] shape the Swift @available path uses.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Availability = [new ObjCAvailability { Platform = "ios", IntroducedVersion = "15.0" }],
                Methods = [new ObjCMethodDecl
                {
                    Selector = "doStuff",
                    IsInstanceMethod = true,
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    Availability = [new ObjCAvailability
                    {
                        Platform = "ios",
                        IntroducedVersion = "13.0",
                        DeprecatedVersion = "16.0",
                        Message = "use somethingElse"
                    }],
                }],
                Properties = [new ObjCPropertyDecl
                {
                    Name = "title",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                    Availability = [new ObjCAvailability { Platform = "macos", IntroducedVersion = "12.0" }],
                }],
            }],
        };

        var result = EmitAndRead(module);

        Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"ios15.0\")]", result);
        Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"ios13.0\")]", result);
        Assert.Contains("[global::System.Runtime.Versioning.ObsoletedOSPlatform(\"ios16.0\", \"use somethingElse\")]", result);
        Assert.Contains("[global::System.Runtime.Versioning.SupportedOSPlatform(\"macos12.0\")]", result);
    }

    [Fact]
    public void Emit_RecoveredUnavailable_OnProtocol_EmitsUnsupportedOSPlatform()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                Availability = [new ObjCAvailability { Platform = "tvos", IsUnavailable = true }],
            }],
        };

        var result = EmitAndRead(module);

        Assert.Contains("partial interface MyDelegate", result);
        Assert.Contains("[global::System.Runtime.Versioning.UnsupportedOSPlatform(\"tvos\")]", result);
    }

    // --- Instancetype ---

    [Fact]
    public void Emit_InstancetypeInClass_MapsToClassName()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "sharedInstance",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = false
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("MyClass SharedInstance();", result);
    }

    [Fact]
    public void Emit_InstancetypeInProtocol_MapsToNSObject()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyProto",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "copy",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    IsOptional = false
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("NSObject Copy();", result);
    }

    // --- C# keyword escaping ---

    [Fact]
    public void Emit_ParamNameIsCSharpKeyword_EscapedWithAt()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "indexOfObject:",
                    ReturnType = new ObjCTypeRef { Name = "NSUInteger" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "object",
                        Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true }
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("NSObject @object", result);
    }

    [Fact]
    public void Emit_ParamNameEvent_EscapedWithAt()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "didReceiveEvent:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    IsOptional = false,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "event",
                        Type = new ObjCTypeRef { Name = "NSData", IsPointer = true }
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("NSData @event", result);
    }

    [Theory]
    [InlineData("object", "@object")]
    [InlineData("event", "@event")]
    [InlineData("class", "@class")]
    [InlineData("string", "@string")]
    [InlineData("delegate", "@delegate")]
    [InlineData("normalName", "normalName")]
    [InlineData("myObject", "myObject")]
    public void EscapeCSharpKeyword_EscapesCorrectly(string input, string expected)
    {
        Assert.Equal(expected, ApiDefinitionEmitter.EscapeCSharpKeyword(input));
    }

    // --- Selector to method name ---

    [Theory]
    [InlineData("doSomething", "DoSomething")]
    [InlineData("fooWithBar:baz:", "FooWithBar")]
    [InlineData("count", "Count")]
    [InlineData("setName:", "SetName")]
    [InlineData("a", "A")]
    public void SelectorToMethodName_ConvertsCorrectly(string selector, string expected)
    {
        Assert.Equal(expected, ApiDefinitionEmitter.SelectorToMethodName(selector));
    }

    // --- Empty module ---

    [Fact]
    public void Emit_EmptyModule_ProducesMinimalValidOutput()
    {
        var module = new ObjCModule { ModuleName = "Empty" };

        var result = EmitAndRead(module);
        Assert.Contains("using Foundation;", result);
        Assert.Contains("using CoreFoundation;", result);
        Assert.Contains("namespace TestNamespace", result);
        Assert.Contains("{", result);
        Assert.Contains("}", result);
        Assert.DoesNotContain("partial interface", result);
    }

    // --- Ordering ---

    [Fact]
    public void Emit_ProtocolsBeforeClasses()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MyProto" }],
            Classes = [new ObjCClassDecl { Name = "MyClass" }]
        };

        var result = EmitAndRead(module);
        var protoIndex = result.IndexOf("IMyProto", StringComparison.Ordinal);
        var classIndex = result.IndexOf("partial interface MyClass", StringComparison.Ordinal);
        Assert.True(protoIndex < classIndex, "Protocols should be emitted before classes");
    }

    // --- Namespace ---

    [Fact]
    public void Emit_UsesResolvedNamespace()
    {
        var module = new ObjCModule { ModuleName = "Test" };
        var result = EmitAndRead(module, "MyCompany.iOS.Bindings");
        Assert.Contains("namespace MyCompany.iOS.Bindings", result);
    }

    // --- Duplicate constructor disambiguation ---

    [Fact]
    public void Emit_DuplicateConstructors_SecondBecomesNamedInit()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "Printer",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "initWithWifiIPAddress:",
                        ReturnType = new ObjCTypeRef { Name = "instancetype" },
                        IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl
                        {
                            Name = "address",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                        }]
                    },
                    new ObjCMethodDecl
                    {
                        Selector = "initWithBLELocalName:",
                        ReturnType = new ObjCTypeRef { Name = "instancetype" },
                        IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl
                        {
                            Name = "name",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                        }]
                    }
                ]
            }]
        };

        var result = EmitAndRead(module);
        // First init is a Constructor
        Assert.Contains("NativeHandle Constructor(string address);", result);
        // Second init becomes a named method (not another Constructor)
        Assert.Contains("[Export(\"initWithBLELocalName:\")]", result);
        Assert.Contains("InitWithBLELocalName(string name);", result);
        // Should NOT have two Constructor(string) — that would be a compile error
        var constructorCount = result.Split("NativeHandle Constructor(").Length - 1;
        Assert.Equal(1, constructorCount);
    }

    [Fact]
    public void Emit_UniqueConstructors_DifferentParamTypes_BothAreConstructors()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "initWithName:",
                        ReturnType = new ObjCTypeRef { Name = "instancetype" },
                        IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl
                        {
                            Name = "name",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                        }]
                    },
                    new ObjCMethodDecl
                    {
                        Selector = "initWithCount:",
                        ReturnType = new ObjCTypeRef { Name = "instancetype" },
                        IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl
                        {
                            Name = "count",
                            Type = new ObjCTypeRef { Name = "NSInteger" }
                        }]
                    }
                ]
            }]
        };

        var result = EmitAndRead(module);
        // Both should be constructors since they have different param types
        Assert.Contains("NativeHandle Constructor(string name);", result);
        Assert.Contains("NativeHandle Constructor(nint count);", result);
    }

    // --- DisableDefaultCtor ---

    [Fact]
    public void Emit_ClassWithParameterlessInit_HasDisableDefaultCtor()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "init",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[DisableDefaultCtor]", result);
    }

    [Fact]
    public void Emit_ClassWithNoInit_NoDisableDefaultCtor()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "doSomething",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[DisableDefaultCtor]", result);
    }

    // --- Protocol init NOT treated as constructor ---

    [Fact]
    public void Emit_ProtocolInitSelector_NotEmittedAsConstructor()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyFactory",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "initWithConfig:",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    IsOptional = false,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "config",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        // Should be a regular method, NOT a constructor
        Assert.DoesNotContain("Constructor", result);
        Assert.Contains("InitWithConfig(string config);", result);
    }

    // --- Method signature dedup ---

    [Fact]
    public void Emit_DuplicateMethodSignatures_SecondUsesFullSelectorName()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyDict",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "setObject:forKey:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "obj", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                            new ObjCParameterDecl { Name = "key", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                        ]
                    },
                    new ObjCMethodDecl
                    {
                        Selector = "setObject:forKeyedSubscript:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "obj", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                            new ObjCParameterDecl { Name = "key", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                        ]
                    }
                ]
            }]
        };

        var result = EmitAndRead(module);
        // First method gets short name
        Assert.Contains("void SetObject(NSObject obj, NSObject key);", result);
        // Second method with same signature gets full selector name
        Assert.Contains("void SetObjectForKeyedSubscript(NSObject obj, NSObject key);", result);
    }

    [Fact]
    public void Emit_TripleDuplicateMethodSignatures_AllGetUniqueNames()
    {
        // Three methods all resolving to the same short name and param types.
        // The second gets renamed via SelectorToFullMethodName.
        // The third must not collide with the renamed second.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyDict",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "setObject:forKey:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "obj", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                            new ObjCParameterDecl { Name = "key", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                        ]
                    },
                    new ObjCMethodDecl
                    {
                        Selector = "setObject:forKeyedSubscript:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "obj", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                            new ObjCParameterDecl { Name = "key", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                        ]
                    },
                    // Third method: short name "SetObject" collides with first,
                    // full name "SetObjectForSomething" is unique
                    new ObjCMethodDecl
                    {
                        Selector = "setObject:forSomething:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "obj", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                            new ObjCParameterDecl { Name = "key", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                        ]
                    }
                ]
            }]
        };

        var result = EmitAndRead(module);
        // First keeps short name
        Assert.Contains("void SetObject(NSObject obj, NSObject key);", result);
        // Second gets full selector name
        Assert.Contains("void SetObjectForKeyedSubscript(NSObject obj, NSObject key);", result);
        // Third gets full selector name (not "SetObject" which would collide)
        Assert.Contains("void SetObjectForSomething(NSObject obj, NSObject key);", result);
        // No duplicate declarations
        var setObjectCount = result.Split("void SetObject(").Length - 1;
        Assert.Equal(1, setObjectCount);
    }

    [Fact]
    public void Emit_MethodDedup_FullNameCollidesWithExisting_AppendsSuffix()
    {
        // setObjectForKeyedSubscript: (short name "SetObjectForKeyedSubscript") already exists.
        // Then setObject:forKeyedSubscript: (short name "SetObject") collides with another method,
        // so it gets renamed to "SetObjectForKeyedSubscript" — but that already exists too.
        // It should get a numeric suffix.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyDict",
                Methods =
                [
                    // First method: short name "SetObjectForKeyedSubscript" (no colon in selector before params)
                    new ObjCMethodDecl
                    {
                        Selector = "setObjectForKeyedSubscript:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "obj", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                        ]
                    },
                    // Second method: short name "SetObject" — no collision yet
                    new ObjCMethodDecl
                    {
                        Selector = "setObject:forKey:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "obj", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                        ]
                    },
                    // Third method: short name "SetObject" collides with second →
                    // full name "SetObjectForKeyedSubscript" collides with first →
                    // must get numeric suffix
                    new ObjCMethodDecl
                    {
                        Selector = "setObject:forKeyedSubscript:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "obj", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } },
                        ]
                    }
                ]
            }]
        };

        var result = EmitAndRead(module);
        // First: short name stands
        Assert.Contains("void SetObjectForKeyedSubscript(NSObject obj);", result);
        // Second: short name stands (different from first)
        Assert.Contains("void SetObject(NSObject obj);", result);
        // Third: full name collides with first, gets numeric suffix
        Assert.Contains("void SetObjectForKeyedSubscript2(NSObject obj);", result);
    }

    // --- SelectorToFullMethodName ---

    [Theory]
    [InlineData("setObject:forKey:", "SetObjectForKey")]
    [InlineData("doSomething", "DoSomething")]
    [InlineData("initWithName:count:", "InitWithNameCount")]
    public void SelectorToFullMethodName_ConvertsCorrectly(string selector, string expected)
    {
        Assert.Equal(expected, ApiDefinitionEmitter.SelectorToFullMethodName(selector));
    }

    // --- NSFastEnumeration filtering ---

    [Fact]
    public void Emit_ProtocolInheritingNSFastEnumeration_FiltersItOut()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyCollection",
                InheritedProtocolNames = ["NSFastEnumeration", "NSCoding"]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface MyCollection : INSCoding", result);
        Assert.DoesNotContain("NSFastEnumeration", result);
    }

    [Fact]
    public void Emit_ClassAdoptingNSFastEnumeration_FiltersItOut()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyList",
                ProtocolNames = ["NSFastEnumeration", "NSCoding"]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface MyList : INSCoding", result);
        Assert.DoesNotContain("NSFastEnumeration", result);
    }

    // --- Generic type param scoping ---

    [Fact]
    public void Emit_GenericTypeParam_ScopedToDeclaringClass()
    {
        // Class A declares generic param "KeyType". Class B uses "KeyType" as a real type name.
        // The generic param should only be resolved to NSObject within class A, not class B.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            // KeyType is used both as a generic param (in GenericCollection) AND a
            // real concrete type (in KeyTypeConsumer). Declare it as an SDK type so
            // the resolvability filter doesn't drop the KeyTypeConsumer method.
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["KeyType"] = "" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "GenericCollection",
                    GenericTypeParamNames = ["KeyType"],
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "objectForKey:",
                        ReturnType = new ObjCTypeRef { Name = "NSObject", IsPointer = true },
                        IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl
                        {
                            Name = "key",
                            Type = new ObjCTypeRef { Name = "KeyType" }
                        }]
                    }]
                },
                new ObjCClassDecl
                {
                    Name = "KeyTypeConsumer",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "processKey:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl
                        {
                            Name = "key",
                            Type = new ObjCTypeRef { Name = "KeyType" }
                        }]
                    }]
                }
            ]
        };

        var result = EmitAndRead(module);

        // In GenericCollection, KeyType is a generic param → NSObject
        Assert.Contains("NSObject key", result.Split("partial interface GenericCollection")[1]
            .Split("partial interface KeyTypeConsumer")[0]);

        // In KeyTypeConsumer, KeyType is a real type → passthrough as-is
        Assert.Contains("KeyType key", result.Split("partial interface KeyTypeConsumer")[1]);
    }

    // ──────────────────────────────────────────────
    // Category emission tests
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_Category_HasCategoryAndBaseTypeAttributes()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "Widget",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "doExtra",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true
                    }]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Category]", result);
        Assert.Contains("[BaseType(typeof(Widget))]", result);
        Assert.Contains("partial interface Widget_Extras", result);
    }

    [Fact]
    public void Emit_Category_MethodsHaveExport()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "Widget",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "doExtra:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl
                        {
                            Name = "value",
                            Type = new ObjCTypeRef { Name = "int" }
                        }]
                    }]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"doExtra:\")]", result);
        Assert.Contains("void DoExtra(int value);", result);
    }

    [Fact]
    public void Emit_Category_InstancePropertyOnly_ProjectedAsAccessorMethod()
    {
        // A category carrying only instance properties still has surface to bind: bgen generates a
        // [Category] interface as a static extension class, which cannot hold an instance PROPERTY
        // (CS0708) but does hold an instance METHOD (it becomes an extension method carrying the
        // receiver). The property is therefore projected as an accessor method instead of dropped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Info",
                    ClassName = "Widget",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "version",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        IsReadonly = true
                    }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        Assert.Contains("[Category]", result);
        Assert.Contains("partial interface Widget_Info", result);
        Assert.Contains("[Export(\"version\")]", result);
        Assert.Contains("string GetVersion();", result);
        // No instance property declaration survives (that shape is the CS0708).
        Assert.DoesNotContain("string Version {", result);
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.Reason == ObjCSkipReason.EmptyCategory);
    }

    [Theory]
    [InlineData(ObjCMemorySemantic.Copy, "ArgumentSemantic.Copy")]
    [InlineData(ObjCMemorySemantic.Assign, "ArgumentSemantic.Assign")]
    [InlineData(ObjCMemorySemantic.UnsafeUnretained, "ArgumentSemantic.Assign")]
    [InlineData(ObjCMemorySemantic.Weak, "ArgumentSemantic.Weak")]
    [InlineData(ObjCMemorySemantic.Strong, "ArgumentSemantic.Retain")]
    [InlineData(ObjCMemorySemantic.Retain, "ArgumentSemantic.Retain")]
    public void Emit_Category_InstancePropertyAccessors_CarryDeclaredArgumentSemantic(ObjCMemorySemantic semantic, string expected)
    {
        // The accessor pair IS the property, so it has to state the property's declared memory
        // semantic. Projecting it away leaves the two halves of one property describing a weaker
        // contract than the property they replace.
        var module = CategoryWithInstanceProperty(semantic, isReadonly: false);

        var result = EmitAndRead(module);

        Assert.Contains($"[Export(\"tint\", {expected})]", result);
        Assert.Contains($"[Export(\"setTint:\", {expected})]", result);
    }

    [Fact]
    public void Emit_Category_InstancePropertyAccessors_NoSemanticDeclared_ExportStaysBare()
    {
        var module = CategoryWithInstanceProperty(ObjCMemorySemantic.None, isReadonly: false);

        var result = EmitAndRead(module);

        Assert.Contains("[Export(\"tint\")]", result);
        Assert.Contains("[Export(\"setTint:\")]", result);
        Assert.DoesNotContain("ArgumentSemantic", result);
    }

    [Fact]
    public void Emit_Category_ReadonlyInstanceProperty_GetterAloneCarriesSemantic()
    {
        var module = CategoryWithInstanceProperty(ObjCMemorySemantic.Copy, isReadonly: true);

        var result = EmitAndRead(module);

        Assert.Contains("[Export(\"tint\", ArgumentSemantic.Copy)]", result);
        Assert.DoesNotContain("setTint:", result);
    }

    static ObjCModule CategoryWithInstanceProperty(ObjCMemorySemantic semantic, bool isReadonly) => new()
    {
        ModuleName = "Test",
        Categories =
        [
            new ObjCCategoryDecl
            {
                CategoryName = "Paint",
                ClassName = "Widget",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "tint",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = isReadonly,
                    MemorySemantic = semantic
                }]
            }
        ]
    };

    // ──────────────────────────────────────────────
    // Receiver-free overloads for category class members
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_CategoryClassMethod_GetsReceiverFreeForwarder()
    {
        // bgen prepends a receiver to EVERY member of the static extension class it compiles a
        // [Category] into, [Static] included — so without this the only way to call a category's
        // class method is to hand it an instance it never sends to.
        var (apiDefinition, statics, _) = EmitApiDefinitionWithCategoryStatics(CategoryWithClassFactory());

        Assert.Contains("[Static]", apiDefinition);
        Assert.NotNull(statics);
        Assert.Contains("public static partial class Widget_Boxing", statics);
        // The forwarder takes the declared parameters and nothing else: a `this` receiver here
        // would just be the extension method bgen already generated.
        Assert.Contains("public static string BoxSpan(double span)", statics);
        Assert.DoesNotContain("this Widget", statics);
        // It reaches the generated member by supplying the receiver the caller no longer has to.
        Assert.Contains("BoxSpan((Widget)null!, span)", statics);
        // Managed sugar only — a second [Export] of the same selector is a duplicate registration.
        Assert.DoesNotContain("[Export", statics);
    }

    [Fact]
    public void Emit_CategoryClassMethod_ForwarderIsNotABgenInput()
    {
        // The forwarder calls a member bgen has not generated yet when it compiles its own inputs,
        // so it must land in the companion file rather than the ApiDefinition.
        var (apiDefinition, statics, _) = EmitApiDefinitionWithCategoryStatics(CategoryWithClassFactory());

        Assert.DoesNotContain("public static partial class", apiDefinition);
        Assert.Contains("namespace TestNamespace", statics);
    }

    [Fact]
    public void Emit_CategoryInstanceMethodsOnly_NoStaticsFileWritten()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "Widget",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "doExtra",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true
                    }]
                }
            ]
        };

        var (_, statics, _) = EmitApiDefinitionWithCategoryStatics(module);

        Assert.Null(statics);
    }

    [Fact]
    public void Emit_NoForwarders_ClearsStaleStaticsFile()
    {
        // The SDK generates into an incremental intermediate directory, so a file left behind by a
        // previous generate would keep compiling against members this one no longer declares.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl { Name = "Widget" }]
        };

        var (_, statics, _) = EmitApiDefinitionWithCategoryStatics(module, seedStaleCategoryStatics: true);

        Assert.Null(statics);
    }

    [Fact]
    public void Emit_CategoryVariadicClassMethod_GetsNoForwarder()
    {
        // A variadic member is rendered [Internal] on purpose: it is the raw half of the binding,
        // not the surface consumers call. A public forwarder would republish it.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Logging",
                    ClassName = "Widget",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "logFormat:",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = false,
                        IsVariadic = true,
                        Parameters = [new ObjCParameterDecl
                        {
                            Name = "format",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                        }]
                    }]
                }
            ]
        };

        var (apiDefinition, statics, _) = EmitApiDefinitionWithCategoryStatics(module);

        Assert.Contains("[Internal]", apiDefinition);
        Assert.Null(statics);
    }

    [Fact]
    public void Emit_CategoryClassMethod_ColludingWithGeneratedInstanceSignature_SkippedWithDiagnostic()
    {
        // The receiver an instance member is generated WITH is the parameter the class member's
        // forwarder declares, so the two can land on one signature. Emitting both is CS0111 in the
        // consumer's build, and the class member is still reachable through its own overload.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl { Name = "Widget" }],
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Pairing",
                    ClassName = "Widget",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "poke",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "poke:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = false,
                            Parameters = [new ObjCParameterDecl
                            {
                                Name = "other",
                                Type = new ObjCTypeRef { Name = "Widget", IsPointer = true }
                            }]
                        }
                    ]
                }
            ]
        };

        var (apiDefinition, statics, diagnostics) = EmitApiDefinitionWithCategoryStatics(module);

        // Both members still bind — only the extra overload is withheld.
        Assert.Contains("void Poke();", apiDefinition);
        Assert.Contains("void Poke(Widget other);", apiDefinition);
        Assert.Null(statics);
        Assert.Contains(diagnostics.SkippedSymbols, s => s.Reason == ObjCSkipReason.DuplicateSignature && s.SymbolName == "poke:");
    }

    [Fact]
    public void Emit_CategoryClassMethod_OutParameterIsNotConfusedWithAValueParameter()
    {
        // `out NSError` and `NSError` are different C# signatures, so a class method taking the
        // former does NOT collide with an instance method whose generated receiver is the latter.
        // A collision check keyed on the bare mapped type calls them the same and withholds a
        // forwarder that was free all along.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Loading",
                    ClassName = "NSError",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "reload",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "reload:",
                            ReturnType = new ObjCTypeRef { Name = "BOOL" },
                            IsInstanceMethod = false,
                            Parameters = [new ObjCParameterDecl
                            {
                                Name = "error",
                                Type = new ObjCTypeRef
                                {
                                    Name = "NSError",
                                    IsPointer = true,
                                    PointeeType = new ObjCTypeRef { Name = "NSError", IsPointer = true }
                                }
                            }]
                        }
                    ]
                }
            ]
        };

        var (_, statics, diagnostics) = EmitApiDefinitionWithCategoryStatics(module);

        Assert.NotNull(statics);
        // The modifier has to survive into both the declaration and the forwarded call.
        Assert.Contains("out NSError error", statics);
        Assert.Contains("out error)", statics);
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.Reason == ObjCSkipReason.DuplicateSignature);
    }

    [Fact]
    public void Emit_CategoryClassMethod_ForwarderRepeatsMemberAvailability()
    {
        // Attributes do not transfer between overloads: without re-stating them the platform
        // analyzer sees an unconditionally-available API and stops warning about calling a newer
        // selector from an older deployment target.
        var module = CategoryWithClassFactory(
            [new ObjCAvailability { Platform = "ios", IntroducedVersion = "17.0" }]);

        var (_, statics, _) = EmitApiDefinitionWithCategoryStatics(module);

        Assert.NotNull(statics);
        Assert.Contains("SupportedOSPlatform", statics);
        Assert.Contains("17.0", statics);
    }

    static ObjCModule CategoryWithClassFactory(List<ObjCAvailability> availability = null!) => new()
    {
        ModuleName = "Test",
        Categories =
        [
            new ObjCCategoryDecl
            {
                CategoryName = "Boxing",
                ClassName = "Widget",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "boxSpan:",
                    ReturnType = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsInstanceMethod = false,
                    Parameters = [new ObjCParameterDecl
                    {
                        Name = "span",
                        Type = new ObjCTypeRef { Name = "double" }
                    }],
                    Availability = availability ?? []
                }]
            }
        ]
    };

    [Fact]
    public void Emit_Category_ClassPropertyEmitted()
    {
        // [Static] properties (class-level) ARE emitted in categories since
        // they are valid members of static classes.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Info",
                    ClassName = "Widget",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "version",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        IsReadonly = true,
                        IsClass = true // class property → valid in static class
                    }],
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "doSomething",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true
                    }]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Category]", result);
        Assert.Contains("partial interface Widget_Info", result);
        Assert.Contains("[Static]", result);
        Assert.Contains("string Version { get; }", result);
    }

    [Fact]
    public void Emit_Category_InitMethodsSkipped()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Creation",
                    ClassName = "Widget",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "initWithName:",
                            ReturnType = new ObjCTypeRef { Name = "instancetype" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl
                            {
                                Name = "name",
                                Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                            }]
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "doSomething",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("Constructor", result);
        Assert.DoesNotContain("initWithName", result);
        Assert.Contains("DoSomething", result);
    }

    [Fact]
    public void Emit_Category_UnnamedCategory_UsesExtensionsSuffix()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "",
                    ClassName = "Widget",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "doStuff",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true
                    }]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface Widget_Extensions", result);
    }

    [Fact]
    public void Emit_Category_GenericTypeParams_ResolvedToNSObject()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extended",
                    ClassName = "NSArray",
                    GenericTypeParamNames = ["ObjectType"],
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "firstItem",
                        ReturnType = new ObjCTypeRef { Name = "ObjectType" },
                        IsInstanceMethod = true
                    }]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("NSObject FirstItem", result);
    }

    [Fact]
    public void Emit_Category_WithProtocolConformance_StripsProtocols()
    {
        // MAUI bgen generates [Category] interfaces as static classes, which
        // cannot implement interfaces (CS0714). Protocol conformance is stripped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Coding",
                    ClassName = "Widget",
                    ProtocolNames = ["NSCoding", "NSSecureCoding"],
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "encode",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true
                    }]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Category]", result);
        Assert.Contains("partial interface Widget_Coding", result);
        // Protocol conformance stripped (CS0714)
        Assert.DoesNotContain("INSCoding", result);
        Assert.DoesNotContain("INSSecureCoding", result);
        // Method is still emitted
        Assert.Contains("[Export(\"encode\")]", result);
    }

    [Fact]
    public void Emit_Category_DuplicateMethodSignatures_Renamed()
    {
        // Two methods with the same short C# name and identical param types should not produce
        // duplicate signatures — the second should fall back to full selector naming.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "Widget",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doThing:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl
                            {
                                Name = "value",
                                Type = new ObjCTypeRef { Name = "int" }
                            }]
                        },
                        new ObjCMethodDecl
                        {
                            // Same short name "doThing" and same single int param — collision
                            Selector = "doThing:fromSource:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl
                            {
                                Name = "source",
                                Type = new ObjCTypeRef { Name = "int" }
                            }]
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        // First "DoThing" keeps its short name
        Assert.Contains("void DoThing(int value);", result);
        // Second collides on DoThing(int) → renamed to full selector form
        Assert.Contains("DoThingFromSource", result);
    }

    [Fact]
    public void Emit_PureObjC_CategoriesNotDoubleEmitted()
    {
        // Module with populated Categories but no mixed-mode filtering applied.
        // In the pipeline, categories are cleared for pure ObjC before emission.
        // Here we test that when Categories is empty, no [Category] appears.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "Widget",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "doExtra",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    IsFromCategory = true,
                    CategoryName = "Extras"
                }]
            }],
            Categories = [] // Cleared for pure ObjC
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[Category]", result);
        // The method is still emitted inline on the class
        Assert.Contains("[Export(\"doExtra\")]", result);
    }

    [Theory]
    [InlineData("Widget", "Extras", "Widget_Extras")]
    [InlineData("Widget", "", "Widget_Extensions")]
    [InlineData("NSArray", "NSExtendedArray", "NSArray_NSExtendedArray")]
    public void GenerateCategoryInterfaceName_ReturnsCorrectName(string className, string categoryName, string expected)
    {
        Assert.Equal(expected, ApiDefinitionEmitter.GenerateCategoryInterfaceName(className, categoryName));
    }

    [Fact]
    public void Emit_ProtocolMethodDedup_RenamesCollisions()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "ManagerDelegate",
                Methods =
                [
                    new ObjCMethodDecl { Selector = "manager:didConnect:", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl { Name = "manager", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } }, new ObjCParameterDecl { Name = "peer", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } }] },
                    new ObjCMethodDecl { Selector = "manager:didDisconnect:", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl { Name = "manager", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } }, new ObjCParameterDecl { Name = "peer", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } }] },
                ]
            }]
        };

        var result = EmitAndRead(module);
        // First method keeps short name
        Assert.Contains("void Manager(NSObject manager, NSObject peer);", result);
        // Second collision renamed to full selector
        Assert.Contains("void ManagerDidDisconnect(NSObject manager, NSObject peer);", result);
    }

    [Fact]
    public void Emit_DuplicateProperty_SecondSkipped()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "Manager",
                Properties =
                [
                    new ObjCPropertyDecl { Name = "authorization", Type = new ObjCTypeRef { Name = "int" }, IsReadonly = true },
                    new ObjCPropertyDecl { Name = "authorization", Type = new ObjCTypeRef { Name = "int" }, IsReadonly = true },
                ]
            }]
        };

        var result = EmitAndRead(module);
        // Only one property: Export contains selector name, property line has PascalCase
        Assert.Single(result.Split('\n'), l => l.Contains("{ get; }"));
    }

    [Fact]
    public void Emit_ClassMethodNameCollidesWithInstanceProperty_MethodRenamed_PropertyKept()
    {
        // A class method `isEnabled` and an instance property `isEnabled` share the C# name
        // `IsEnabled` but dispatch through distinct ObjC selectors (class vs instance method
        // lists). A property accessor's C# name is fixed by bgen, but a method can be renamed,
        // so the property wins the name and the method takes a numeric suffix — its [Export]
        // selector is preserved. Previously the property was silently dropped purely because
        // methods emit before properties.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "Manager",
                Methods = [new ObjCMethodDecl { Selector = "isEnabled", ReturnType = new ObjCTypeRef { Name = "BOOL" }, IsInstanceMethod = false }],
                Properties = [new ObjCPropertyDecl { Name = "isEnabled", Type = new ObjCTypeRef { Name = "BOOL" }, IsReadonly = true }]
            }]
        };

        var result = EmitAndRead(module);
        var lines = result.Split('\n').Where(l => l.Contains("IsEnabled")).ToList();
        // Method renamed to clear the property's name; its selector is preserved.
        Assert.Contains("[Export(\"isEnabled\")]", result);
        Assert.Contains(lines, l => l.Contains("bool IsEnabled2()"));
        // Property kept (previously dropped).
        Assert.Contains(lines, l => l.Contains("bool IsEnabled { get; }"));
    }

    [Fact]
    public void Emit_RenamedMethodCollidesWithProperty_MethodSuffixed_PropertyKept()
    {
        // When method dedup renames `manager:didDisconnect:` → `ManagerDidDisconnect`, and a
        // property is also named `managerDidDisconnect`, the property wins the C# name (its
        // accessor name is fixed) and the method takes a numeric suffix (`ManagerDidDisconnect2`).
        // Previously the property was dropped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "Widget",
                Methods =
                [
                    new ObjCMethodDecl { Selector = "manager:didConnect:", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl { Name = "m", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } }, new ObjCParameterDecl { Name = "p", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } }] },
                    new ObjCMethodDecl { Selector = "manager:didDisconnect:", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl { Name = "m", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } }, new ObjCParameterDecl { Name = "p", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true } }] },
                ],
                Properties = [new ObjCPropertyDecl { Name = "managerDidDisconnect", Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true }, IsReadonly = true }]
            }]
        };

        var result = EmitAndRead(module);
        // First method keeps its short name.
        Assert.Contains("void Manager(NSObject m, NSObject p);", result);
        // Second method renamed to the full selector, then suffixed to clear the property's name.
        Assert.Contains("void ManagerDidDisconnect2(NSObject m, NSObject p);", result);
        // Property with the colliding PascalCase name is kept (previously dropped).
        Assert.Contains("ManagerDidDisconnect { get; }", result);
    }

    [Fact]
    public void Emit_ProtocolMethodSharesPropertyGetterSelector_MethodDropped_PropertyKept()
    {
        // A protocol method `isReady` and a readonly property `isReady` resolve to the SAME ObjC
        // selector (`isReady`): they are the same method, so exporting both would SIGABRT the
        // registrar with a duplicate selector. The property wins (its getter covers the selector)
        // and the redundant standalone method is dropped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                Methods = [new ObjCMethodDecl { Selector = "isReady", ReturnType = new ObjCTypeRef { Name = "BOOL" }, IsInstanceMethod = true }],
                Properties = [new ObjCPropertyDecl { Name = "isReady", Type = new ObjCTypeRef { Name = "BOOL" }, IsReadonly = true }]
            }]
        };

        var result = EmitAndRead(module);
        // Property kept ...
        Assert.Contains("bool IsReady { get; }", result);
        // ... and the duplicate-selector method dropped (no standalone method form).
        Assert.DoesNotContain("bool IsReady();", result);
    }

    [Fact]
    public void Emit_CategoryMethodPropertyCollision_MethodDroppedAccessorKept()
    {
        // Category paths also need method→property collision tracking. The property's projected
        // getter exports `tintColor`, so the like-named method is the same selector — it drops in
        // favour of the property, matching the class and protocol paths.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Styling",
                    ClassName = "Widget",
                    Methods = [new ObjCMethodDecl { Selector = "tintColor", ReturnType = new ObjCTypeRef { Name = "UIColor", IsPointer = true }, IsInstanceMethod = true }],
                    Properties = [new ObjCPropertyDecl { Name = "tintColor", Type = new ObjCTypeRef { Name = "UIColor", IsPointer = true }, IsReadonly = true }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        // The selector is exported exactly once, by the projected accessor.
        Assert.Equal(1, CountOccurrences(result, "[Export(\"tintColor\")]"));
        Assert.Contains("UIColor GetTintColor();", result);
        Assert.DoesNotContain("UIColor TintColor();", result);
        // A category is a static extension class, so the property form never appears (CS0708).
        Assert.DoesNotContain(result.Split('\n'), l => l.Contains("TintColor { get; }"));
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolKind == "Method" && s.SymbolName == "tintColor");
        Assert.Equal(ObjCSkipReason.DuplicateSelector, skip.Reason);
    }

    // --- Fix: ApiDefinition.cs includes CoreAnimation using ---

    [Fact]
    public void Emit_IncludesUsingCoreAnimation()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl { Name = "MyView" }]
        };
        var result = EmitAndRead(module);
        Assert.Contains("using CoreAnimation;", result);
    }

    [Fact]
    public void EmitClass_NSURLProtocolNames_MappedToNetConvention()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyDownloader",
                    ProtocolNames = ["NSURLSessionTaskDelegate", "NSURLSessionDataDelegate"]
                }
            ]
        };
        var result = EmitAndRead(module);
        Assert.Contains("INSUrlSessionTaskDelegate", result);
        Assert.Contains("INSUrlSessionDataDelegate", result);
        Assert.DoesNotContain("INSURLSessionTaskDelegate", result);
    }

    [Fact]
    public void EmitProtocol_NSURLInheritedProtocol_MappedToNetConvention()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "MyDelegate",
                    InheritedProtocolNames = ["NSURLSessionDelegate"]
                }
            ]
        };
        var result = EmitAndRead(module);
        Assert.Contains("INSUrlSessionDelegate", result);
        Assert.DoesNotContain("INSURLSessionDelegate", result);
    }

    [Fact]
    public void EmitClass_NSSecureCoding_SkipsInitWithCoder()
    {
        // When a class conforms to NSSecureCoding, bgen auto-generates initWithCoder:.
        // Our explicit emission would cause CS0111 duplicate constructor.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyRequest",
                    ProtocolNames = ["NSCopying", "NSSecureCoding"],
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "initWithCoder:",
                            ReturnType = new ObjCTypeRef { Name = "instancetype" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "coder", Type = new ObjCTypeRef { Name = "NSCoder", IsPointer = true } }]
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "initWithName:",
                            ReturnType = new ObjCTypeRef { Name = "instancetype" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "name", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }]
                        }
                    ]
                }
            ]
        };
        var result = EmitAndRead(module);

        // initWithCoder: should be skipped — bgen handles it via INSSecureCoding
        Assert.DoesNotContain("initWithCoder:", result);
        // Other constructors should still be emitted
        Assert.Contains("initWithName:", result);
    }

    [Fact]
    public void EmitClass_NoNSCoding_KeepsInitWithCoder()
    {
        // When a class does NOT conform to NSCoding, initWithCoder: should be emitted normally.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyCustomClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "initWithCoder:",
                            ReturnType = new ObjCTypeRef { Name = "instancetype" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "coder", Type = new ObjCTypeRef { Name = "NSCoder", IsPointer = true } }]
                        }
                    ]
                }
            ]
        };
        var result = EmitAndRead(module);

        // No NSCoding conformance → initWithCoder: should be kept
        Assert.Contains("initWithCoder:", result);
    }

    // --- Source-aware ApiDefinition type filtering ---

    [Fact]
    public void Emit_ApiDefinitionFiltering_SkipsMethodWithUnresolvableParamType()
    {
        // When AppleSdkTypeNamespaces is populated, methods with types NOT in the known set
        // or Apple SDK should be skipped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["UIColor"] = "" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doGood:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "color", Type = new ObjCTypeRef { Name = "UIColor", IsPointer = true } }]
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "doBad:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "info", Type = new ObjCTypeRef { Name = "ThirdPartyType", IsPointer = true } }]
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("DoGood", result);
        Assert.DoesNotContain("DoBad", result);
    }

    [Fact]
    public void Emit_ApiDefinitionFiltering_SkipsMethodWithUnresolvableReturnType()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSData"] = "" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "fetchData",
                            ReturnType = new ObjCTypeRef { Name = "ExternalResult", IsPointer = true },
                            IsInstanceMethod = true,
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("FetchData", result);
    }

    [Fact]
    public void Emit_ApiDefinitionFiltering_SkipsPropertyWithUnresolvableType()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["UIView"] = "" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Properties =
                    [
                        new ObjCPropertyDecl { Name = "goodView", Type = new ObjCTypeRef { Name = "UIView", IsPointer = true } },
                        new ObjCPropertyDecl { Name = "badThing", Type = new ObjCTypeRef { Name = "ThirdPartyWidget", IsPointer = true } }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("GoodView", result);
        Assert.DoesNotContain("BadThing", result);
    }

    /// <summary>
    /// A member typed by a system struct spelled through a typedef over a differently-named record
    /// tag (<c>typedef struct _NSRange NSRange;</c>) survives emission. Before the registry claimed
    /// the public spelling, the typedef hop rewrote the member to <c>_NSRange</c> — a name no
    /// platform assembly declares — and the member was dropped as unresolvable.
    /// </summary>
    [Fact]
    public void Emit_MemberTypedBySystemStructOverRecordTag_Survives()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSError"] = "Foundation" },
            Typedefs =
            [
                new ObjCTypedefDecl { Name = "NSRange", UnderlyingType = new ObjCTypeRef { Name = "_NSRange" } }
            ],
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "coordinatesInRange:",
                            ReturnType = new ObjCTypeRef { Name = "NSRange" },
                            Parameters = [new ObjCParameterDecl { Name = "range", Type = new ObjCTypeRef { Name = "NSRange" } }],
                            IsInstanceMethod = true,
                        }
                    ]
                }
            ]
        };

        var (content, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        Assert.Contains("CoordinatesInRange", content);
        Assert.Contains("NSRange", content);
        Assert.DoesNotContain("_NSRange", content);
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.Reason == ObjCSkipReason.UnresolvableType);
    }

    /// <summary>
    /// Members typed by registered Apple SDK enums survive emission; one typed by an enum outside the
    /// vocabulary is still dropped AND the drop is recorded, so an unbound member never disappears
    /// without a persisted diagnostic.
    /// </summary>
    [Fact]
    public void Emit_MembersTypedBySystemEnums_Survive_UnknownEnumRecordsSkip()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSError"] = "Foundation" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Properties =
                    [
                        new ObjCPropertyDecl { Name = "interfaceStyle", Type = new ObjCTypeRef { Name = "UIUserInterfaceStyle" } }
                    ],
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "currentAuthorization",
                            ReturnType = new ObjCTypeRef { Name = "CLAuthorizationStatus" },
                            IsInstanceMethod = true,
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "vendorAuthorization",
                            ReturnType = new ObjCTypeRef { Name = "ZZVendorAuthorizationStatus" },
                            IsInstanceMethod = true,
                        }
                    ]
                }
            ]
        };

        var (content, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        Assert.Contains("CurrentAuthorization", content);
        Assert.Contains("CLAuthorizationStatus", content);
        Assert.Contains("InterfaceStyle", content);
        Assert.Contains("UIUserInterfaceStyle", content);
        Assert.DoesNotContain("VendorAuthorization", content);
        Assert.Contains(diagnostics.SkippedSymbols,
            s => s.SymbolName == "vendorAuthorization" && s.Reason == ObjCSkipReason.UnresolvableType);
    }

    [Fact]
    public void Emit_ApiDefinitionFiltering_AcceptsProtocolInterfacePrefix()
    {
        // A protocol-typed member binds to the protocol INTERFACE, `ISomeDelegate` — an `I` the
        // emitter synthesizes; it must resolve when the bare protocol name is an Apple SDK type.
        // The fixture models the real clang shape, `id<SomeDelegate>`: the leading `I` never
        // appears in the AST, and only a name the mapper itself synthesized may take the
        // strip-the-I retry (a vendor class literally named `ISomeDelegate` must not).
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["SomeDelegate"] = "" },
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "MyProtocol",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "getDelegate",
                            ReturnType = new ObjCTypeRef { Name = "id", ProtocolQualifications = { "SomeDelegate" } },
                            IsInstanceMethod = true,
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("GetDelegate", result);
    }

    [Fact]
    public void Emit_ApiDefinitionFiltering_FallbackHeuristicWhenAppleSdkTypesNull()
    {
        // When AppleSdkTypeNamespaces is null (no Clang context, e.g. -fmodules AST),
        // the fallback only accepts names whose head matches a registered Apple
        // ObjC class prefix. The bare "any uppercase" rule was a false-positive
        // source: it let cross-framework third-party types (e.g. CloudPlatformOptions in a
        // sibling xcframework) through and produced CS0246 at compile time.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = null,
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doApple:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "color", Type = new ObjCTypeRef { Name = "UIColor", IsPointer = true } }]
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "doStuff:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "thing", Type = new ObjCTypeRef { Name = "AnyUnknownType", IsPointer = true } }]
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "doBad:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "x", Type = new ObjCTypeRef { Name = "some_c_type_t" } }]
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        // Apple-prefixed types pass the fallback
        Assert.Contains("DoApple", result);
        // Uppercase types without an Apple prefix are filtered (they would
        // otherwise emit references to types that aren't actually available)
        Assert.DoesNotContain("DoStuff", result);
        // Lowercase C-style types are also filtered
        Assert.DoesNotContain("DoBad", result);
    }

    [Fact]
    public void Emit_ApiDefinitionFiltering_AcceptsModuleDefinedTypes()
    {
        // Types defined in the module itself (classes, enums, structs, protocols) should always pass.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string>(), // Empty — no Apple types
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "Config",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "getStatus",
                            ReturnType = new ObjCTypeRef { Name = "MyStatus" },
                            IsInstanceMethod = true,
                        }
                    ]
                }
            ],
            Enums = [new ObjCEnumDecl { Name = "MyStatus", UnderlyingType = new ObjCTypeRef { Name = "NSInteger" } }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("GetStatus", result);
    }

    [Fact]
    public void Emit_ApiDefinitionFiltering_AcceptsRenamedUrlHttpAppleTypes()
    {
        // Apple SDK types with URL/HTTP naming are stored as raw ObjC names
        // (NSURLSessionDelegate) but MapType produces .NET convention names
        // (NSUrlSessionDelegate). The filter must reverse-normalize to match.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSURLSessionDelegate"] = "", ["NSHTTPURLResponse"] = "" },
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "MyDelegate",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "getResponse",
                            ReturnType = new ObjCTypeRef { Name = "NSHTTPURLResponse", IsPointer = true },
                            IsInstanceMethod = true,
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "getDelegate",
                            ReturnType = new ObjCTypeRef { Name = "id", ProtocolQualifications = { "NSURLSessionDelegate" } },
                            IsInstanceMethod = true,
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        // NSHTTPURLResponse → NSHttpUrlResponse via MapType, must match SDK name
        Assert.Contains("GetResponse", result);
        // id<NSURLSessionDelegate> → synthesized INSUrlSessionDelegate → strip I → NSUrlSessionDelegate
        // → reverse-normalize to NSURLSessionDelegate, which is in the SDK set.
        Assert.Contains("GetDelegate", result);
    }

    [Fact]
    public void Emit_ApiDefinitionFiltering_SkippedPropertyDoesNotReserveName()
    {
        // A property skipped by the resolvability filter must NOT reserve its
        // emitted name, so a later valid property with the same PascalCase name
        // can still be emitted.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["UIColor"] = "" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Properties =
                    [
                        // First property: unresolvable type → skipped
                        new ObjCPropertyDecl { Name = "color", Type = new ObjCTypeRef { Name = "ThirdPartyColor", IsPointer = true } },
                        // Second property: same PascalCase name "Color" but resolvable type → should emit
                        new ObjCPropertyDecl { Name = "color", Type = new ObjCTypeRef { Name = "UIColor", IsPointer = true } }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("Color", result);
        Assert.Contains("UIColor", result);
    }

    // --- Doc comment emission ---

    [Fact]
    public void Emit_ClassWithDocComment_EmitsXmlSummary()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "DocumentedClass",
                    DocComment = "A class that does things."
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("/// <summary>", result);
        Assert.Contains("/// A class that does things.", result);
        Assert.Contains("/// </summary>", result);
    }

    [Fact]
    public void Emit_MethodWithDocCommentAndParams_EmitsXmlDocWithParams()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doThingWithName:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "name", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }],
                            DocComment = "Does a thing.",
                            DocParams = [new ObjCDocParam { Name = "name", Description = "The name to use." }]
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("/// <summary>", result);
        Assert.Contains("/// Does a thing.", result);
        Assert.Contains("/// <param name=\"name\">The name to use.</param>", result);
    }

    [Fact]
    public void Emit_DocCommentWithXmlCharacters_EscapesProperly()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    DocComment = "Returns true if a < b && c > d."
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("a &lt; b &amp;&amp; c &gt; d", result);
    }

    [Fact]
    public void Emit_MethodWithGenericCollectionReturn_EmitsTypeHint()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "DataManager",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "allItems",
                            IsInstanceMethod = true,
                            ReturnType = new ObjCTypeRef
                            {
                                Name = "NSArray",
                                IsPointer = true,
                                GenericArgs = [new ObjCTypeRef { Name = "NSString", IsPointer = true }]
                            }
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        // With typed array mapping, NSArray<NSString *> → string[] (no type hint comment needed)
        Assert.Contains("string[] AllItems", result);
    }

    [Fact]
    public void Emit_MethodWithGenericCollectionParam_EmitsParamHint()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "DataManager",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "processItems:",
                            IsInstanceMethod = true,
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            Parameters =
                            [
                                new ObjCParameterDecl
                                {
                                    Name = "items",
                                    Type = new ObjCTypeRef
                                    {
                                        Name = "NSDictionary",
                                        IsPointer = true,
                                        GenericArgs =
                                        [
                                            new ObjCTypeRef { Name = "NSString", IsPointer = true },
                                            new ObjCTypeRef { Name = "NSNumber", IsPointer = true }
                                        ]
                                    }
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("// Parameter 'items': Key type: string, Value type: NSNumber", result);
    }

    [Fact]
    public void Emit_PropertyWithGenericCollection_EmitsTypeHint()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "DataManager",
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "items",
                            IsReadonly = true,
                            Type = new ObjCTypeRef
                            {
                                Name = "NSArray",
                                IsPointer = true,
                                GenericArgs = [new ObjCTypeRef { Name = "NSData", IsPointer = true }]
                            }
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        // With typed array mapping, NSArray<NSData *> → NSData[] (no type hint comment needed)
        Assert.Contains("NSData[] Items { get; }", result);
    }

    // --- Fix #9a: ArgumentSemantic on property [Export] attribute ---

    [Theory]
    [InlineData(ObjCMemorySemantic.Copy, "ArgumentSemantic.Copy")]
    [InlineData(ObjCMemorySemantic.Assign, "ArgumentSemantic.Assign")]
    [InlineData(ObjCMemorySemantic.Weak, "ArgumentSemantic.Weak")]
    [InlineData(ObjCMemorySemantic.Strong, "ArgumentSemantic.Retain")]
    [InlineData(ObjCMemorySemantic.Retain, "ArgumentSemantic.Retain")]
    [InlineData(ObjCMemorySemantic.UnsafeUnretained, "ArgumentSemantic.Assign")]
    public void Emit_Property_WithMemorySemantic_EmitsArgumentSemantic(ObjCMemorySemantic semantic, string expectedSuffix)
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "data",
                    Type = new ObjCTypeRef { Name = "NSData", IsPointer = true },
                    IsReadonly = true,
                    MemorySemantic = semantic,
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains($"[Export(\"data\", {expectedSuffix})]", result);
    }

    [Fact]
    public void Emit_Property_NoMemorySemantic_NoArgumentSemantic()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "data",
                    Type = new ObjCTypeRef { Name = "NSData", IsPointer = true },
                    IsReadonly = true,
                    MemorySemantic = ObjCMemorySemantic.None,
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"data\")]", result);
        Assert.DoesNotContain("ArgumentSemantic", result);
    }

    [Fact]
    public void Emit_CopyProperty_ReadWrite_HasArgumentSemanticOnGetter()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "APNSToken",
                    Type = new ObjCTypeRef { Name = "NSData", IsPointer = true },
                    IsReadonly = false,
                    MemorySemantic = ObjCMemorySemantic.Copy,
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"APNSToken\", ArgumentSemantic.Copy)]", result);
        Assert.Contains("NSData APNSToken", result);
    }

    [Fact]
    public void Emit_WeakDelegateProperty_HasArgumentSemanticWeak()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true, Nullability = ObjCNullability.Nullable },
                    IsReadonly = false,
                    MemorySemantic = ObjCMemorySemantic.Weak,
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"delegate\", ArgumentSemantic.Weak)]", result);
        Assert.Contains("[NullAllowed]", result);
    }

    // --- Fix #9b: [Bind] for custom getter selectors ---

    [Fact]
    public void Emit_Property_CustomGetter_EmitsBindAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "autoInitEnabled",
                    Type = new ObjCTypeRef { Name = "BOOL" },
                    IsReadonly = true,
                    GetterSelector = "isAutoInitEnabled",
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Export(\"isAutoInitEnabled\")]", result);
        Assert.Contains("[Bind(\"isAutoInitEnabled\")] get;", result);
    }

    [Fact]
    public void Emit_Property_CustomGetter_ReadWrite_EmitsBindOnGet()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "hidden",
                    Type = new ObjCTypeRef { Name = "BOOL" },
                    IsReadonly = false,
                    GetterSelector = "isHidden",
                    SetterSelector = "setHidden:",
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Bind(\"isHidden\")] get;", result);
        Assert.Contains("[Export(\"setHidden:\")] set;", result);
    }

    [Fact]
    public void Emit_Property_MatchingGetter_NoBindAttribute()
    {
        // When getter selector matches property name, no [Bind] needed
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "count",
                    Type = new ObjCTypeRef { Name = "NSInteger" },
                    IsReadonly = true,
                    GetterSelector = "count",
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[Bind(", result);
        Assert.Contains("nint Count { get; }", result);
    }

    [Fact]
    public void Emit_Property_NullGetter_NoBindAttribute()
    {
        // When no getter selector is set (null), no [Bind] needed
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "title",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[Bind(", result);
    }

    // --- Fix #10: Typed arrays/generics ---

    [Fact]
    public void Emit_Property_NSArrayOfConcreteType_EmitsTypedArray()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                // The element type must be a resolvable concrete type — declare it in the module so
                // it lands in knownTypes (an undeclared element would correctly drop, since emitting
                // a typed array of an unresolvable element is CS0246 in the contract compile).
                new ObjCClassDecl { Name = "LabelPrinterLog" },
                new ObjCClassDecl
                {
                    Name = "Logger",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "allLogs",
                        Type = new ObjCTypeRef
                        {
                            Name = "NSArray",
                            IsPointer = true,
                            GenericArgs = [new ObjCTypeRef { Name = "LabelPrinterLog", IsPointer = true }]
                        },
                        IsReadonly = true,
                    }]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("LabelPrinterLog[] AllLogs { get; }", result);
    }

    [Fact]
    public void Emit_Method_NSArrayParam_EmitsTypedArrayParam()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "Printer",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "printURLs:settings:",
                    IsInstanceMethod = true,
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    Parameters =
                    [
                        new ObjCParameterDecl
                        {
                            Name = "urls",
                            Type = new ObjCTypeRef
                            {
                                Name = "NSArray",
                                IsPointer = true,
                                GenericArgs = [new ObjCTypeRef { Name = "NSURL", IsPointer = true }]
                            }
                        },
                        new ObjCParameterDecl
                        {
                            Name = "settings",
                            Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true }
                        }
                    ]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("NSUrl[] urls", result);
    }

    [Fact]
    public void Emit_Method_NSDictionaryParam_EmitsTypedDictionary()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "Config",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "applyConfig:",
                    IsInstanceMethod = true,
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    Parameters =
                    [
                        new ObjCParameterDecl
                        {
                            Name = "config",
                            Type = new ObjCTypeRef
                            {
                                Name = "NSDictionary",
                                IsPointer = true,
                                GenericArgs =
                                [
                                    new ObjCTypeRef { Name = "NSString", IsPointer = true },
                                    new ObjCTypeRef { Name = "NSNumber", IsPointer = true }
                                ]
                            }
                        }
                    ]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("NSDictionary<NSString, NSNumber> config", result);
    }

    // ──────────────────────────────────────────────
    // Fix #4: Optional vs required member classification
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ProtocolOptionalProperty_NoAbstractAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyProto",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "optionalTitle",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                    IsOptional = true,
                    GetterSelector = "optionalTitle"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[Abstract]", result);
        Assert.Contains("string OptionalTitle { get; }", result);
    }

    [Fact]
    public void Emit_ProtocolMixedOptionalAndRequired_CorrectAbstractPlacement()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyProto",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "requiredMethod",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        IsOptional = false
                    },
                    new ObjCMethodDecl
                    {
                        Selector = "optionalMethod",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true,
                        IsOptional = true
                    }
                ],
                Properties =
                [
                    new ObjCPropertyDecl
                    {
                        Name = "requiredProp",
                        Type = new ObjCTypeRef { Name = "NSInteger" },
                        IsReadonly = true,
                        IsOptional = false
                    },
                    new ObjCPropertyDecl
                    {
                        Name = "optionalProp",
                        Type = new ObjCTypeRef { Name = "NSInteger" },
                        IsReadonly = true,
                        IsOptional = true
                    }
                ]
            }]
        };

        var result = EmitAndRead(module);
        var lines = result.Split('\n');

        // For each member, check whether [Abstract] appears immediately before [Export]
        // Required method: [Abstract] should precede [Export("requiredMethod")]
        var requiredMethodExportLine = Array.FindIndex(lines, l => l.Contains("[Export(\"requiredMethod\")]"));
        Assert.True(requiredMethodExportLine > 0);
        Assert.Contains("[Abstract]", lines[requiredMethodExportLine - 1]);

        // Optional method: NO [Abstract] before [Export("optionalMethod")]
        var optionalMethodExportLine = Array.FindIndex(lines, l => l.Contains("[Export(\"optionalMethod\")]"));
        Assert.True(optionalMethodExportLine > 0);
        Assert.DoesNotContain("[Abstract]", lines[optionalMethodExportLine - 1]);

        // Required property: [Abstract] should precede [Export("requiredProp")]
        var requiredPropExportLine = Array.FindIndex(lines, l => l.Contains("[Export(\"requiredProp\")]"));
        Assert.True(requiredPropExportLine > 0);
        Assert.Contains("[Abstract]", lines[requiredPropExportLine - 1]);

        // Optional property: NO [Abstract] before [Export("optionalProp")]
        var optionalPropExportLine = Array.FindIndex(lines, l => l.Contains("[Export(\"optionalProp\")]"));
        Assert.True(optionalPropExportLine > 0);
        Assert.DoesNotContain("[Abstract]", lines[optionalPropExportLine - 1]);
    }

    // ──────────────────────────────────────────────
    // Fix #6: DisableDefaultCtor + explicit init contradiction
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_DisableDefaultCtor_SuppressesExplicitInitConstructor()
    {
        // When [DisableDefaultCtor] is emitted, the explicit [Export("init")] Constructor()
        // should be suppressed to avoid contradiction.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "init",
                        ReturnType = new ObjCTypeRef { Name = "instancetype" },
                        IsInstanceMethod = true
                    },
                    new ObjCMethodDecl
                    {
                        Selector = "doSomething",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true
                    }
                ]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[DisableDefaultCtor]", result);
        // Should NOT have [Export("init")] Constructor() — contradicts DisableDefaultCtor
        Assert.DoesNotContain("[Export(\"init\")]", result);
        Assert.DoesNotContain("Constructor()", result);
        // Other methods should still be emitted
        Assert.Contains("[Export(\"doSomething\")]", result);
    }

    [Fact]
    public void Emit_DisableDefaultCtor_KeepsParameterizedConstructors()
    {
        // DisableDefaultCtor should NOT suppress parameterized constructors
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "init",
                        ReturnType = new ObjCTypeRef { Name = "instancetype" },
                        IsInstanceMethod = true
                    },
                    new ObjCMethodDecl
                    {
                        Selector = "initWithName:",
                        ReturnType = new ObjCTypeRef { Name = "instancetype" },
                        IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl
                        {
                            Name = "name",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                        }]
                    }
                ]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[DisableDefaultCtor]", result);
        // Parameterized init should still be emitted
        Assert.Contains("[Export(\"initWithName:\")]", result);
        Assert.Contains("NativeHandle Constructor(string name);", result);
        // Parameterless init suppressed
        Assert.DoesNotContain("[Export(\"init\")]", result);
    }

    // ──────────────────────────────────────────────
    // Fix #7: [Model] on delegate protocols
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_DelegateProtocol_HasModelAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyViewDelegate",
                IsDelegateProtocol = true,
                Methods = [new ObjCMethodDecl
                {
                    Selector = "viewDidLoad",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    IsOptional = true
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Protocol, Model]", result);
        Assert.Contains("[BaseType(typeof(NSObject))]", result);
        // [Model] protocols declare the bare name (no I prefix) per Xamarin convention — bgen
        // synthesizes IMyViewDelegate from it. The empty `interface IMyViewDelegate {}` forward
        // declaration (for member references) is emitted, but the [Protocol] declaration itself
        // must never be pre-prefixed to the double-I shape.
        Assert.Contains("partial interface MyViewDelegate", result);
        Assert.Contains("interface IMyViewDelegate { }", result);
        Assert.DoesNotContain("partial interface IMyViewDelegate", result);
    }

    [Fact]
    public void Emit_DataSourceProtocol_HasModelAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyTableDataSource",
                IsDelegateProtocol = true,
                Methods = [new ObjCMethodDecl
                {
                    Selector = "numberOfRows",
                    ReturnType = new ObjCTypeRef { Name = "NSInteger" },
                    IsInstanceMethod = true,
                    IsOptional = false
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Protocol, Model]", result);
        Assert.Contains("partial interface MyTableDataSource", result);
    }

    [Fact]
    public void Emit_NonDelegateProtocol_NoModelAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Configurable",
                IsDelegateProtocol = false
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Protocol]", result);
        Assert.DoesNotContain("[Model]", result);
        Assert.Contains("partial interface Configurable", result);
    }

    // ──────────────────────────────────────────────
    // Fix #8: WeakDelegate/Wrap pattern
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_DelegateProperty_EmitsWeakDelegateWrapPattern()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyViewDelegate",
                IsDelegateProtocol = true
            }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["MyViewDelegate"]
                    },
                    IsReadonly = false,
                    GetterSelector = "delegate"
                }]
            }]
        };

        var result = EmitAndRead(module);

        // Strong-typed property with [Wrap]
        Assert.Contains("[Wrap(\"WeakDelegate\")]", result);
        Assert.Contains("[NullAllowed]", result);
        Assert.Contains("MyViewDelegate Delegate { get; set; }", result);

        // Weak NSObject property with [Export] — readwrite uses setter export
        Assert.Contains("[NullAllowed, Export(\"delegate\", ArgumentSemantic.Weak)]", result);
        Assert.Contains("NSObject WeakDelegate {", result);
        Assert.Contains("[Export(\"setDelegate:\")] set;", result);
    }

    [Fact]
    public void Emit_DataSourceProperty_EmitsWeakDelegateWrapPattern()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "TableDataSource",
                IsDelegateProtocol = true
            }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyTable",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "dataSource",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["TableDataSource"]
                    },
                    IsReadonly = false,
                    GetterSelector = "dataSource"
                }]
            }]
        };

        var result = EmitAndRead(module);

        Assert.Contains("[Wrap(\"WeakDataSource\")]", result);
        Assert.Contains("TableDataSource DataSource { get; set; }", result);
        Assert.Contains("[NullAllowed, Export(\"dataSource\", ArgumentSemantic.Weak)]", result);
        Assert.Contains("NSObject WeakDataSource {", result);
        Assert.Contains("[Export(\"setDataSource:\")] set;", result);
    }

    [Fact]
    public void Emit_DelegateProperty_Availability_AppliedToBothWrapAndWeakMembers()
    {
        // A delegate property with recovered availability emits TWO C# members (the [Wrap] strong
        // property AND the [Export] weak backing property). The platform-availability attribute must
        // guard BOTH — a consumer touching WeakDelegate directly needs the analyzer guard just as much
        // as one using the strong Delegate property. Regression guard: the weak member used to be
        // emitted without availability.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyViewDelegate",
                IsDelegateProtocol = true
            }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["MyViewDelegate"]
                    },
                    IsReadonly = false,
                    GetterSelector = "delegate",
                    Availability = [new ObjCAvailability { Platform = "ios", IntroducedVersion = "15.0" }]
                }]
            }]
        };

        var result = EmitAndRead(module);

        // Both the strong [Wrap] property and the weak [Export] property emit; each is guarded.
        Assert.Contains("[Wrap(\"WeakDelegate\")]", result);
        Assert.Contains("NSObject WeakDelegate {", result);

        // The availability attribute appears TWICE — once before each member.
        const string attr = "[global::System.Runtime.Versioning.SupportedOSPlatform(\"ios15.0\")]";
        var first = result.IndexOf(attr, StringComparison.Ordinal);
        Assert.True(first >= 0, "availability must guard the strong [Wrap] delegate property");
        var second = result.IndexOf(attr, first + attr.Length, StringComparison.Ordinal);
        Assert.True(second >= 0, "availability must ALSO guard the weak [Export] backing property");

        // Sanity: the second attribute precedes the weak [Export] member.
        var weakExportIdx = result.IndexOf("[NullAllowed, Export(\"delegate\", ArgumentSemantic.Weak)]", StringComparison.Ordinal);
        Assert.True(second < weakExportIdx, "second availability attribute must precede the weak [Export] property");
    }

    [Fact]
    public void Emit_NonDelegateProperty_NoWeakDelegatePattern()
    {
        // A property named "delegate" but whose type is NOT a delegate protocol
        // should be emitted normally.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef { Name = "NSObject", IsPointer = true },
                    IsReadonly = false,
                    GetterSelector = "delegate"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("WeakDelegate", result);
        Assert.DoesNotContain("[Wrap(", result);
        Assert.Contains("[Export(\"delegate\")]", result);
    }

    [Fact]
    public void Emit_NavigationDelegateProperty_EmitsWeakDelegateWrapPattern()
    {
        // A property named "navigationDelegate" with a delegate protocol type
        // should also use the WeakDelegate/Wrap pattern, not just "delegate"/"dataSource".
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "WKNavigationDelegate",
                IsDelegateProtocol = true
            }],
            Classes = [new ObjCClassDecl
            {
                Name = "WKWebView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "navigationDelegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["WKNavigationDelegate"]
                    },
                    IsReadonly = false,
                    GetterSelector = "navigationDelegate"
                }]
            }]
        };

        var result = EmitAndRead(module);

        // Should emit the WeakDelegate/Wrap pattern
        Assert.Contains("[Wrap(\"WeakNavigationDelegate\")]", result);
        Assert.Contains("WKNavigationDelegate NavigationDelegate { get; set; }", result);
        Assert.Contains("[NullAllowed, Export(\"navigationDelegate\", ArgumentSemantic.Weak)]", result);
        Assert.Contains("NSObject WeakNavigationDelegate {", result);
    }

    // --- [DesignatedInitializer] emission ---

    [Fact]
    public void Emit_DesignatedInitializer_EmitsAttribute()
    {
        var module = ObjCModuleBuilder.Create()
            .WithClass(new ObjCClassDecl
            {
                Name = "MyWidget",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "initWithName:value:",
                        ReturnType = SimpleType("instancetype"),
                        IsInstanceMethod = true,
                        IsDesignatedInitializer = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "name", Type = SimpleType("NSString", isPointer: true) },
                            new ObjCParameterDecl { Name = "value", Type = SimpleType("int") }
                        ]
                    }
                ]
            })
            .Build();

        var result = EmitAndRead(module);
        Assert.Contains("[DesignatedInitializer]", result);
        Assert.Contains("[Export(\"initWithName:value:\")]", result);
        Assert.Contains("NativeHandle Constructor(", result);
    }

    [Fact]
    public void Emit_NonDesignatedInitializer_NoAttribute()
    {
        var module = ObjCModuleBuilder.Create()
            .WithClass(new ObjCClassDecl
            {
                Name = "MyWidget",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "initWithFrame:",
                        ReturnType = SimpleType("instancetype"),
                        IsInstanceMethod = true,
                        IsDesignatedInitializer = false,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "frame", Type = SimpleType("CGRect") }
                        ]
                    }
                ]
            })
            .Build();

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[DesignatedInitializer]", result);
        Assert.Contains("[Export(\"initWithFrame:\")]", result);
    }

    [Fact]
    public void Emit_DesignatedInitializer_NotEmittedOnNonConstructorMethod()
    {
        var module = ObjCModuleBuilder.Create()
            .WithClass(new ObjCClassDecl
            {
                Name = "MyWidget",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "doSomething",
                        ReturnType = SimpleType("void"),
                        IsInstanceMethod = true,
                        IsDesignatedInitializer = true, // Should be ignored for non-init methods
                    }
                ]
            })
            .Build();

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[DesignatedInitializer]", result);
    }

    // --- Delegate method naming ---

    [Theory]
    // Two-part: first part is delegate owner, second is action
    [InlineData("messaging:didReceiveRegistrationToken:", "DidReceiveRegistrationToken")]
    [InlineData("tableView:cellForRowAtIndexPath:", "CellForRowAtIndexPath")]
    [InlineData("didReceiveNotification:", "DidReceiveNotification")]
    [InlineData("viewDidLoad", "ViewDidLoad")]
    // Three-part: concatenate all parts after the first (owner)
    [InlineData("URLSession:task:didCompleteWithError:", "TaskDidCompleteWithError")]
    [InlineData("URLSession:downloadTask:didFinishDownloadingToURL:", "DownloadTaskDidFinishDownloadingToURL")]
    [InlineData("tableView:commitEditingStyle:forRowAtIndexPath:", "CommitEditingStyleForRowAtIndexPath")]
    public void SelectorToDelegateMethodName_ConvertsCorrectly(string selector, string expected)
    {
        Assert.Equal(expected, ApiDefinitionEmitter.SelectorToDelegateMethodName(selector));
    }

    [Fact]
    public void Emit_DelegateProtocolMethod_UsesLastSelectorPart()
    {
        var module = ObjCModuleBuilder.Create()
            .WithProtocol(new ObjCProtocolDecl
            {
                Name = "CloudPlatformSdkMessagingDelegate",
                IsDelegateProtocol = true,
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "messaging:didReceiveRegistrationToken:",
                        ReturnType = SimpleType("void"),
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "messaging", Type = SimpleType("CloudPlatformSdkMessaging", isPointer: true) },
                            new ObjCParameterDecl { Name = "fcmToken", Type = SimpleType("NSString", isPointer: true) }
                        ]
                    }
                ]
            })
            // CloudPlatformSdkMessaging would normally come from a sibling
            // framework (CloudPlatformSdkMessaging module) parsed alongside — declare it here so
            // the resolvability filter doesn't drop the method we're asserting on.
            .WithAppleSdkTypeNames("CloudPlatformSdkMessaging")
            .Build();

        var result = EmitAndRead(module);
        // Delegate protocol method should use last selector part (action verb)
        Assert.Contains("void DidReceiveRegistrationToken(", result);
        Assert.DoesNotContain("void Messaging(", result);
    }

    [Fact]
    public void Emit_NonDelegateProtocolMethod_UsesFirstSelectorPart()
    {
        var module = ObjCModuleBuilder.Create()
            .WithProtocol(new ObjCProtocolDecl
            {
                Name = "SomeProtocol",
                IsDelegateProtocol = false,
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "messaging:didReceiveRegistrationToken:",
                        ReturnType = SimpleType("void"),
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl { Name = "messaging", Type = SimpleType("CloudPlatformSdkMessaging", isPointer: true) },
                            new ObjCParameterDecl { Name = "fcmToken", Type = SimpleType("NSString", isPointer: true) }
                        ]
                    }
                ]
            })
            .WithAppleSdkTypeNames("CloudPlatformSdkMessaging")
            .Build();

        var result = EmitAndRead(module);
        // Non-delegate protocol should use first selector part
        Assert.Contains("void Messaging(", result);
    }

    // --- Foreign-type category emission ---

    [Fact]
    public void Emit_ForeignTypeCategory_ProtocolOnlySkipped()
    {
        // Protocol-only categories are skipped: bgen generates static classes
        // which cannot implement interfaces (CS0714)
        var module = ObjCModuleBuilder.Create()
            .WithCategory(new ObjCCategoryDecl
            {
                CategoryName = "MOSValue",
                ClassName = "NSNull",
                ProtocolNames = ["MOSValue"],
            })
            .Build();

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[Category]", result);
        Assert.DoesNotContain("NSNull_MOSValue", result);
    }

    [Fact]
    public void Emit_ForeignTypeCategoryWithMethods_EmitsMethodsStripsProtocols()
    {
        // Categories with methods are emitted, but protocol conformance is stripped
        // because bgen generates static classes which can't implement interfaces
        var module = ObjCModuleBuilder.Create()
            .WithCategory(new ObjCCategoryDecl
            {
                CategoryName = "Swift",
                ClassName = "NSNumber",
                ProtocolNames = ["MOSInt", "MOSBool"],
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "mos_intValue",
                        ReturnType = SimpleType("int"),
                        IsInstanceMethod = true,
                    }
                ]
            })
            .Build();

        var result = EmitAndRead(module);
        Assert.Contains("[Category]", result);
        Assert.Contains("[BaseType(typeof(NSNumber))]", result);
        // Protocol conformance stripped (CS0714: static classes can't implement interfaces)
        Assert.Contains("partial interface NSNumber_Swift", result);
        Assert.DoesNotContain("IMOSInt", result);
        Assert.DoesNotContain("IMOSBool", result);
        Assert.Contains("[Export(\"mos_intValue\")]", result);
        Assert.Contains("int Mos_intValue()", result);
    }

    [Fact]
    public void Emit_ForeignTypeCategoryWithProperties_ProjectsInstancePropertiesAsAccessorMethods()
    {
        // bgen generates a [Category] as a static class, which cannot hold an instance PROPERTY
        // (CS0708) — but an instance METHOD is fine (it becomes an extension method carrying the
        // receiver), so the property is projected onto accessor methods rather than dropped.
        var module = ObjCModuleBuilder.Create()
            .WithCategory(new ObjCCategoryDecl
            {
                CategoryName = "WebCache",
                ClassName = "UIButton",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "sd_setImageWithURL:",
                        ReturnType = SimpleType("void"),
                        IsInstanceMethod = true,
                        Parameters = [new ObjCParameterDecl { Name = "url", Type = SimpleType("NSUrl", isPointer: true) }]
                    }
                ],
                Properties =
                [
                    new ObjCPropertyDecl
                    {
                        Name = "sd_currentImageURL",
                        Type = SimpleType("NSUrl", isPointer: true),
                        IsReadonly = true,
                        IsClass = false, // instance property — projected as an accessor method
                    }
                ]
            })
            .Build();

        var result = EmitAndRead(module);
        Assert.Contains("[Category]", result);
        // Method emitted as extension method
        Assert.Contains("Sd_setImageWithURL(", result);
        // Instance property reachable through its accessor, on the property's own selector
        Assert.Contains("[Export(\"sd_currentImageURL\")]", result);
        Assert.Contains("NSUrl GetSd_currentImageURL();", result);
        // …but never as an instance property (CS0708)
        Assert.DoesNotContain("NSUrl Sd_currentImageURL {", result);
    }

    [Fact]
    public void Emit_Category_ClassPropertyOnly_NotSkippedAsEmpty()
    {
        // A category with only class (static) properties and no methods should
        // still be emitted — class properties are valid in static extension classes.
        var module = ObjCModuleBuilder.Create()
            .WithCategory(new ObjCCategoryDecl
            {
                CategoryName = "Defaults",
                ClassName = "NSNumber",
                Properties =
                [
                    new ObjCPropertyDecl
                    {
                        Name = "defaultValue",
                        Type = SimpleType("NSNumber", isPointer: true),
                        IsReadonly = true,
                        IsClass = true, // class property → valid in static class
                    }
                ]
            })
            .Build();

        var result = EmitAndRead(module);
        Assert.Contains("[Category]", result);
        Assert.Contains("partial interface NSNumber_Defaults", result);
        Assert.Contains("[Static]", result);
        Assert.Contains("NSNumber DefaultValue { get; }", result);
    }

    [Fact]
    public void Emit_ChildProtocol_SignatureClashWithInheritedParent_RenamedViaFullSelector()
    {
        // Reproduces the IMTRXPCServerProtocol pattern from Matter: child protocol's own method
        // and an inherited parent method PascalCase to the same name + same C# param signature.
        // bgen flattens inherited methods into the generated concrete class, so the clash would
        // produce CS0111 without cross-interface dedup. The emitter must seed the child's dedup
        // set with parent signatures and rename the child's method via SelectorToFullMethodName.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "ParentProto",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doThing:withState:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters =
                            [
                                new ObjCParameterDecl { Name = "thing", Type = new ObjCTypeRef { Name = "NSUUID", IsPointer = true } },
                                new ObjCParameterDecl { Name = "state", Type = new ObjCTypeRef { Name = "NSDictionary", IsPointer = true } },
                            ]
                        }
                    ]
                },
                new ObjCProtocolDecl
                {
                    Name = "ChildProto",
                    InheritedProtocolNames = ["ParentProto"],
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doThing:withContext:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters =
                            [
                                new ObjCParameterDecl { Name = "thing", Type = new ObjCTypeRef { Name = "NSUUID", IsPointer = true } },
                                new ObjCParameterDecl { Name = "context", Type = new ObjCTypeRef { Name = "NSDictionary", IsPointer = true } },
                            ]
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        // Parent keeps the short name; child renames to full-selector form.
        Assert.Contains("void DoThing(NSUuid thing, NSDictionary state);", result);
        Assert.Contains("void DoThingWithContext(NSUuid thing, NSDictionary context);", result);
        Assert.DoesNotContain("void DoThing(NSUuid thing, NSDictionary context);", result);
    }

    [Fact]
    public void Emit_TransitiveInheritance_ChildSeesGrandparentInducedRename()
    {
        // Three-level chain where the parent's own method gets renamed by a grandparent-induced
        // collision. If the child's seed records the parent under its naïve short name (instead of
        // recursively resolving the parent's actual emission), the child can emit a duplicate of
        // the parent's renamed method. Verifies SeedInheritedProtocolSignatures handles transitive
        // renames via recursive memoization.
        //
        // Selector + param shape:
        //   Grand: do:    (NSUUID) → emits Do(NSUuid)
        //   Parent : Grand: do:    (NSUUID) → start "Do(NSUuid)" collides → rename to full
        //                                       "Do" (single part) → suffix → Do2(NSUuid)
        //   Child  : Parent: do:   (NSUUID) → must avoid both Do(NSUuid) and Do2(NSUuid) → Do3(NSUuid)
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "GrandProto",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "do:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [ new ObjCParameterDecl { Name = "x", Type = new ObjCTypeRef { Name = "NSUUID", IsPointer = true } } ]
                        }
                    ]
                },
                new ObjCProtocolDecl
                {
                    Name = "ParentProto",
                    InheritedProtocolNames = ["GrandProto"],
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "do:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [ new ObjCParameterDecl { Name = "x", Type = new ObjCTypeRef { Name = "NSUUID", IsPointer = true } } ]
                        }
                    ]
                },
                new ObjCProtocolDecl
                {
                    Name = "ChildProto",
                    InheritedProtocolNames = ["ParentProto"],
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "do:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [ new ObjCParameterDecl { Name = "x", Type = new ObjCTypeRef { Name = "NSUUID", IsPointer = true } } ]
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("void Do(NSUuid x);", result);
        Assert.Contains("void Do2(NSUuid x);", result);
        Assert.Contains("void Do3(NSUuid x);", result);
    }

    [Fact]
    public void Emit_ChildMethod_CollidingWithInheritedParentProperty_RenamedViaFullSelector()
    {
        // Parent protocol exposes property `foo`; child protocol exposes method `foo:` (one param).
        // bgen flattens both into the concrete class — property Foo and method Foo(...) would
        // collide as CS0102 ("type already contains a definition for 'Foo'"). The seed must record
        // the ancestor's property name into emittedMemberNames AND EmitMethod must consult that
        // set during dedup, renaming the child method via SelectorToFullMethodName.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "ParentProto",
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "foo",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                            IsReadonly = true,
                        }
                    ]
                },
                new ObjCProtocolDecl
                {
                    Name = "ChildProto",
                    InheritedProtocolNames = ["ParentProto"],
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "foo:withBar:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters =
                            [
                                new ObjCParameterDecl { Name = "a", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } },
                                new ObjCParameterDecl { Name = "b", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } },
                            ]
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("string Foo { get; }", result);
        Assert.Contains("void FooWithBar(string a, string b);", result);
        // The naïve short name must NOT be emitted — it would collide with parent's Foo property.
        Assert.DoesNotContain("void Foo(string a, string b);", result);
    }

    [Fact]
    public void Emit_MethodOverloads_SameShortName_DifferentSignatures_NotRenamed()
    {
        // Two methods on the same protocol that PascalCase to the same name but have different
        // parameter signatures are legal C# overloads (CS0111 only fires on identical signatures).
        // The cross-set dedup that prevents method-vs-property name collisions must NOT block
        // legitimate method overloads.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "Proto",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doThing:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [ new ObjCParameterDecl { Name = "a", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } } ]
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "doThing:withCount:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters =
                            [
                                new ObjCParameterDecl { Name = "a", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } },
                                new ObjCParameterDecl { Name = "n", Type = new ObjCTypeRef { Name = "int" } },
                            ]
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("void DoThing(string a);", result);
        Assert.Contains("void DoThing(string a, int n);", result);
        Assert.DoesNotContain("DoThingWithCount", result);
    }

    [Fact]
    public void Emit_ChildMethodOverload_SameShortNameAsInheritedMethod_DifferentSignature_NotRenamed()
    {
        // Same rule across inheritance: child method with same short name as parent's method but
        // different signature is a legal overload after bgen flattens, and must not be renamed.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "ParentProto",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doThing:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [ new ObjCParameterDecl { Name = "a", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } } ]
                        }
                    ]
                },
                new ObjCProtocolDecl
                {
                    Name = "ChildProto",
                    InheritedProtocolNames = ["ParentProto"],
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "doThing:withCount:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters =
                            [
                                new ObjCParameterDecl { Name = "a", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } },
                                new ObjCParameterDecl { Name = "n", Type = new ObjCTypeRef { Name = "int" } },
                            ]
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("void DoThing(string a);", result);
        Assert.Contains("void DoThing(string a, int n);", result);
        Assert.DoesNotContain("DoThingWithCount", result);
    }

    [Fact]
    public void Emit_WeakDelegatePattern_PreservedWhenMethodNameCollides_MethodRenamed()
    {
        // The WeakDelegate/Wrap pattern emits two members (Delegate + WeakDelegate). When a
        // method PascalCases to `WeakDelegate` — the synthetic weak-accessor name — the property
        // members win their C# names and the METHOD takes a numeric suffix (`WeakDelegate2`),
        // preserving both the strong delegate property and its weak accessor. Previously both
        // delegate members were dropped to clear the method's CS0102 collision.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyViewDelegate",
                IsDelegateProtocol = true
            }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "weakDelegate",
                        ReturnType = new ObjCTypeRef { Name = "NSObject", IsPointer = true },
                        IsInstanceMethod = true,
                        Parameters = []
                    }
                ],
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef
                    {
                        Name = "id",
                        IsPointer = true,
                        ProtocolQualifications = ["MyViewDelegate"]
                    },
                    IsReadonly = false,
                    GetterSelector = "delegate"
                }]
            }]
        };

        var result = EmitAndRead(module);

        // Method renamed to clear the synthetic weak-accessor name.
        Assert.Contains("WeakDelegate2()", result);
        // The WeakDelegate/Wrap pattern is preserved (no longer dropped).
        Assert.Contains("[Wrap(\"WeakDelegate\")]", result);
        // Strong delegate property preserved.
        Assert.Contains("Delegate { get; set; }", result);
    }

    // ──────────────────────────────────────────────
    // Unresolvable base/conformance degradation
    // (a mixed binding whose surface references an external/cross-boundary type)
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ClassWithUnresolvableBaseType_Dropped_ResolvableSiblingKept()
    {
        // A Swift test framework like Quick declares `QuickSpec : XCTestCase`. XCTestCase is NOT a
        // bindable Apple SDK type (it lives under the platform Developer-tools frameworks, so it is
        // absent from AppleSdkTypeNamespaces), so emitting [BaseType(typeof(XCTestCase))] would fail with
        // CS0246. Degrade gracefully: drop QuickSpec but keep QuickConfiguration : NSObject.
        var module = new ObjCModule
        {
            ModuleName = "Quick",
            // Non-empty → engages the precise oracle. NSString is a real SDK type; XCTestCase is
            // deliberately absent (mirroring the IsAppleSdkPath fix that excludes it).
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSString"] = "", ["NSObject"] = "" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "QuickSpec",
                    SuperclassName = "XCTestCase",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "spec",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true
                    }]
                },
                new ObjCClassDecl
                {
                    Name = "QuickConfiguration",
                    SuperclassName = "NSObject",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "configure",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true
                    }]
                }
            ]
        };

        var result = EmitAndRead(module);
        // Unresolvable-base class dropped entirely — no dangling [BaseType(typeof(XCTestCase))].
        Assert.DoesNotContain("XCTestCase", result);
        Assert.DoesNotContain("partial interface QuickSpec", result);
        // Resolvable sibling kept.
        Assert.Contains("partial interface QuickConfiguration", result);
        Assert.Contains("[BaseType(typeof(NSObject))]", result);
    }

    [Fact]
    public void Emit_TransitiveSubclassOfDroppedClass_AlsoDropped()
    {
        // A test framework commonly subclasses its own base spec: MySpec : MyBaseSpec : XCTestCase.
        // MyBaseSpec drops because XCTestCase is unresolvable. MySpec must ALSO drop — its base type
        // MyBaseSpec was removed, so [BaseType(typeof(MyBaseSpec))] would dangle (CS0246). The
        // per-class base check can't catch this on its own: every class name is seeded into
        // knownTypes before emission, so MyBaseSpec still reads as "known" for MySpec — only the
        // fixpoint pre-pass over the drop set catches the transitive chain. Declaration order is
        // intentionally leaf-before-base to prove the fixpoint converges regardless of order.
        var module = new ObjCModule
        {
            ModuleName = "Quick",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSString"] = "", ["NSObject"] = "" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MySpec",
                    SuperclassName = "MyBaseSpec",
                    Methods = [new ObjCMethodDecl { Selector = "spec", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true }]
                },
                new ObjCClassDecl
                {
                    Name = "MyBaseSpec",
                    SuperclassName = "XCTestCase",
                    Methods = [new ObjCMethodDecl { Selector = "baseSpec", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true }]
                },
                new ObjCClassDecl
                {
                    Name = "QuickConfiguration",
                    SuperclassName = "NSObject",
                    Methods = [new ObjCMethodDecl { Selector = "configure", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true }]
                }
            ],
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "MySpec",
                    Methods = [new ObjCMethodDecl { Selector = "doExtra", ReturnType = new ObjCTypeRef { Name = "void" }, IsInstanceMethod = true }]
                }
            ]
        };

        var result = EmitAndRead(module);
        // Both the unresolvable-base class and its subclass are dropped.
        Assert.DoesNotContain("XCTestCase", result);
        Assert.DoesNotContain("partial interface MyBaseSpec", result);
        Assert.DoesNotContain("partial interface MySpec", result);
        // Critically: no dangling base type referencing the dropped intermediate class.
        Assert.DoesNotContain("[BaseType(typeof(MyBaseSpec))]", result);
        // A category on the transitively-dropped subclass is skipped too.
        Assert.DoesNotContain("MySpec_Extras", result);
        // The resolvable sibling is unaffected.
        Assert.Contains("partial interface QuickConfiguration", result);
    }

    [Fact]
    public void Emit_CategoryOnDroppedClass_Skipped()
    {
        // A category whose base class was dropped (unresolvable base type) would emit
        // [Category][BaseType(typeof(QuickSpec))] against a removed class → CS0246. It must be
        // skipped too. A category on a kept class is unaffected.
        var module = new ObjCModule
        {
            ModuleName = "Quick",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSString"] = "", ["NSObject"] = "" },
            Classes =
            [
                new ObjCClassDecl { Name = "QuickSpec", SuperclassName = "XCTestCase" },
                new ObjCClassDecl { Name = "QuickConfiguration", SuperclassName = "NSObject" }
            ],
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "QuickSpec",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "doExtra",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true
                    }]
                },
                new ObjCCategoryDecl
                {
                    CategoryName = "Helpers",
                    ClassName = "QuickConfiguration",
                    Methods = [new ObjCMethodDecl
                    {
                        Selector = "help",
                        ReturnType = new ObjCTypeRef { Name = "void" },
                        IsInstanceMethod = true
                    }]
                }
            ]
        };

        var result = EmitAndRead(module);
        // Category on the dropped class is skipped.
        Assert.DoesNotContain("QuickSpec_Extras", result);
        // Category on the kept class survives.
        Assert.Contains("QuickConfiguration_Helpers", result);
    }

    [Fact]
    public void Emit_ClassConformingToUnresolvableProtocol_DropsThatConformance()
    {
        // A class may conform to a mix of a bindable Apple protocol (NSCoding) and a third-party
        // protocol with no .NET binding. Emitting `: IThirdPartyProto` would fail with CS0246, so
        // only the resolvable conformance is kept.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSObject"] = "", ["NSCoding"] = "" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    SuperclassName = "NSObject",
                    ProtocolNames = ["NSCoding", "ThirdPartyProto"]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface MyClass : INSCoding", result);
        Assert.DoesNotContain("IThirdPartyProto", result);
    }

    [Fact]
    public void Emit_ProtocolInheritingUnresolvableProtocol_DropsThatInheritance()
    {
        // Same gate on the protocol inheritance list: an inherited third-party protocol with no
        // .NET binding is dropped, the resolvable one is kept.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSObject"] = "", ["NSCoding"] = "" },
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "MyDelegate",
                    InheritedProtocolNames = ["NSCoding", "ThirdPartyBase"]
                }
            ]
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface MyDelegate : INSCoding", result);
        Assert.DoesNotContain("IThirdPartyBase", result);
    }

    // ──────────────────────────────────────────────
    // Protocol naming is decided at the emission source (threaded localProtocolNames
    // → MapType + the conformance/inheritance lists), NOT by a blunt whole-file IFoo→Foo
    // regex post-process. Spelling is POSITIONAL for an own protocol:
    //   * DECLARATION and inheritance/conformance lists use the BARE name — bgen
    //     synthesizes its `IFoo` interface from the bare `[Protocol] interface Foo` (and,
    //     for [Model] protocols, the `Foo` Model class), and converts a bare conformance
    //     to `: IFoo` in its output.
    //   * MEMBER types (parameter/return/property) use the INTERFACE `IFoo`, resolved
    //     against the empty `interface IFoo {}` forward declaration the emitter writes per
    //     own protocol — binding a member to the bare name makes bgen pick the Model class
    //     and a conforming subclass throws InvalidCastException at runtime.
    // A protocol from the platform SDK keeps its `I` prefix everywhere (its interface
    // already ships there). Delegate protocols are a subset of own protocols; the only
    // delegate-specific spelling is the strongly-typed `[Wrap]` delegate PROPERTY, which
    // targets the Model class so consumers can subclass it.
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ClassConformingToLocalDelegateProtocol_UsesBareNameInConformanceList()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MyViewDelegate", IsDelegateProtocol = true }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                ProtocolNames = ["MyViewDelegate"],
            }],
        };

        var result = EmitAndRead(module);
        // The class conformance list must reference the [Model] protocol by its bare
        // (class) name — the Xamarin convention — not the I-prefixed interface form.
        Assert.Contains("partial interface MyView : MyViewDelegate", result);
        Assert.DoesNotContain(": IMyViewDelegate", result);
    }

    [Fact]
    public void Emit_ClassConformingToLocalNonDelegateProtocol_UsesBareNameInConformanceList()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "Configurable", IsDelegateProtocol = false }],
            Classes = [new ObjCClassDecl
            {
                Name = "Widget",
                ProtocolNames = ["Configurable"],
            }],
        };

        var result = EmitAndRead(module);
        // B2: a non-delegate protocol declared in THIS binding is still referenced bare in
        // the conformance list — bgen synthesizes its `IConfigurable`, and the contract
        // compile only sees the bare `[Protocol] interface Configurable`. An `IConfigurable`
        // reference would be undefined (CS0246).
        Assert.Contains("partial interface Widget : Configurable", result);
        Assert.DoesNotContain(": IConfigurable", result);
    }

    [Fact]
    public void Emit_ProtocolInheritingLocalDelegateProtocol_UsesBareNameInInheritanceList()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl { Name = "BaseViewDelegate", IsDelegateProtocol = true },
                new ObjCProtocolDecl
                {
                    Name = "ExtendedViewDelegate",
                    IsDelegateProtocol = true,
                    InheritedProtocolNames = ["BaseViewDelegate"],
                },
            ],
        };

        var result = EmitAndRead(module);
        // Both are [Model] delegate protocols → both emitted bare, including in the
        // inheritance list of the deriving protocol.
        Assert.Contains("partial interface ExtendedViewDelegate : BaseViewDelegate", result);
        Assert.DoesNotContain(": IBaseViewDelegate", result);
    }

    [Fact]
    public void Emit_MethodParameterTypedAsLocalDelegateProtocol_UsesInterface()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MyViewDelegate", IsDelegateProtocol = true }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "registerHandler:",
                        ReturnType = SimpleType("void"),
                        IsInstanceMethod = true,
                        Parameters =
                        [
                            new ObjCParameterDecl
                            {
                                Name = "handler",
                                Type = new ObjCTypeRef { Name = "id", IsPointer = true, ProtocolQualifications = ["MyViewDelegate"] },
                            },
                        ],
                    },
                ],
            }],
        };

        var result = EmitAndRead(module);
        // A protocol-typed parameter (MapType step 3) binds to the INTERFACE `IMyViewDelegate`,
        // even for a [Model] delegate protocol — so any conforming object can be passed. Binding
        // it to the bare Model class would InvalidCast a conforming non-subclass at runtime. (The
        // Model-class spelling is reserved for the strongly-typed [Wrap] delegate property.)
        Assert.Contains("RegisterHandler(IMyViewDelegate handler)", result);
        Assert.DoesNotContain("(MyViewDelegate handler)", result);
    }

    [Fact]
    public void Emit_DocCommentMentioningInterfaceName_NotRewritten_NoWholeFileRegex()
    {
        // Regression for the removed whole-file IFoo→Foo regex: it rewrote EVERY
        // \bIMyViewDelegate\b occurrence, including prose inside doc comments. With the
        // decision moved to the emission source, an unrelated mention of the I-prefixed
        // name in a doc comment survives verbatim.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MyViewDelegate", IsDelegateProtocol = true }],
            Classes = [new ObjCClassDecl
            {
                Name = "MyView",
                DocComment = "Prefer the IMyViewDelegate interface for testing.",
            }],
        };

        var result = EmitAndRead(module);
        // The doc-comment text is preserved — the old regex would have corrupted it to
        // "MyViewDelegate interface".
        Assert.Contains("IMyViewDelegate interface for testing", result);
    }

    // ──────────────────────────────────────────────
    // Protocol bare-name declaration (no double-I prefix)
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_NonDelegateProtocol_DeclaredBareName_NotDoubleIPrefixed()
    {
        // bgen derives the consumer-facing `IFoo` interface (and, for [Model] protocols, the `Foo`
        // Model class) from a protocol declared as `partial interface Foo`. Declaring it as `IFoo`
        // here makes bgen emit `IIFoo` plus an orphan `Foo`, which surfaced as an
        // InvalidCastException at runtime. The emitter must declare the BARE name and let bgen apply
        // its single "I" prefix.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl { Name = "MLNFeature" }],
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface MLNFeature", result);
        Assert.DoesNotContain("partial interface IMLNFeature", result);
    }

    // ──────────────────────────────────────────────
    // Method/property selector + name de-duplication
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_MethodSelectorMatchingPropertySetter_DropsMethod_KeepsProperty()
    {
        // A read-write `URL` property emits its setter as [Export("setURL:")]. An explicit
        // `- (void)setURL:` method would emit the SAME [Export("setURL:")], registering one ObjC
        // selector twice and aborting the runtime registrar at launch. The property is kept; the
        // duplicate-selector method is dropped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MLNImageSource",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "URL",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                }],
                Methods = [new ObjCMethodDecl
                {
                    Selector = "setURL:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl { Name = "url", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }],
                }],
            }],
        };

        var result = EmitAndRead(module);
        var setterExports = System.Text.RegularExpressions.Regex.Matches(result, @"\[Export\(""setURL:""\)\]").Count;
        Assert.Equal(1, setterExports);                       // only the property's setter
        Assert.DoesNotContain("void SetURL", result);         // the explicit method is gone
        Assert.Contains("[Export(\"URL\")]", result);         // the property survives
    }

    [Fact]
    public void Emit_StaticMethodSelectorMatchingInstancePropertyAccessor_NotDropped()
    {
        // The de-dup is keyed on instance/class kind: a CLASS method `URL` and an INSTANCE property
        // `URL` dispatch through separate ObjC method lists, so they do NOT collide and the method
        // must be kept (over-dropping would silently lose a real API).
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MLNThing",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "url",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                }],
                Methods = [new ObjCMethodDecl
                {
                    Selector = "url",
                    ReturnType = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsInstanceMethod = false,   // class (static) method
                }],
            }],
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Static]", result);                  // the class method is still emitted
    }

    [Fact]
    public void Emit_MethodNameCollidingWithProperty_RenamesMethod_KeepsProperty()
    {
        // A `camera` property (C# `Camera`) and a method `camera:fittingCoordinateBounds:` whose
        // synthesized short name is also `Camera` (different selector, same C# name). Methods emit
        // before properties, so before the pre-seed the method claimed `Camera` and the property
        // was silently dropped (the bgen-flattened output would CS0102). The method must rename to
        // its full selector and the property must survive.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MLNMapView",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "camera",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                }],
                Methods = [new ObjCMethodDecl
                {
                    Selector = "camera:fittingCoordinateBounds:",
                    ReturnType = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsInstanceMethod = true,
                    Parameters =
                    [
                        new ObjCParameterDecl { Name = "camera", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } },
                        new ObjCParameterDecl { Name = "bounds", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } },
                    ],
                }],
            }],
        };

        var result = EmitAndRead(module);
        Assert.Contains("CameraFittingCoordinateBounds", result);                 // method renamed
        Assert.Contains("[Export(\"camera:fittingCoordinateBounds:\")]", result); // export preserved
        Assert.Contains("Camera {", result);                                      // property survives
    }

    // ──────────────────────────────────────────────
    // Shape A — protocol init requirement vs synthesized Model default ctor
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ProtocolWithParameterlessInitRequirement_HasDisableDefaultCtor()
    {
        // A [Protocol] that declares a parameterless `init` requirement otherwise registers `init`
        // twice on bgen's concrete adapter type — once for the synthesized default ctor, once for the
        // abstract requirement re-emitted as a method — aborting the .NET registrar at launch, and the
        // re-emitted method compiles to `public virtual NSObject Init()` that hides NSObject.Init()
        // (CS0108). Fully mirroring EmitClass's parameterless-init handling resolves both: emit
        // [DisableDefaultCtor] (suppress the ctor) AND drop the parameterless `init` method.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Configuring",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "init",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    IsOptional = false,
                }],
            }],
        };

        var result = EmitAndRead(module);
        Assert.Contains("[DisableDefaultCtor]", result);
        // Neither member carries `init`: the ctor is suppressed by [DisableDefaultCtor] and the
        // parameterless `init` method is dropped, so no duplicate selector and no NSObject.Init() shadow.
        var initExports = System.Text.RegularExpressions.Regex.Matches(result, @"\[Export\(""init""\)\]").Count;
        Assert.Equal(0, initExports);
    }

    [Fact]
    public void Emit_ProtocolWithParameterizedInitRequirement_NoDisableDefaultCtor()
    {
        // A parameterized `initWithConfig:` requirement does NOT collide with the synthesized
        // parameterless `init` ctor (different selector), so [DisableDefaultCtor] must NOT be emitted
        // — over-emitting it would needlessly strip the Model's default ctor.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Configuring",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "initWithConfig:",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    IsOptional = false,
                    Parameters = [new ObjCParameterDecl { Name = "config", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }],
                }],
            }],
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[DisableDefaultCtor]", result);
    }

    [Fact]
    public void Emit_DelegateProtocolWithParameterlessInitRequirement_ModelAndDisableDefaultCtor()
    {
        // The actually-colliding Shape A at runtime: a delegate protocol gets [Model], so bgen emits a
        // concrete Model class whose synthesized default ctor exports `init` — colliding with the
        // abstract `init` requirement. [DisableDefaultCtor] must compose with [Model] to suppress that
        // ctor, and the parameterless `init` method must be dropped (full EmitClass mirror).
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "ConfigDelegate",
                IsDelegateProtocol = true,
                Methods = [new ObjCMethodDecl
                {
                    Selector = "init",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    IsOptional = false,
                }],
            }],
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Protocol, Model]", result);
        Assert.Contains("[DisableDefaultCtor]", result);
        var initExports = System.Text.RegularExpressions.Regex.Matches(result, @"\[Export\(""init""\)\]").Count;
        Assert.Equal(0, initExports);
    }

    [Fact]
    public void Emit_ProtocolWithOnlyParameterlessInitRequirement_EmitsEmptyInterfaceWithDisableDefaultCtor()
    {
        // Degenerate Shape A: the protocol's ONLY member is the parameterless `init`. Dropping it
        // leaves a valid empty marker interface (bgen supports empty protocols); [DisableDefaultCtor]
        // is still emitted, and no `init` selector and no NSObject.Init() shadow survive.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Marker",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "init",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    IsOptional = false,
                }],
            }],
        };

        var result = EmitAndRead(module);
        Assert.Contains("partial interface Marker", result);
        Assert.Contains("[DisableDefaultCtor]", result);
        var initExports = System.Text.RegularExpressions.Regex.Matches(result, @"\[Export\(""init""\)\]").Count;
        Assert.Equal(0, initExports);
    }

    [Fact]
    public void Emit_ProtocolWithParameterlessInitWithNoColon_HasDisableDefaultCtor_KeepsMethod()
    {
        // A 0-parameter `initWith…` (no colon, e.g. `initWithDefaults`) exports a selector DISTINCT
        // from `init`, so it never collides with the synthesized default ctor and must STAY emitted —
        // but it still triggers [DisableDefaultCtor] (mirrors EmitClass, which keys Disable off any
        // parameterless init-family selector). Only the bare `init` selector is ever dropped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Configuring",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "initWithDefaults",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    IsOptional = false,
                }],
            }],
        };

        var result = EmitAndRead(module);
        Assert.Contains("[DisableDefaultCtor]", result);
        Assert.Contains("[Export(\"initWithDefaults\")]", result);  // distinct selector → method kept
    }

    [Fact]
    public void Emit_ProtocolWithParameterlessInitRequirement_RecordsDuplicateSelectorSkip()
    {
        // The Shape A drop must be observable as a DuplicateSelector diagnostic on the `init` method,
        // so a regression that loses the method through a different path is still distinguishable.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Configuring",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "init",
                    ReturnType = new ObjCTypeRef { Name = "instancetype" },
                    IsInstanceMethod = true,
                    IsOptional = false,
                }],
            }],
        };

        var (_, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        Assert.Contains(diagnostics.SkippedSymbols,
            s => s.Reason == ObjCSkipReason.DuplicateSelector && s.SymbolKind == "Method" && s.SymbolName == "init");
    }

    // ──────────────────────────────────────────────
    // Shape B — class method vs flattened conformed-protocol property accessor
    // ──────────────────────────────────────────────

    [Fact]
    public void Emit_ClassMethodMatchingConformedProtocolPropertySetter_DropsMethod_KeepsProperty()
    {
        // A class conforms to a [Protocol] that declares a settable required property whose setter
        // selector equals a method the class also declares. The registrar flattens the conforming
        // class's required protocol members onto the class, so the class would register the setter
        // selector twice (the flattened property setter + the method) and abort at launch. The method
        // must be dropped in favour of the flattened property accessor.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Requesting",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "errorRecoveryDisabled",
                    Type = new ObjCTypeRef { Name = "BOOL" },
                    IsReadonly = false,
                    IsOptional = false,
                }],
            }],
            Classes = [new ObjCClassDecl
            {
                Name = "Request",
                ProtocolNames = ["Requesting"],
                Methods = [new ObjCMethodDecl
                {
                    Selector = "setErrorRecoveryDisabled:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl { Name = "disable", Type = new ObjCTypeRef { Name = "BOOL" } }],
                }],
            }],
        };

        var result = EmitAndRead(module);
        // The flattened property setter export survives exactly once (on the protocol); the class
        // method that re-exported the same selector is gone.
        var setterExports = System.Text.RegularExpressions.Regex.Matches(result, @"\[Export\(""setErrorRecoveryDisabled:""\)\]").Count;
        Assert.Equal(1, setterExports);
        Assert.DoesNotContain("void SetErrorRecoveryDisabled", result);
    }

    [Fact]
    public void Emit_ClassMethodMatchingTransitiveProtocolPropertySetter_DropsMethod()
    {
        // Transitive flattening: the class conforms to protocol B, which inherits protocol A; A's
        // required settable property is flattened onto the class too, so a class method whose
        // selector equals A's setter must also be dropped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "BaseRequesting",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "errorRecoveryDisabled",
                        Type = new ObjCTypeRef { Name = "BOOL" },
                        IsReadonly = false,
                        IsOptional = false,
                    }],
                },
                new ObjCProtocolDecl
                {
                    Name = "Requesting",
                    InheritedProtocolNames = ["BaseRequesting"],
                },
            ],
            Classes = [new ObjCClassDecl
            {
                Name = "Request",
                ProtocolNames = ["Requesting"],
                Methods = [new ObjCMethodDecl
                {
                    Selector = "setErrorRecoveryDisabled:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl { Name = "disable", Type = new ObjCTypeRef { Name = "BOOL" } }],
                }],
            }],
        };

        var result = EmitAndRead(module);
        var setterExports = System.Text.RegularExpressions.Regex.Matches(result, @"\[Export\(""setErrorRecoveryDisabled:""\)\]").Count;
        Assert.Equal(1, setterExports);
        Assert.DoesNotContain("void SetErrorRecoveryDisabled", result);
    }

    [Fact]
    public void Emit_ClassMethodMatchingOptionalProtocolPropertySetter_NotDropped()
    {
        // An OPTIONAL protocol property is reached through the generated interface's extension
        // methods, never registered on a conforming class, so its setter selector does NOT collide
        // with a class method. Over-dropping here would silently lose a real class API.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Requesting",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "errorRecoveryDisabled",
                    Type = new ObjCTypeRef { Name = "BOOL" },
                    IsReadonly = false,
                    IsOptional = true,   // optional → not flattened onto the conforming class
                }],
            }],
            Classes = [new ObjCClassDecl
            {
                Name = "Request",
                ProtocolNames = ["Requesting"],
                Methods = [new ObjCMethodDecl
                {
                    Selector = "setErrorRecoveryDisabled:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl { Name = "disable", Type = new ObjCTypeRef { Name = "BOOL" } }],
                }],
            }],
        };

        var result = EmitAndRead(module);
        Assert.Contains("void SetErrorRecoveryDisabled", result);  // class method survives
    }

    [Fact]
    public void Emit_ClassStaticMethodMatchingInheritedInstancePropertyAccessor_NotDropped()
    {
        // Instance/class kind separation extends to inherited accessors: a CLASS (static) method
        // whose selector equals an INSTANCE property accessor flattened from a conformed protocol
        // dispatches through a separate ObjC method list and must NOT be dropped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Requesting",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "token",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                    IsOptional = false,
                }],
            }],
            Classes = [new ObjCClassDecl
            {
                Name = "Request",
                ProtocolNames = ["Requesting"],
                Methods = [new ObjCMethodDecl
                {
                    Selector = "token",
                    ReturnType = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsInstanceMethod = false,   // class (static) method
                }],
            }],
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Static]", result);   // the class method is still emitted
    }

    [Fact]
    public void Emit_ClassStaticMethodMatchingConformedProtocolClassPropertySetter_DropsMethod()
    {
        // The class-namespace counterpart of the instance drop: a CLASS (static) method whose selector
        // equals a settable CLASS property accessor flattened from a conformed protocol collides within
        // the metaclass method list and must be dropped in favour of the flattened class-property setter.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Configuring",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "config",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsClass = true,
                    IsReadonly = false,
                    IsOptional = false,
                }],
            }],
            Classes = [new ObjCClassDecl
            {
                Name = "Manager",
                ProtocolNames = ["Configuring"],
                Methods = [new ObjCMethodDecl
                {
                    Selector = "setConfig:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = false,   // class (static) method — same kind as the class property
                    Parameters = [new ObjCParameterDecl { Name = "config", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }],
                }],
            }],
        };

        var result = EmitAndRead(module);
        // Exactly one `setConfig:` export (the flattened class-property setter); the class method is gone.
        var setterExports = System.Text.RegularExpressions.Regex.Matches(result, @"\[Export\(""setConfig:""\)\]").Count;
        Assert.Equal(1, setterExports);
        Assert.DoesNotContain("void SetConfig", result);
    }

    [Fact]
    public void Emit_ClassMethodMatchingConformedProtocolPropertySetter_RecordsDuplicateSelectorSkip()
    {
        // The Shape B drop must be observable as a DuplicateSelector diagnostic on the colliding method.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "Requesting",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "errorRecoveryDisabled",
                    Type = new ObjCTypeRef { Name = "BOOL" },
                    IsReadonly = false,
                    IsOptional = false,
                }],
            }],
            Classes = [new ObjCClassDecl
            {
                Name = "Request",
                ProtocolNames = ["Requesting"],
                Methods = [new ObjCMethodDecl
                {
                    Selector = "setErrorRecoveryDisabled:",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Parameters = [new ObjCParameterDecl { Name = "disable", Type = new ObjCTypeRef { Name = "BOOL" } }],
                }],
            }],
        };

        var (_, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        Assert.Contains(diagnostics.SkippedSymbols,
            s => s.Reason == ObjCSkipReason.DuplicateSelector && s.SymbolKind == "Method" && s.SymbolName == "setErrorRecoveryDisabled:");
    }

    [Fact]
    public void Emit_MemberTypedByBareLocalAcronymProtocol_SurvivesTheResolvabilityGate()
    {
        // The resolvability gate re-maps a member's type to decide whether to emit it, and that
        // check MUST use the same inputs as the emission it is predicting. A member typed by the
        // BARE name of a locally declared protocol maps to the protocol INTERFACE, but only when
        // `localProtocolNames` is supplied; without it the mapping falls through to the plain
        // .NET-acronym fallback. For a protocol whose acronym-normalized spelling differs from its
        // raw one (NSURL* -> NSUrl*), those two answers are different STRINGS: the check asks about
        // `NSUrlThing` (which is in neither knownTypes nor the Apple SDK set) while emission would
        // have written the perfectly resolvable `INSUrlThing`. The member was dropped with only a
        // debug log — no diagnostic, no compile error, just a missing binding.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            // Non-null (and non-empty) => the "AST-expanded SDK types" path, which returns false for
            // an unknown name instead of accepting anything with a known Apple prefix.
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSObject"] = "" },
            Protocols = [new ObjCProtocolDecl { Name = "NSURLThing" }],
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "consume:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters =
                            [
                                new ObjCParameterDecl
                                {
                                    Name = "thing",
                                    Type = new ObjCTypeRef { Name = "NSURLThing", IsPointer = true }
                                }
                            ]
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "produce",
                            ReturnType = new ObjCTypeRef { Name = "NSURLThing", IsPointer = true },
                            IsInstanceMethod = true
                        }
                    ],
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "thing",
                            Type = new ObjCTypeRef { Name = "NSURLThing", IsPointer = true },
                            IsReadonly = true,
                            GetterSelector = "thing"
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);

        // The protocol is DECLARED under the same name every reference to it resolves to, so bgen
        // generates the `INSUrlThing` those references name. Name= keeps the native registration on
        // the ObjC spelling, so the rename is C#-side only.
        Assert.Contains("[Protocol(Name = \"NSURLThing\")]", result);
        Assert.Contains("partial interface NSUrlThing", result);
        Assert.DoesNotContain("partial interface NSURLThing", result);
        // Every member typed by it binds to that protocol's interface rather than vanishing. The
        // property already survived before the fix (its gate threaded localProtocolNames); the two
        // methods did not.
        Assert.Contains("Consume(INSUrlThing thing)", result);
        Assert.Contains("INSUrlThing Produce()", result);
        Assert.Contains("INSUrlThing Thing { get; }", result);
    }

    [Fact]
    public void Emit_LocalAcronymClass_DeclarationMatchesEveryReferenceToIt()
    {
        // The class-side half of the same declaration/reference agreement. A member typed by a
        // locally declared class maps through the .NET acronym convention (`NSURLBox` -> `NSUrlBox`),
        // so declaring the interface under the raw ObjC name left the member referencing a type that
        // does not exist in the file — CS0246 in the api-definition contract compile, which fails the
        // whole binding rather than dropping one member.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSObject"] = "" },
            Classes =
            [
                new ObjCClassDecl { Name = "NSURLBox" },
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "box",
                            Type = new ObjCTypeRef { Name = "NSURLBox", IsPointer = true },
                            IsReadonly = true,
                            GetterSelector = "box"
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);

        Assert.Contains("[BaseType(typeof(NSObject), Name = \"NSURLBox\")]", result);
        Assert.Contains("partial interface NSUrlBox", result);
        Assert.DoesNotContain("partial interface NSURLBox", result);
        Assert.Contains("NSUrlBox Box { get; }", result);
    }

    [Fact]
    public void Emit_InstancetypeOnAcronymRenamedClass_ResolvesToTheDeclarationName()
    {
        // `instancetype` maps to whatever the emitter passes as `declaringClassName`. Passing the raw
        // ObjC name there while the interface is declared under the acronym-mapped one produces a
        // method returning a type the file never declares — CS0246, which fails the whole binding.
        // The resolvability gate cannot catch it: both spellings are seeded into knownTypes.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSObject"] = "" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "NSURLBox",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "sharedBox",
                            ReturnType = new ObjCTypeRef { Name = "instancetype" }
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "copyBox",
                            IsInstanceMethod = true,
                            ReturnType = new ObjCTypeRef { Name = "instancetype" }
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);

        Assert.Contains("partial interface NSUrlBox", result);
        Assert.Contains("NSUrlBox SharedBox", result);
        Assert.Contains("NSUrlBox CopyBox", result);
        Assert.DoesNotContain("NSURLBox SharedBox", result);
        Assert.DoesNotContain("NSURLBox CopyBox", result);
    }

    [Fact]
    public void Emit_CategoryOnAcronymRenamedClass_UsesTheMappedNameEverywhere()
    {
        // Same agreement on the category path: the [BaseType] target, the generated extension
        // interface name, and `instancetype` all name the extended class, so all three must use the
        // spelling the class declaration carries.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSObject"] = "" },
            Classes = [new ObjCClassDecl { Name = "NSURLBox" }],
            Categories =
            [
                new ObjCCategoryDecl
                {
                    ClassName = "NSURLBox",
                    CategoryName = "Extras",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "makeBox",
                            IsInstanceMethod = true,
                            ReturnType = new ObjCTypeRef { Name = "instancetype" }
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);

        Assert.Contains("[BaseType(typeof(NSUrlBox))]", result);
        Assert.Contains("partial interface NSUrlBox_Extras", result);
        Assert.Contains("NSUrlBox MakeBox", result);
        Assert.DoesNotContain("NSURLBox", result.Replace("Name = \"NSURLBox\"", ""));
    }

    [Fact]
    public void Emit_WeakDelegateWrapProperty_TypedByTheDelegateProtocolDeclarationName()
    {
        // The [Wrap] half of the WeakDelegate pattern is typed by the [Model] class bgen generates
        // from the protocol declaration, so it must carry the declaration's spelling. Keeping the raw
        // ObjC name for everything except a class/protocol clash left an acronym-renamed delegate
        // protocol declared `NSUrlThingDelegate` while the property was typed `NSURLThingDelegate`.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSObject"] = "" },
            Protocols =
            [
                new ObjCProtocolDecl { Name = "NSURLThingDelegate", IsDelegateProtocol = true }
            ],
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "MyClass",
                    Properties =
                    [
                        // `id<Proto>` is the shape clang actually produces for a delegate property —
                        // a protocol name is not a standalone pointee — so it carries the protocol in
                        // ProtocolQualifications. The bare-pointer form below is the other branch
                        // ResolveDelegateProtocolName accepts; both must land on the same spelling.
                        new ObjCPropertyDecl
                        {
                            Name = "delegate",
                            Type = new ObjCTypeRef { Name = "id", ProtocolQualifications = ["NSURLThingDelegate"] },
                            GetterSelector = "delegate"
                        },
                        new ObjCPropertyDecl
                        {
                            Name = "secondaryDelegate",
                            Type = new ObjCTypeRef { Name = "NSURLThingDelegate", IsPointer = true },
                            GetterSelector = "secondaryDelegate"
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);

        Assert.Contains("[Protocol(Name = \"NSURLThingDelegate\"), Model]", result);
        Assert.Contains("partial interface NSUrlThingDelegate", result);
        Assert.Contains("NSUrlThingDelegate Delegate", result);
        Assert.Contains("NSUrlThingDelegate SecondaryDelegate", result);
        Assert.DoesNotContain("NSURLThingDelegate Delegate", result);
        Assert.DoesNotContain("NSURLThingDelegate SecondaryDelegate", result);
    }

    [Fact]
    public void Emit_ChildProtocol_DropsAPropertyTheParentAlreadyEmitsWhenTypedByALocalAcronymProtocol()
    {
        // ComputeProtocolEmissionSet decides what an ancestor already occupies. Its property replay
        // used to re-run the resolvability check WITHOUT the local-protocol sets, so a parent property
        // typed by a bare local acronym protocol mapped to a name absent from knownTypes, failed, and
        // never entered MemberNames — leaving the child free to re-emit a name the parent owns, which
        // bgen flattens onto one interface.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["NSObject"] = "" },
            Protocols =
            [
                new ObjCProtocolDecl { Name = "NSURLThing" },
                new ObjCProtocolDecl
                {
                    Name = "Parent",
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "thing",
                            Type = new ObjCTypeRef { Name = "NSURLThing", IsPointer = true },
                            IsReadonly = true,
                            GetterSelector = "thing"
                        }
                    ]
                },
                new ObjCProtocolDecl
                {
                    Name = "Child",
                    InheritedProtocolNames = ["Parent"],
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "thing",
                            Type = new ObjCTypeRef { Name = "NSURLThing", IsPointer = true },
                            IsReadonly = true,
                            GetterSelector = "thing"
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);

        // The parent owns the member; the child inherits it rather than redeclaring it.
        Assert.Equal(1, CountOccurrences(result, "INSUrlThing Thing { get; }"));
    }

    // --- Category instance-property projection ---

    [Fact]
    public void Emit_CategoryWithMethodsAndInstanceProperties_ProjectsPropertiesAsAccessorMethods()
    {
        // The shape that used to lose half a library's surface in silence: a boxing/unboxing
        // category whose boxing half is class METHODS (emitted) and whose unboxing half is readonly
        // instance PROPERTIES. The instance properties are projected as accessor methods so both
        // halves reach the consumer, and nothing is dropped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Boxing",
                    ClassName = "NSValue",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "valueForSpan:",
                            ReturnType = new ObjCTypeRef { Name = "NSValue", IsPointer = true },
                            Parameters = [new ObjCParameterDecl { Name = "span", Type = new ObjCTypeRef { Name = "double" } }]
                        }
                    ],
                    Properties =
                    [
                        new ObjCPropertyDecl { Name = "spanValue", Type = new ObjCTypeRef { Name = "double" }, IsReadonly = true },
                        new ObjCPropertyDecl { Name = "scaleValue", Type = new ObjCTypeRef { Name = "double" }, IsReadonly = true }
                    ]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        // Class-method half still emits.
        Assert.Contains("[Export(\"valueForSpan:\")]", result);
        // Instance-property half survives as accessor methods carrying the property's own selector.
        Assert.Contains("[Export(\"spanValue\")]", result);
        Assert.Contains("double GetSpanValue();", result);
        Assert.Contains("[Export(\"scaleValue\")]", result);
        Assert.Contains("double GetScaleValue();", result);
        // Never as instance properties — a bgen static extension class rejects those (CS0708).
        Assert.DoesNotContain("double SpanValue {", result);
        Assert.DoesNotContain("double ScaleValue {", result);
        Assert.Empty(diagnostics.SkippedSymbols);
    }

    [Fact]
    public void Emit_CategoryReadWriteInstanceProperty_ProjectsBothAccessors()
    {
        // A read-write category instance property loses nothing either: the setter projects as a
        // second accessor method exporting the property's real setter selector.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "Widget",
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "label",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                            GetterSelector = "extraLabel",
                            SetterSelector = "setExtraLabel:"
                        }
                    ]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Contains("[Export(\"extraLabel\")]", result);
        Assert.Contains("string GetLabel();", result);
        Assert.Contains("[Export(\"setExtraLabel:\")]", result);
        Assert.Contains("void SetLabel(string value);", result);
        Assert.Empty(diagnostics.SkippedSymbols);
    }

    [Fact]
    public void Emit_CategoryInstancePropertyWithUnresolvableType_RecordsSkipAndKeepsMethods()
    {
        // Fail-closed: an accessor that cannot be projected soundly must be visible in the binding
        // report rather than disappearing, and it must not take the category's methods with it.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "Widget",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "refresh",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true
                        }
                    ],
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "gadget",
                            Type = new ObjCTypeRef { Name = "ThirdPartyGadget", IsPointer = true },
                            IsReadonly = true
                        }
                    ]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Contains("void Refresh();", result);
        Assert.DoesNotContain("GetGadget", result);
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "Gadget");
        Assert.Equal("Property", skip.SymbolKind);
        Assert.Equal(ObjCSkipReason.UnresolvableType, skip.Reason);
    }

    [Fact]
    public void Emit_CategoryPointerValuedInstanceProperty_KeepsGetterAndRecordsSetterSkip()
    {
        // A pointer-to-value-type setter value would project as `out T`, which cannot carry the
        // value INTO the call. The setter half drops with a record; the getter half still emits.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "Widget",
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "cursor",
                            Type = new ObjCTypeRef { Name = "double", IsPointer = true }
                        }
                    ]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Contains("GetCursor();", result);
        Assert.DoesNotContain("SetCursor", result);
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "setCursor");
        Assert.Equal(ObjCSkipReason.UnsupportedConstruct, skip.Reason);
    }

    [Fact]
    public void Emit_CategoryProjectedAccessorNameCollidingWithMethod_EmitsDistinctMembers()
    {
        // The projected name goes through the same dedup as any other method name, so a category
        // method that already occupies `GetLabel` does not end up declared twice (CS0111).
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "Widget",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "getLabel",
                            ReturnType = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                            IsInstanceMethod = true
                        }
                    ],
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "label",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                            IsReadonly = true
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);

        // Both selectors survive, and no member signature is declared twice.
        Assert.Contains("[Export(\"getLabel\")]", result);
        Assert.Contains("[Export(\"label\")]", result);
        Assert.Equal(1, CountOccurrences(result, "string GetLabel();"));
    }

    [Fact]
    public void Emit_CategoryMethodSharingProjectedAccessorSelector_DropsMethodAndRecordsSkip()
    {
        // The projected accessors export the property's real ObjC selectors, so a category method
        // exporting one of them is the SAME selector registered twice — which aborts the .NET
        // registrar at launch. The method is dropped in favour of the property, exactly as on the
        // class path.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "Widget",
                    Methods =
                    [
                        new ObjCMethodDecl
                        {
                            Selector = "tintColor",
                            ReturnType = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                            IsInstanceMethod = true
                        },
                        new ObjCMethodDecl
                        {
                            Selector = "setTintColor:",
                            ReturnType = new ObjCTypeRef { Name = "void" },
                            IsInstanceMethod = true,
                            Parameters = [new ObjCParameterDecl { Name = "color", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }]
                        }
                    ],
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "tintColor",
                            Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }
                        }
                    ]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        // Each selector is exported exactly once — by the projected accessor, not the method.
        Assert.Equal(1, CountOccurrences(result, "[Export(\"tintColor\")]"));
        Assert.Equal(1, CountOccurrences(result, "[Export(\"setTintColor:\")]"));
        Assert.Contains("GetTintColor();", result);
        Assert.Contains("SetTintColor(", result);
        Assert.Equal(2, diagnostics.SkippedSymbols.Count(s => s.SymbolKind == "Method" && s.Reason == ObjCSkipReason.DuplicateSelector));
    }

    [Fact]
    public void Emit_InstanceOnlyCategoryWithAllPropertiesUnresolvable_RecordsEmptyCategory()
    {
        // Nothing is projectable, so the category has no content at all — it must record
        // EmptyCategory rather than opening an empty [Category] shell.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Categories =
            [
                new ObjCCategoryDecl
                {
                    CategoryName = "Extras",
                    ClassName = "Widget",
                    Properties =
                    [
                        new ObjCPropertyDecl
                        {
                            Name = "helper",
                            Type = new ObjCTypeRef { Name = "MysteryHelper", IsPointer = true },
                            IsReadonly = true
                        }
                    ]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.DoesNotContain("Widget_Extras", result);
        Assert.Contains(diagnostics.SkippedSymbols, s => s.SymbolKind == "Category" && s.Reason == ObjCSkipReason.EmptyCategory);
    }

    // --- Superclass-chain property re-declaration ---

    [Fact]
    public void Emit_SubclassRedeclaresIdenticalInheritedProperty_DefersToInheritedMember()
    {
        // ObjC headers routinely re-declare an inherited property to restate a protocol
        // conformance. bgen generates a real C# class deriving from its [BaseType], so re-emitting
        // the member hides the inherited one (CS0108 in every consumer build). An identical
        // re-declaration adds nothing, so the inherited member is kept and the copy is recorded.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "title", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                },
                new ObjCClassDecl
                {
                    Name = "DerivedShape",
                    SuperclassName = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "title", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Equal(1, CountOccurrences(result, "string Title { get; }"));
        Assert.DoesNotContain("[New]", result);
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "Title");
        Assert.Equal("Property", skip.SymbolKind);
        Assert.Contains("BaseShape", skip.Detail);
    }

    [Fact]
    public void Emit_SubclassNarrowsInheritedReadWriteProperty_DefersToWiderInheritedMember()
    {
        // The live regression: headers re-declare an inherited read-write property read-only to
        // restate a protocol conformance. Emitting that copy hides the base member and makes the
        // setter unreachable through a subclass-typed variable. The widest surface wins — here the
        // base already IS the widest, so the subclass keeps inheriting it and nothing is hidden.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "BaseShape",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "title",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        SetterSelector = "setDisplayTitle:"
                    }]
                },
                new ObjCClassDecl
                {
                    Name = "DerivedShape",
                    SuperclassName = "BaseShape",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "title",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        IsReadonly = true
                    }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        // Exactly one Title member survives — the base's, still read-write on its own selector —
        // so nothing is hidden (no CS0108) and the setter stays reachable from the subclass.
        Assert.Equal(1, CountOccurrences(result, "[Export(\"setDisplayTitle:\")] set;"));
        Assert.DoesNotContain("[New]", result);
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "Title");
        Assert.Contains("BaseShape", skip.Detail);
    }

    [Fact]
    public void Emit_SubclassRetypedReadonlyRedeclaration_EmitsNewAndRecordsUnprojectableSetter()
    {
        // A re-declaration that changes the bound type has to emit, and it hides a read-write
        // ancestor member. The inherited setter CANNOT be re-exported here: it takes the ancestor's
        // type, so sending its selector with this member's type would be a message the ancestor's
        // implementation cannot decode. The accessor is recorded as lost instead.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "BaseShape",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "tag",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        SetterSelector = "setDisplayTag:"
                    }]
                },
                new ObjCClassDecl
                {
                    Name = "DerivedShape",
                    SuperclassName = "BaseShape",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "tag",
                        Type = new ObjCTypeRef { Name = "NSNumber", IsPointer = true },
                        IsReadonly = true
                    }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Contains("[New]", result);
        Assert.Contains("NSNumber Tag { get; }", result);
        // Only the base member exports the setter — the retyped re-declaration does not claim it.
        Assert.Equal(1, CountOccurrences(result, "[Export(\"setDisplayTag:\")] set;"));
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "setTag");
        Assert.Equal(ObjCSkipReason.UnsupportedConstruct, skip.Reason);
        Assert.Contains("BaseShape", skip.Detail);
    }

    [Fact]
    public void Emit_SubclassWidensInheritedReadonlyProperty_EmitsNewWithSetter()
    {
        // The mirror case: the base is read-only and the subclass adds a setter. The subclass
        // member is genuinely wider, so it emits — marked [New] because it hides the base member.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "title", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                },
                new ObjCClassDecl
                {
                    Name = "DerivedShape",
                    SuperclassName = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "title", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }]
                }
            ]
        };

        var result = EmitAndRead(module);

        Assert.Contains("[New]", result);
        Assert.Contains("[Export(\"setTitle:\")] set;", result);
        // The hidden member belongs to the generated BASE CLASS, which an ApiDefinition interface
        // reaches through [BaseType] and not through interface inheritance — so the declaration must
        // NOT carry the C# `new` keyword here (nothing to hide at this level: CS0109).
        Assert.DoesNotContain("new string Title", result);
    }

    [Fact]
    public void Emit_SubclassRedeclaresInheritedPropertyWithDifferentType_EmitsNew()
    {
        // A re-declaration that changes the bound type is not a duplicate — it must emit, and it
        // must announce the hiding.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "tag", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                },
                new ObjCClassDecl
                {
                    Name = "DerivedShape",
                    SuperclassName = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "tag", Type = new ObjCTypeRef { Name = "NSNumber", IsPointer = true }, IsReadonly = true }]
                }
            ]
        };

        var result = EmitAndRead(module);

        Assert.Contains("string Tag { get; }", result);
        Assert.Contains("[New]", result);
        Assert.Contains("NSNumber Tag { get; }", result);
    }

    [Fact]
    public void Emit_GrandparentPropertyRedeclaration_DefersToInheritedMember()
    {
        // The walk is transitive: the member may come from any ancestor, not just the direct base.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "RootShape",
                    Properties = [new ObjCPropertyDecl { Name = "title", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                },
                new ObjCClassDecl { Name = "MiddleShape", SuperclassName = "RootShape" },
                new ObjCClassDecl
                {
                    Name = "LeafShape",
                    SuperclassName = "MiddleShape",
                    Properties = [new ObjCPropertyDecl { Name = "title", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Equal(1, CountOccurrences(result, "string Title { get; }"));
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "Title");
        Assert.Contains("RootShape", skip.Detail);
    }

    [Fact]
    public void Emit_SubclassOfForeignSuperclass_EmitsPropertyUnchanged()
    {
        // A superclass outside this module exposes no members the walk can see, so the emitter
        // degrades to plain emission rather than guessing what the foreign base declares.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNamespaces = new Dictionary<string, string> { ["UIView"] = "UIKit" },
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "DerivedView",
                    SuperclassName = "UIView",
                    Properties = [new ObjCPropertyDecl { Name = "title", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Contains("string Title { get; }", result);
        Assert.DoesNotContain("[New]", result);
        Assert.Empty(diagnostics.SkippedSymbols);
    }

    [Fact]
    public void Emit_SubclassRedeclaresIdenticalDelegateProperty_DefersToInheritedPair()
    {
        // A delegate property emits as a [Wrap] + weak pair. An identical re-declaration adds
        // nothing, so — exactly as for a plain property — the inherited pair is kept rather than
        // hidden by a second copy.
        var module = BuildDelegateRedeclarationModule(baseIsReadonly: false, derivedIsReadonly: false);

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.DoesNotContain("[New]", result);
        Assert.Equal(1, CountOccurrences(result, "ShapeDelegate Delegate {"));
        Assert.Equal(1, CountOccurrences(result, "NSObject WeakDelegate {"));
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "Delegate");
        Assert.Contains("BaseShape", skip.Detail);
    }

    [Fact]
    public void Emit_SubclassNarrowsInheritedDelegateProperty_DefersToWiderInheritedPair()
    {
        // The delegate path takes the same widest-surface rule as a plain property: a read-only
        // re-declaration over a read-write base must not hide the base's setters.
        var module = BuildDelegateRedeclarationModule(baseIsReadonly: false, derivedIsReadonly: true);

        var result = EmitAndRead(module);

        Assert.DoesNotContain("[New]", result);
        // The surviving pair is the base's, still writable.
        Assert.Equal(1, CountOccurrences(result, "ShapeDelegate Delegate { get; set; }"));
        Assert.Equal(1, CountOccurrences(result, "[Export(\"setDelegate:\")] set;"));
    }

    [Fact]
    public void Emit_SubclassWidensInheritedDelegateProperty_MarksBothHalvesNew()
    {
        // A genuinely wider re-declaration emits, hiding TWO base members — so both halves of the
        // pair need the `new` keyword.
        var module = BuildDelegateRedeclarationModule(baseIsReadonly: true, derivedIsReadonly: false);

        var result = EmitAndRead(module);

        // One [New] for the [Wrap] property, one for its weak backing property.
        Assert.Equal(2, CountOccurrences(result, "[New]"));
        Assert.Contains("ShapeDelegate Delegate { get; set; }", result);
    }

    static ObjCModule BuildDelegateRedeclarationModule(bool baseIsReadonly, bool derivedIsReadonly) => new()
    {
        ModuleName = "Test",
        Protocols =
        [
            new ObjCProtocolDecl { Name = "ShapeDelegate", IsDelegateProtocol = true }
        ],
        Classes =
        [
            new ObjCClassDecl
            {
                Name = "BaseShape",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef { Name = "id", IsPointer = true, ProtocolQualifications = ["ShapeDelegate"] },
                    IsReadonly = baseIsReadonly
                }]
            },
            new ObjCClassDecl
            {
                Name = "DerivedShape",
                SuperclassName = "BaseShape",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "delegate",
                    Type = new ObjCTypeRef { Name = "id", IsPointer = true, ProtocolQualifications = ["ShapeDelegate"] },
                    IsReadonly = derivedIsReadonly
                }]
            }
        ]
    };

    [Fact]
    public void Emit_SubclassInstancePropertyOverInheritedClassProperty_EmitsNewWithoutDropping()
    {
        // `+tally` and `-tally` are DIFFERENT ObjC selectors and different C# members, so the
        // instance member is not a duplicate of the inherited static one — dropping it would delete
        // reachable API. It emits, marked [New] to declare the C#-level hiding.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "tally", Type = new ObjCTypeRef { Name = "NSInteger" }, IsReadonly = true, IsClass = true }]
                },
                new ObjCClassDecl
                {
                    Name = "DerivedShape",
                    SuperclassName = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "tally", Type = new ObjCTypeRef { Name = "NSInteger" }, IsReadonly = true }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Equal(2, CountOccurrences(result, "nint Tally { get; }"));
        Assert.Contains("[New]", result);
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.SymbolName == "Tally");
    }

    [Fact]
    public void Emit_SubclassRedeclarationRenamingGetter_EmitsNewAndKeepsBothSelectors()
    {
        // `getter=childValue` exports a DIFFERENT ObjC selector than the inherited `baseValue`, so
        // the inherited member cannot stand in for it — deferring would delete a reachable selector.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "BaseShape",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "value",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        GetterSelector = "baseValue",
                        IsReadonly = true
                    }]
                },
                new ObjCClassDecl
                {
                    Name = "DerivedShape",
                    SuperclassName = "BaseShape",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "value",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        GetterSelector = "childValue",
                        IsReadonly = true
                    }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Contains("[Bind(\"baseValue\")]", result);
        Assert.Contains("[Bind(\"childValue\")]", result);
        Assert.Equal(1, CountOccurrences(result, "[New]"));
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.SymbolName == "Value");
    }

    [Fact]
    public void Emit_SubclassRedeclarationRenamingSetter_EmitsNewAndKeepsBothSelectors()
    {
        // Same rule on the write half: a renamed setter is a distinct selector, so the read-write
        // re-declaration is not covered by the inherited member.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "BaseShape",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "value",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        SetterSelector = "setBaseValue:"
                    }]
                },
                new ObjCClassDecl
                {
                    Name = "DerivedShape",
                    SuperclassName = "BaseShape",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "value",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        SetterSelector = "setChildValue:"
                    }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Contains("[Export(\"setBaseValue:\")] set;", result);
        Assert.Contains("[Export(\"setChildValue:\")] set;", result);
        Assert.Equal(1, CountOccurrences(result, "[New]"));
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.SymbolName == "Value");
    }

    [Fact]
    public void Emit_ParallelClassAndInstanceMembersInChain_ClassifyIndependently()
    {
        // A class and an instance member can be inherited under ONE C# name. The middle class's
        // static member must not displace the root's instance member in the walk, or the leaf's
        // instance re-declaration would be classified against the wrong one and hide the root's
        // writable member.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "RootShape",
                    Properties = [new ObjCPropertyDecl { Name = "value", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }]
                },
                new ObjCClassDecl
                {
                    Name = "MiddleShape",
                    SuperclassName = "RootShape",
                    Properties = [new ObjCPropertyDecl { Name = "value", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true, IsClass = true }]
                },
                new ObjCClassDecl
                {
                    Name = "LeafShape",
                    SuperclassName = "MiddleShape",
                    Properties = [new ObjCPropertyDecl { Name = "value", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        // The root's writable instance member survives, and the leaf defers to it rather than
        // hiding it with a read-only copy.
        Assert.Equal(1, CountOccurrences(result, "[Export(\"setValue:\")] set;"));
        Assert.Contains("[Static]", result);
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "Value");
        Assert.Contains("RootShape", skip.Detail);
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.SymbolName == "setValue");
    }

    [Fact]
    public void Emit_InstanceRedeclarationOverInheritedReadWriteClassProperty_RecordsNoSetterLoss()
    {
        // `+tally` and `-tally` are different selectors, so a read-only instance declaration does
        // not narrow the inherited STATIC setter — nothing is lost and nothing must be recorded.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "tally", Type = new ObjCTypeRef { Name = "NSInteger" }, IsClass = true }]
                },
                new ObjCClassDecl
                {
                    Name = "DerivedShape",
                    SuperclassName = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "tally", Type = new ObjCTypeRef { Name = "NSInteger" }, IsReadonly = true }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Contains("[New]", result);
        Assert.Contains("nint Tally { get; }", result);
        Assert.Empty(diagnostics.SkippedSymbols);
    }

    [Fact]
    public void Emit_MiddleClassDeferredRedeclaration_LeafStillDefersToRootMember()
    {
        // The map must describe the member a subclass really inherits, not the raw declarations of
        // every ancestor. The middle class defers (its read-only copy adds nothing), so the name
        // keeps resolving to the root's read-write member — and the leaf must defer to THAT, not
        // hide a middle member that was never emitted.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "RootShape",
                    Properties = [new ObjCPropertyDecl
                    {
                        Name = "tag",
                        Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                        SetterSelector = "setDisplayTag:"
                    }]
                },
                new ObjCClassDecl
                {
                    Name = "MiddleShape",
                    SuperclassName = "RootShape",
                    Properties = [new ObjCPropertyDecl { Name = "tag", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                },
                new ObjCClassDecl
                {
                    Name = "LeafShape",
                    SuperclassName = "MiddleShape",
                    Properties = [new ObjCPropertyDecl { Name = "tag", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.Equal(1, CountOccurrences(result, "[Export(\"setDisplayTag:\")] set;"));
        Assert.DoesNotContain("[New]", result);
        // Both re-declarations defer, and both name the root as the owner they deferred to.
        Assert.Equal(2, diagnostics.SkippedSymbols.Count(s => s.SymbolName == "Tag"));
        Assert.All(diagnostics.SkippedSymbols.Where(s => s.SymbolName == "Tag"), s => Assert.Contains("RootShape", s.Detail));
    }

    [Fact]
    public void Emit_MiddleClassWidenedRedeclaration_LeafDefersToMiddleMember()
    {
        // The mirror: the middle class widens the root's read-only member, so it DOES emit and the
        // name now resolves to the middle's read-write member. An identical leaf re-declaration
        // defers to that one.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "RootShape",
                    Properties = [new ObjCPropertyDecl { Name = "tag", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true }, IsReadonly = true }]
                },
                new ObjCClassDecl
                {
                    Name = "MiddleShape",
                    SuperclassName = "RootShape",
                    Properties = [new ObjCPropertyDecl { Name = "tag", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }]
                },
                new ObjCClassDecl
                {
                    Name = "LeafShape",
                    SuperclassName = "MiddleShape",
                    Properties = [new ObjCPropertyDecl { Name = "tag", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }]
                }
            ]
        };

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        // Middle's widening member is the only writable Tag; the leaf copy is deferred to it.
        Assert.Equal(1, CountOccurrences(result, "[Export(\"setTag:\")] set;"));
        Assert.Equal(1, CountOccurrences(result, "[New]"));
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "Tag");
        Assert.Contains("MiddleShape", skip.Detail);
    }

    // --- Conformed-protocol narrowing of an inherited property ---

    [Fact]
    public void Emit_ConformedProtocolNarrowsInheritedProperty_ReDeclaresWithAncestorAccessors()
    {
        // bgen inlines a conformed protocol's members into the generated class, so a protocol
        // restating an ancestor's read-write property as read-only puts a getter-only member on the
        // subclass that hides the inherited setter — assigning through a subclass-typed variable
        // stops compiling. Declaring the member explicitly pre-empts that inline, so the ancestor's
        // read-write shape has to be re-declared here even though the subclass's own headers say
        // nothing about it.
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: false);

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        var derived = InterfaceBody(result, "DerivedShape");
        Assert.Contains("[New]", derived);
        Assert.Contains("[Export(\"setTitle:\")] set;", derived);
        // The declaration also hides the conformed protocol interface's member right here in the
        // ApiDefinition, so it states the hiding — an unstated one is a CS0108 in the binding build.
        Assert.Contains("new string Title", derived);
        // Nothing was dropped, so nothing is recorded as skipped.
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.SymbolName == "Title");
    }

    [Fact]
    public void Emit_ConformedProtocolNarrowsRedeclaredInheritedProperty_EmitsInsteadOfDeferring()
    {
        // The shape real headers ship: the subclass ALSO re-declares the property read-only to
        // restate the conformance. Deferring to the inherited member (which is what an otherwise
        // identical re-declaration earns) leaves the protocol's read-only view to be inlined in its
        // place, so the widening re-declaration supersedes it — and since the member is emitted,
        // it is not recorded as a skip.
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: true);

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        var derived = InterfaceBody(result, "DerivedShape");
        Assert.Contains("[New]", derived);
        Assert.Contains("new string Title", derived);
        Assert.Contains("[Export(\"setTitle:\")] set;", derived);
        Assert.DoesNotContain(diagnostics.SkippedSymbols, s => s.SymbolName == "Title");
    }

    [Fact]
    public void Emit_ConformedProtocolMatchesInheritedAccessors_StillDefersToInheritedMember()
    {
        // The protocol view is read-write too, so nothing narrows: the inlined member and the
        // inherited one carry the same accessors. The identical re-declaration keeps deferring and
        // stays recorded — a re-declaration nobody needs must not appear just because a conformance
        // mentions the selector.
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: false, derivedRedeclares: true);

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        Assert.DoesNotContain("[New]", result);
        Assert.Equal("", InterfaceBody(result, "DerivedShape").Trim());
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "Title");
        Assert.Contains("BaseShape", skip.Detail);
    }

    [Fact]
    public void Emit_ConformedProtocolWithoutInheritedMember_EmitsNoRedeclaration()
    {
        // Narrowing needs something to narrow. With no ancestor member of that name in this module,
        // the protocol's read-only view is all there is, and re-declaring it on the class would
        // both invent API and claim to hide a member that isn't there (CS0109).
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: false);
        module.Classes[0].Properties.Clear();

        var result = EmitAndRead(module);

        Assert.DoesNotContain("[New]", result);
        Assert.Equal("", InterfaceBody(result, "DerivedShape").Trim());
    }

    [Fact]
    public void Emit_InheritedProtocolNarrowsInheritedProperty_ReDeclaresWithAncestorAccessors()
    {
        // bgen inlines the conformance list's TRANSITIVE protocol parents too, so the narrowing view
        // can arrive through a protocol the class never names directly (the shape a map-rendering
        // framework's overlay/annotation protocols take).
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: false);
        module.Protocols.Add(new ObjCProtocolDecl { Name = "Overlay", InheritedProtocolNames = ["Titled"] });
        module.Classes[1].ProtocolNames.Clear();
        module.Classes[1].ProtocolNames.Add("Overlay");

        var result = EmitAndRead(module);

        var derived = InterfaceBody(result, "DerivedShape");
        Assert.Contains("[New]", derived);
        Assert.Contains("new string Title", derived);
        Assert.Contains("[Export(\"setTitle:\")] set;", derived);
    }

    [Fact]
    public void Emit_RequiredConformedProtocolNarrowsInheritedProperty_ReDeclaresWithAncestorAccessors()
    {
        // bgen inlines a conformed protocol's REQUIRED members into the class too, so optionality
        // is not what decides whether the narrower view lands on the subclass. Real headers write
        // the narrowing property both ways; the re-declaration has to be there either way.
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: false);
        module.Protocols[0].Properties[0] = module.Protocols[0].Properties[0] with { IsOptional = false };

        var result = EmitAndRead(module);

        var derived = InterfaceBody(result, "DerivedShape");
        Assert.Contains("[New]", derived);
        Assert.Contains("new string Title", derived);
        Assert.Contains("[Export(\"setTitle:\")] set;", derived);
    }

    [Fact]
    public void Emit_MethodSharingPlannedRedeclarationSetterSelector_IsSkipped()
    {
        // The re-declaration is planned before the method loop precisely so its accessor selectors
        // are already claimed: a method exporting `setTitle:` would otherwise export that selector a
        // second time (the method plus the flattened property accessor), which aborts the .NET
        // registrar at launch. Here the ONLY declaration exporting it is the planned re-declaration
        // — the class itself declares no property at all.
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: false);
        module.Classes[1].Methods.Add(new ObjCMethodDecl
        {
            Selector = "setTitle:",
            ReturnType = new ObjCTypeRef { Name = "void" },
            IsInstanceMethod = true,
            Parameters = [new ObjCParameterDecl { Name = "title", Type = new ObjCTypeRef { Name = "NSString", IsPointer = true } }]
        });

        var (result, diagnostics) = EmitApiDefinitionWithDiagnostics(module);

        var derived = InterfaceBody(result, "DerivedShape");
        Assert.Equal(1, CountOccurrences(derived, "[Export(\"setTitle:\")]"));
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "setTitle:");
        Assert.Equal(ObjCSkipReason.DuplicateSelector, skip.Reason);
    }

    [Fact]
    public void Emit_UnemittableOwnPropertyOverPlannedRedeclaration_StillReDeclaresTheInheritedMember()
    {
        // The subclass declares the property itself, but with a type that cannot be resolved in the
        // ApiDefinition, so its declaration never reaches the output. The planned re-declaration is
        // the class's only chance to keep the inherited setter reachable, so an own declaration that
        // was DROPPED must not count as having carried it.
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: true);
        module.Classes[1].Properties[0] = module.Classes[1].Properties[0] with
        {
            Type = new ObjCTypeRef { Name = "OUCompletelyUnknownType", IsPointer = true }
        };

        var result = EmitAndRead(module);

        var derived = InterfaceBody(result, "DerivedShape");
        Assert.Contains("new string Title", derived);
        Assert.Contains("[Export(\"setTitle:\")] set;", derived);
    }

    [Fact]
    public void Emit_OwnDeclarationSupersedingPlannedRedeclaration_StatesTheInterfaceHiding()
    {
        // The subclass re-declares the member with a renamed setter, so it exports a selector the
        // inherited member does not and has to emit in its own right (superseding the plan). It
        // still hides the conformed protocol interface's member, so it states the hiding — and the
        // planned re-declaration must not also emit, which would be a second `Title` (CS0102).
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: true);
        module.Classes[1].Properties[0] = module.Classes[1].Properties[0] with
        {
            IsReadonly = false,
            SetterSelector = "assignTitle:"
        };

        var result = EmitAndRead(module);

        var derived = InterfaceBody(result, "DerivedShape");
        Assert.Equal(1, CountOccurrences(derived, "new string Title"));
        Assert.Contains("[Export(\"assignTitle:\")] set;", derived);
        Assert.DoesNotContain("[Export(\"setTitle:\")] set;", derived);
    }

    [Fact]
    public void Emit_AncestorPropertyTypedRelativeToItsOwnClass_EmitsNoRedeclaration()
    {
        // The ancestor's declaration is re-emitted in the SUBCLASS's declaration context, so a type
        // written relative to the ancestor means something else here: `instancetype` binds the
        // SUBCLASS, and the re-declaration would re-export the ancestor's setter selector under a
        // narrower type than the implementation accepts. (An ObjC lightweight-generic parameter the
        // subclass doesn't carry is the same problem, one step further along — it names nothing.)
        // Leave the narrowing alone rather than answer it with a declaration that doesn't hold.
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: false);
        var baseShapeRef = new ObjCTypeRef { Name = "BaseShape", IsPointer = true };
        module.Classes[0].Properties[0] = new ObjCPropertyDecl { Name = "partner", Type = new ObjCTypeRef { Name = "instancetype" } };
        module.Protocols[0].Properties[0] = new ObjCPropertyDecl { Name = "partner", Type = baseShapeRef, IsReadonly = true, IsOptional = true };

        var result = EmitAndRead(module);

        var derived = InterfaceBody(result, "DerivedShape");
        Assert.Equal("", derived.Trim());
        // Specifically, no re-declaration bound to the subclass rather than the ancestor.
        Assert.DoesNotContain("DerivedShape Partner", result);
    }

    [Fact]
    public void Emit_ConformedProtocolNarrowsInheritedClassProperty_ReDeclaresOnlyTheStaticMember()
    {
        // Class and instance members live in separate ObjC dispatch namespaces, so the narrowing
        // decision is made per kind. Here only the protocol's CLASS property narrows: the static
        // member is re-declared (carrying [Static]) and the unrelated inherited instance member is
        // left alone.
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: false);
        var tallyType = new ObjCTypeRef { Name = "NSInteger" };
        module.Protocols[0].Properties[0] = new ObjCPropertyDecl { Name = "tally", Type = tallyType, IsReadonly = true, IsOptional = true, IsClass = true };
        module.Classes[0].Properties.Add(new ObjCPropertyDecl { Name = "tally", Type = tallyType, IsClass = true });

        var result = EmitAndRead(module);

        var derived = InterfaceBody(result, "DerivedShape");
        Assert.Equal(1, CountOccurrences(derived, "[New]"));
        Assert.Contains("[Static]", derived);
        Assert.Contains("[Export(\"setTally:\")] set;", derived);
        // The inherited instance `title` is untouched — no conformance narrows it.
        Assert.DoesNotContain("Title", derived);
    }

    [Fact]
    public void Emit_ConformedProtocolNarrowsDifferentlyTypedInheritedProperty_EmitsNoRedeclaration()
    {
        // Same selector, different bound type: the ancestor's accessors do not implement what the
        // protocol asks for, so re-exporting its setter selector under the protocol's member would
        // send a message the implementation cannot decode. No re-declaration is sound here.
        var module = BuildProtocolNarrowingModule(protocolIsReadonly: true, derivedRedeclares: false);
        module.Protocols[0].Properties[0] = module.Protocols[0].Properties[0] with
        {
            Type = new ObjCTypeRef { Name = "NSNumber", IsPointer = true }
        };

        var result = EmitAndRead(module);

        Assert.DoesNotContain("[New]", result);
        Assert.Equal("", InterfaceBody(result, "DerivedShape").Trim());
    }

    // ──────────────────────────────────────────────
    // Extern constants → [Static] partial interface {Module}Constants
    // (ObjCConstantsEmitter; formerly in StructsAndEnumsEmitter)
    // ──────────────────────────────────────────────

    [Fact]
    public void EmitConstant_NSString_AsField()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLErrorDomain",
                    Type = SimpleType("NSString", isPointer: true),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"TLErrorDomain\", \"__Internal\")]", output);
        // Module tag "TL" is stripped; interface member has no public/static modifiers.
        Assert.Contains("NSString ErrorDomain { get; }", output);
        Assert.DoesNotContain("public static NSString", output);
    }

    [Fact]
    public void EmitConstant_Int_AsField()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "maxRetries",
                    Type = SimpleType("int"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"maxRetries\", \"__Internal\")]", output);
        Assert.Contains("int MaxRetries { get; }", output);
    }

    [Fact]
    public void EmitConstant_UnsupportedType_RecordsSkip()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "someStruct",
                    Type = SimpleType("TLCustomStruct"),
                    IsExtern = true
                }
            ]
        };

        var (output, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        Assert.DoesNotContain("[Field(", output);
        Assert.DoesNotContain("partial interface TestLibConstants", output);
        Assert.DoesNotContain("TODO", output);
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "someStruct");
        Assert.Equal("constant", skip.SymbolKind);
        Assert.Equal(ObjCSkipReason.UnsupportedConstruct, skip.Reason);
    }

    [Fact]
    public void EmitConstant_WithTypedefAlias_Resolved()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "MyInt",
                    UnderlyingType = SimpleType("NSInteger")
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "maxCount",
                    Type = SimpleType("MyInt"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        // MyInt → NSInteger → nint via typedefMap
        Assert.Contains("nint MaxCount { get; }", output);
        Assert.DoesNotContain("MyInt", output);
    }

    [Fact]
    public void ConstantsClassName_MatchesModuleName()
    {
        var module = new ObjCModule
        {
            ModuleName = "MyFramework",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "version",
                    Type = SimpleType("NSInteger"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("partial interface MyFrameworkConstants", output);
    }

    [Fact]
    public void EmitConstant_Nfloat_AsField()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "defaultScale",
                    Type = SimpleType("CGFloat"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"defaultScale\", \"__Internal\")]", output);
        Assert.Contains("nfloat DefaultScale { get; }", output);
    }

    [Fact]
    public void EmitConstant_NonExtern_Skipped()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "kLocalConst",
                    Type = SimpleType("int"),
                    IsExtern = false
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.DoesNotContain("[Field(", output);
        Assert.DoesNotContain("partial interface TestLibConstants", output);
        Assert.DoesNotContain("KLocalConst", output);
    }

    [Fact]
    public void EmitConstantsInterface_IsPartialWithStaticAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "kVersion",
                    Type = SimpleType("int"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Static]", output);
        Assert.Contains("partial interface TestLibConstants", output);
        Assert.DoesNotContain("public static class TestLibConstants", output);
    }

    [Fact]
    public void EmitConstant_TypedefNSString_EmitsFieldProperty()
    {
        // e.g., typedef NSString *MOSNotification; extern MOSNotification const MOSStoreDidChange;
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "MOSNotification",
                    UnderlyingType = SimpleType("NSString", isPointer: true)
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "MOSStoreDidChange",
                    Type = SimpleType("MOSNotification"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"MOSStoreDidChange\", \"__Internal\")]", output);
        // Tag "MOS" stripped → StoreDidChange
        Assert.Contains("NSString StoreDidChange { get; }", output);
        Assert.DoesNotContain("TODO", output);
    }

    [Fact]
    public void EmitConstant_TypedefNSStringPointerUsage_EmitsFieldProperty()
    {
        // typedef NSString TLNotification; usage: TLNotification *
        // (typedef drops pointer, usage adds it)
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "TLNotification",
                    UnderlyingType = SimpleType("NSString")
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLDidComplete",
                    Type = SimpleType("TLNotification", isPointer: true),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"TLDidComplete\", \"__Internal\")]", output);
        // Tag "TL" stripped → DidComplete
        Assert.Contains("NSString DidComplete { get; }", output);
        Assert.DoesNotContain("TODO", output);
    }

    [Fact]
    public void EmitConstant_ChainedTypedefNSString_EmitsFieldProperty()
    {
        // typedef NSString *BaseNotification; typedef BaseNotification DerivedNotification;
        // Chain: DerivedNotification → BaseNotification → NSString*
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "BaseNotification",
                    UnderlyingType = SimpleType("NSString", isPointer: true)
                },
                new ObjCTypedefDecl
                {
                    Name = "DerivedNotification",
                    UnderlyingType = SimpleType("BaseNotification")
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLSomeEvent",
                    Type = SimpleType("DerivedNotification"),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"TLSomeEvent\", \"__Internal\")]", output);
        // Tag "TL" stripped → SomeEvent
        Assert.Contains("NSString SomeEvent { get; }", output);
        Assert.DoesNotContain("TODO", output);
    }

    [Fact]
    public void EmitConstant_DirectNSString_StillWorks()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLDirectString",
                    Type = SimpleType("NSString", isPointer: true),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("[Field(\"TLDirectString\", \"__Internal\")]", output);
        // Tag "TL" stripped → DirectString
        Assert.Contains("NSString DirectString { get; }", output);
    }

    [Fact]
    public void EmitConstant_NonNSStringTypedef_RecordsSkip()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Typedefs =
            [
                new ObjCTypedefDecl
                {
                    Name = "CustomType",
                    UnderlyingType = SimpleType("SomeStruct")
                }
            ],
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "TLCustomConst",
                    Type = SimpleType("CustomType"),
                    IsExtern = true
                }
            ]
        };

        var (output, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        Assert.DoesNotContain("[Field(", output);
        Assert.DoesNotContain("partial interface TestLibConstants", output);
        Assert.DoesNotContain("TODO", output);
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "TLCustomConst");
        Assert.Equal("constant", skip.SymbolKind);
        Assert.Equal(ObjCSkipReason.UnsupportedConstruct, skip.Reason);
    }

    [Fact]
    public void EmitConstant_ModuleTagStrip_WhenAllShareUppercaseTag()
    {
        var module = new ObjCModule
        {
            ModuleName = "Mapbox",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "MLNShapeSourceOptionClustered",
                    Type = SimpleType("NSString", isPointer: true),
                    IsExtern = true
                },
                new ObjCConstantDecl
                {
                    Name = "MLNOfflinePackError",
                    Type = SimpleType("NSString", isPointer: true),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        Assert.Contains("partial interface MapboxConstants", output);
        Assert.Contains("NSString ShapeSourceOptionClustered { get; }", output);
        Assert.Contains("NSString OfflinePackError { get; }", output);
        Assert.DoesNotContain("MLNShapeSourceOptionClustered { get;", output);
        Assert.DoesNotContain("MLNOfflinePackError { get;", output);
    }

    [Fact]
    public void EmitConstant_ModuleTagStrip_DoesNotFireWhenOneLacksTag()
    {
        var module = new ObjCModule
        {
            ModuleName = "Mapbox",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "MLNShapeSourceOptionClustered",
                    Type = SimpleType("NSString", isPointer: true),
                    IsExtern = true
                },
                new ObjCConstantDecl
                {
                    Name = "OtherConstant",
                    Type = SimpleType("NSString", isPointer: true),
                    IsExtern = true
                }
            ]
        };

        var output = EmitAndRead(module);
        // No shared tag → first-letter PascalCase only (already PascalCase names unchanged)
        Assert.Contains("NSString MLNShapeSourceOptionClustered { get; }", output);
        Assert.Contains("NSString OtherConstant { get; }", output);
        Assert.DoesNotContain("NSString ShapeSourceOptionClustered { get; }", output);
    }

    [Fact]
    public void EmitConstant_Long_EmitsNint_Int64t_RecordsSkip()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "nativeWidthCount",
                    Type = SimpleType("long"),
                    IsExtern = true
                },
                new ObjCConstantDecl
                {
                    Name = "fixedWidthCount",
                    Type = SimpleType("int64_t"),
                    IsExtern = true
                }
            ]
        };

        var (output, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        Assert.Contains("[Field(\"nativeWidthCount\", \"__Internal\")]", output);
        Assert.Contains("nint NativeWidthCount { get; }", output);
        Assert.DoesNotContain("fixedWidthCount", output);
        Assert.DoesNotContain("FixedWidthCount", output);
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "fixedWidthCount");
        Assert.Equal("constant", skip.SymbolKind);
        Assert.Equal(ObjCSkipReason.UnsupportedConstruct, skip.Reason);
    }

    [Fact]
    public void EmitConstant_UnsignedLong_EmitsNuint_Uint64t_RecordsSkip()
    {
        // The unsigned half of the native-width promotion: `unsigned long` and `uint64_t` both map
        // to C# `ulong`, so only the source spelling distinguishes pointer-width from fixed 64-bit.
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "nativeWidthMask",
                    Type = SimpleType("unsigned long"),
                    IsExtern = true
                },
                new ObjCConstantDecl
                {
                    Name = "fixedWidthMask",
                    Type = SimpleType("uint64_t"),
                    IsExtern = true
                }
            ]
        };

        var (output, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        Assert.Contains("[Field(\"nativeWidthMask\", \"__Internal\")]", output);
        Assert.Contains("nuint NativeWidthMask { get; }", output);
        Assert.DoesNotContain("fixedWidthMask", output);
        Assert.DoesNotContain("FixedWidthMask", output);
        var skip = Assert.Single(diagnostics.SkippedSymbols, s => s.SymbolName == "fixedWidthMask");
        Assert.Equal("constant", skip.SymbolKind);
        Assert.Equal(ObjCSkipReason.UnsupportedConstruct, skip.Reason);
    }

    [Fact]
    public void EmitConstant_AllUnsupported_OmitsConstantsInterface()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Constants =
            [
                new ObjCConstantDecl
                {
                    Name = "alpha",
                    Type = SimpleType("TLCustomStruct"),
                    IsExtern = true
                },
                new ObjCConstantDecl
                {
                    Name = "beta",
                    Type = SimpleType("int64_t"),
                    IsExtern = true
                }
            ]
        };

        var (output, diagnostics) = EmitApiDefinitionWithDiagnostics(module);
        Assert.DoesNotContain("[Static]", output);
        Assert.DoesNotContain("partial interface TestLibConstants", output);
        Assert.DoesNotContain("[Field(", output);
        Assert.Equal(2, diagnostics.SkippedSymbols.Count(s => s.SymbolKind == "constant"));
    }

    [Fact]
    public void EmitConstant_ZeroExternConstants_OmitsConstantsInterface()
    {
        var module = new ObjCModule
        {
            ModuleName = "TestLib",
            Classes = [new ObjCClassDecl { Name = "Widget" }],
            Constants = []
        };

        var output = EmitAndRead(module);
        Assert.DoesNotContain("partial interface TestLibConstants", output);
        Assert.DoesNotContain("[Field(", output);
        // Class still emitted; [Static] may appear for other reasons — only assert constants surface.
        Assert.Contains("partial interface Widget", output);
    }

    /// <summary>
    /// A base class owning a read-write <c>title</c>, a protocol restating that selector, and a
    /// subclass conforming to the protocol — the geometry in which bgen's protocol inlining can
    /// shadow the inherited setter.
    /// </summary>
    static ObjCModule BuildProtocolNarrowingModule(bool protocolIsReadonly, bool derivedRedeclares)
    {
        var titleType = new ObjCTypeRef { Name = "NSString", IsPointer = true };
        return new ObjCModule
        {
            ModuleName = "Test",
            Protocols =
            [
                new ObjCProtocolDecl
                {
                    Name = "Titled",
                    Properties = [new ObjCPropertyDecl { Name = "title", Type = titleType, IsReadonly = protocolIsReadonly, IsOptional = true }]
                }
            ],
            Classes =
            [
                new ObjCClassDecl
                {
                    Name = "BaseShape",
                    Properties = [new ObjCPropertyDecl { Name = "title", Type = titleType }]
                },
                new ObjCClassDecl
                {
                    Name = "DerivedShape",
                    SuperclassName = "BaseShape",
                    ProtocolNames = ["Titled"],
                    Properties = derivedRedeclares
                        ? [new ObjCPropertyDecl { Name = "title", Type = titleType, IsReadonly = protocolIsReadonly }]
                        : []
                }
            ]
        };
    }

    /// <summary>
    /// The body of one emitted <c>partial interface</c>, so an assertion can name the type the
    /// member has to land on rather than searching the whole file.
    /// </summary>
    static string InterfaceBody(string emitted, string interfaceName)
    {
        var header = $"    partial interface {interfaceName}";
        var start = emitted.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, $"emitted output declares '{interfaceName}'");
        var open = emitted.IndexOf("    {", start, StringComparison.Ordinal);
        var close = emitted.IndexOf("\n    }", open, StringComparison.Ordinal);
        Assert.True(open >= 0 && close > open, $"'{interfaceName}' has a body");
        return emitted[(open + "    {".Length)..close];
    }

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
