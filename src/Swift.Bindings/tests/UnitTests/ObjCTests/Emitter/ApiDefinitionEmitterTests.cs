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
        Assert.Contains("partial interface IMyDelegate", result);
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
        Assert.Contains("partial interface IMyDelegate : INSCoding, INSCopying", result);
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
        Assert.Contains("partial interface IMyDelegate : INSCoding", result);
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
        Assert.Contains("partial interface IMyDelegate\n", result);
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

    // --- Availability ---

    [Fact]
    public void Emit_IntroducedAvailability_EmitsAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Availability = [new ObjCAvailability
                {
                    Platform = "ios",
                    IntroducedVersion = "16.0"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Introduced(PlatformName.iOS, 16, 0)]", result);
    }

    [Fact]
    public void Emit_DeprecatedAvailability_EmitsAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Availability = [new ObjCAvailability
                {
                    Platform = "ios",
                    DeprecatedVersion = "15.0"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Deprecated(PlatformName.iOS, 15, 0)]", result);
    }

    [Fact]
    public void Emit_NonIosAvailability_Skipped()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Availability = [new ObjCAvailability
                {
                    Platform = "macos",
                    IntroducedVersion = "13.0"
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.DoesNotContain("Introduced", result);
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

    // --- Method availability ---

    [Fact]
    public void Emit_MethodAvailability_EmitsAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "newApi",
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    IsInstanceMethod = true,
                    Availability = [new ObjCAvailability
                    {
                        Platform = "ios",
                        IntroducedVersion = "17.0"
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Introduced(PlatformName.iOS, 17, 0)]", result);
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
        Assert.Contains("partial interface IMyCollection : INSCoding", result);
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
            AppleSdkTypeNames = new HashSet<string> { "KeyType" },
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
    public void Emit_Category_InstancePropertyOnly_SkippedAsEmpty()
    {
        // Categories with only instance properties and no methods are skipped entirely.
        // MAUI bgen generates [Category] interfaces as static classes, which cannot
        // have instance members (CS0708).
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

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[Category]", result);
        Assert.DoesNotContain("Widget_Info", result);
    }

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
    public void Emit_MethodPropertyNameCollision_PropertySkipped()
    {
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
        // Method emitted
        Assert.Contains("[Export(\"isEnabled\")]", result);
        // Property with same PascalCase name should be skipped
        var lines = result.Split('\n').Where(l => l.Contains("IsEnabled")).ToList();
        // Should have method line but NOT property line
        Assert.Contains(lines, l => l.Contains("bool IsEnabled()"));
        Assert.DoesNotContain(lines, l => l.Contains("{ get; }"));
    }

    [Fact]
    public void Emit_RenamedMethodPropertyCollision_PropertySkipped()
    {
        // P2 fix: when method dedup renames "Manager" → "ManagerDidDisconnect",
        // a property named "managerDidDisconnect" should be skipped
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
        // First method: Manager(NSObject, NSObject)
        Assert.Contains("void Manager(NSObject m, NSObject p);", result);
        // Second method renamed to full selector: ManagerDidDisconnect(NSObject, NSObject)
        Assert.Contains("void ManagerDidDisconnect(NSObject m, NSObject p);", result);
        // Property with same PascalCase name as renamed method should be skipped
        Assert.DoesNotContain("{ get; }", result.Split('\n').Where(l => l.Contains("ManagerDidDisconnect")).LastOrDefault() ?? "");
    }

    [Fact]
    public void Emit_ProtocolMethodPropertyCollision_PropertySkipped()
    {
        // P2 fix: protocol paths also need method→property collision tracking
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
        // Method emitted
        Assert.Contains("bool IsReady();", result);
        // Property with same name should be skipped
        var propertyLines = result.Split('\n').Where(l => l.Contains("{ get; }")).ToList();
        Assert.Empty(propertyLines);
    }

    [Fact]
    public void Emit_CategoryMethodPropertyCollision_PropertySkipped()
    {
        // Category paths also need method→property collision tracking
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

        var result = EmitAndRead(module);
        // Method emitted
        Assert.Contains("UIColor TintColor();", result);
        // Property with same name should be skipped
        var propertyLines = result.Split('\n').Where(l => l.Contains("{ get; }")).ToList();
        Assert.Empty(propertyLines);
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
        // When AppleSdkTypeNames is populated, methods with types NOT in the known set
        // or Apple SDK should be skipped.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNames = new HashSet<string> { "UIColor" },
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
            AppleSdkTypeNames = new HashSet<string> { "NSData" },
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
            AppleSdkTypeNames = new HashSet<string> { "UIView" },
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

    [Fact]
    public void Emit_ApiDefinitionFiltering_AcceptsProtocolInterfacePrefix()
    {
        // Protocol interface types have I prefix (e.g., ICTTelephonyNetworkInfoDelegate)
        // Should resolve via Apple SDK if the base name (without I) is in the set.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNames = new HashSet<string> { "SomeDelegate" },
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
                            ReturnType = new ObjCTypeRef { Name = "ISomeDelegate" },
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
        // When AppleSdkTypeNames is null (no Clang context, e.g. -fmodules AST),
        // the fallback only accepts names whose head matches a registered Apple
        // ObjC class prefix. The bare "any uppercase" rule was a false-positive
        // source: it let cross-framework third-party types (e.g. FIROptions in a
        // sibling xcframework) through and produced CS0246 at compile time.
        var module = new ObjCModule
        {
            ModuleName = "Test",
            AppleSdkTypeNames = null,
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
            AppleSdkTypeNames = new HashSet<string>(), // Empty — no Apple types
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
            AppleSdkTypeNames = new HashSet<string> { "NSURLSessionDelegate", "NSHTTPURLResponse" },
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
                            ReturnType = new ObjCTypeRef { Name = "INSUrlSessionDelegate" },
                            IsInstanceMethod = true,
                        }
                    ]
                }
            ]
        };

        var result = EmitAndRead(module);
        // NSHTTPURLResponse → NSHttpUrlResponse via MapType, must match SDK name
        Assert.Contains("GetResponse", result);
        // INSUrlSessionDelegate (I-prefixed protocol) → strip I → NSUrlSessionDelegate → reverse to NSURLSessionDelegate
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
            AppleSdkTypeNames = new HashSet<string> { "UIColor" },
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
            Classes = [new ObjCClassDecl
            {
                Name = "Logger",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "allLogs",
                    Type = new ObjCTypeRef
                    {
                        Name = "NSArray",
                        IsPointer = true,
                        GenericArgs = [new ObjCTypeRef { Name = "BRLMLog", IsPointer = true }]
                    },
                    IsReadonly = true,
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("BRLMLog[] AllLogs { get; }", result);
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

    // --- Fix #13: Platform availability attribute emission ---

    [Fact]
    public void Emit_ClassWithIntroduced_EmitsIntroducedAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MBMicroblinkApp",
                Availability = [new ObjCAvailability { Platform = "ios", IntroducedVersion = "13.0" }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Introduced(PlatformName.iOS, 13, 0)]", result);
        Assert.Contains("[BaseType(typeof(NSObject))]", result);
        Assert.Contains("partial interface MBMicroblinkApp", result);
    }

    [Fact]
    public void Emit_ProtocolWithIntroduced_EmitsIntroducedAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Protocols = [new ObjCProtocolDecl
            {
                Name = "MyDelegate",
                Availability = [new ObjCAvailability { Platform = "ios", IntroducedVersion = "15.0" }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Introduced(PlatformName.iOS, 15, 0)]", result);
        Assert.Contains("partial interface IMyDelegate", result);
    }

    [Fact]
    public void Emit_MethodWithDeprecated_EmitsDeprecatedAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Methods = [new ObjCMethodDecl
                {
                    Selector = "oldMethod",
                    IsInstanceMethod = true,
                    ReturnType = new ObjCTypeRef { Name = "void" },
                    Availability = [new ObjCAvailability
                    {
                        Platform = "ios",
                        IntroducedVersion = "10.0",
                        DeprecatedVersion = "16.0",
                        Message = "Use newMethod instead"
                    }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Introduced(PlatformName.iOS, 10, 0)]", result);
        Assert.Contains("[Deprecated(PlatformName.iOS, 16, 0, message: \"Use newMethod instead\")]", result);
        Assert.Contains("OldMethod", result);
    }

    [Fact]
    public void Emit_PropertyWithIntroduced_EmitsIntroducedAttribute()
    {
        var module = new ObjCModule
        {
            ModuleName = "Test",
            Classes = [new ObjCClassDecl
            {
                Name = "MyClass",
                Properties = [new ObjCPropertyDecl
                {
                    Name = "newProp",
                    Type = new ObjCTypeRef { Name = "NSString", IsPointer = true },
                    IsReadonly = true,
                    Availability = [new ObjCAvailability { Platform = "ios", IntroducedVersion = "14.0" }]
                }]
            }]
        };

        var result = EmitAndRead(module);
        Assert.Contains("[Introduced(PlatformName.iOS, 14, 0)]", result);
        Assert.Contains("string NewProp { get; }", result);
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
        // [Model] protocols use bare name (no I prefix) per Xamarin convention
        Assert.Contains("partial interface MyViewDelegate", result);
        Assert.DoesNotContain("IMyViewDelegate", result);
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
        Assert.Contains("partial interface IConfigurable", result);
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
                Name = "FIRMessagingDelegate",
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
                            new ObjCParameterDecl { Name = "messaging", Type = SimpleType("FIRMessaging", isPointer: true) },
                            new ObjCParameterDecl { Name = "fcmToken", Type = SimpleType("NSString", isPointer: true) }
                        ]
                    }
                ]
            })
            // The third-party FIRMessaging type would normally come from a sibling
            // framework (FirebaseMessaging) parsed alongside — declare it here so
            // the resolvability filter doesn't drop the method we're asserting on.
            .WithAppleSdkTypeNames("FIRMessaging")
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
                            new ObjCParameterDecl { Name = "messaging", Type = SimpleType("FIRMessaging", isPointer: true) },
                            new ObjCParameterDecl { Name = "fcmToken", Type = SimpleType("NSString", isPointer: true) }
                        ]
                    }
                ]
            })
            .WithAppleSdkTypeNames("FIRMessaging")
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
                CategoryName = "RLMValue",
                ClassName = "NSNull",
                ProtocolNames = ["RLMValue"],
            })
            .Build();

        var result = EmitAndRead(module);
        Assert.DoesNotContain("[Category]", result);
        Assert.DoesNotContain("NSNull_RLMValue", result);
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
                ProtocolNames = ["RLMInt", "RLMBool"],
                Methods =
                [
                    new ObjCMethodDecl
                    {
                        Selector = "rlm_intValue",
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
        Assert.DoesNotContain("IRLMInt", result);
        Assert.DoesNotContain("IRLMBool", result);
        Assert.Contains("[Export(\"rlm_intValue\")]", result);
        Assert.Contains("int Rlm_intValue()", result);
    }

    [Fact]
    public void Emit_ForeignTypeCategoryWithProperties_SkipsInstanceProperties()
    {
        // Instance properties are skipped: bgen generates static classes
        // which cannot have instance members (CS0708)
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
                        IsClass = false, // instance property — should be skipped
                    }
                ]
            })
            .Build();

        var result = EmitAndRead(module);
        Assert.Contains("[Category]", result);
        // Method emitted as extension method
        Assert.Contains("Sd_setImageWithURL(", result);
        // Instance property skipped (CS0708)
        Assert.DoesNotContain("Sd_currentImageURL", result);
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
    public void Emit_WeakDelegatePattern_DroppedWhenWeakNameCollidesWithPriorMethod()
    {
        // The WeakDelegate/Wrap pattern emits two members (PropName + WeakPropName).
        // If a previously emitted method has already claimed either name, both members must
        // be dropped — otherwise the generated binding would have a duplicate-name CS0102.
        // Here a method named `weakDelegate` PascalCases to `WeakDelegate`, which is the
        // exact synthetic name the WeakDelegate pattern would emit.
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

        // The method must still be emitted.
        Assert.Contains("WeakDelegate", result);
        // The WeakDelegate/Wrap pattern must NOT be emitted (would duplicate WeakDelegate).
        Assert.DoesNotContain("[Wrap(\"WeakDelegate\")]", result);
        Assert.DoesNotContain("MyViewDelegate Delegate { get; set; }", result);
    }
}
