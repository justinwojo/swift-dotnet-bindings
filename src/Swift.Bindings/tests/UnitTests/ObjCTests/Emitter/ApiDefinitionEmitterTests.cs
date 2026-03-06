// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using BindingsGeneration.ObjC;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests.ObjCTests;

public class ApiDefinitionEmitterTests
{
    static readonly Microsoft.Extensions.Logging.ILogger Logger = NullLogger.Instance;

    // Helper to emit and read back the file content
    static string EmitAndRead(ObjCModule module, string ns = "TestNamespace")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"apidefinition_test_{Guid.NewGuid():N}");
        try
        {
            var path = ApiDefinitionEmitter.Emit(module, dir, ns, Logger);
            Assert.Equal(Path.Combine(dir, "ApiDefinition.cs"), path);
            return File.ReadAllText(path);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

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
    public void Emit_InitSelector_EmitsConstructor()
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
        Assert.Contains("[Export(\"init\")]", result);
        Assert.Contains("NativeHandle Constructor();", result);
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
}
