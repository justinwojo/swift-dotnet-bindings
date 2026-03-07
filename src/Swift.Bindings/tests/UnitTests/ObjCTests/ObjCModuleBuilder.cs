// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using BindingsGeneration.ObjC;
using static BindingsGeneration.Tests.ObjCTests.ObjCTestHelpers;

namespace BindingsGeneration.Tests.ObjCTests;

/// <summary>
/// Fluent builder for constructing ObjCModule instances in tests.
/// </summary>
public class ObjCModuleBuilder
{
    private string _moduleName = "TestLib";
    private readonly List<ObjCClassDecl> _classes = [];
    private readonly List<ObjCProtocolDecl> _protocols = [];
    private readonly List<ObjCEnumDecl> _enums = [];
    private readonly List<ObjCStructDecl> _structs = [];
    private readonly List<ObjCFunctionDecl> _functions = [];
    private readonly List<ObjCConstantDecl> _constants = [];
    private readonly List<ObjCTypedefDecl> _typedefs = [];
    private readonly List<ObjCCategoryDecl> _categories = [];

    public static ObjCModuleBuilder Create(string moduleName = "TestLib") =>
        new() { _moduleName = moduleName };

    public ObjCModuleBuilder WithClass(string name, string? superclass = null, Action<ClassBuilder>? configure = null)
    {
        var builder = new ClassBuilder(name, superclass);
        configure?.Invoke(builder);
        _classes.Add(builder.Build());
        return this;
    }

    public ObjCModuleBuilder WithClass(ObjCClassDecl cls)
    {
        _classes.Add(cls);
        return this;
    }

    public ObjCModuleBuilder WithProtocol(string name, Action<ProtocolBuilder>? configure = null)
    {
        var builder = new ProtocolBuilder(name);
        configure?.Invoke(builder);
        _protocols.Add(builder.Build());
        return this;
    }

    public ObjCModuleBuilder WithProtocol(ObjCProtocolDecl proto)
    {
        _protocols.Add(proto);
        return this;
    }

    public ObjCModuleBuilder WithEnum(string name, Action<EnumBuilder>? configure = null)
    {
        var builder = new EnumBuilder(name);
        configure?.Invoke(builder);
        _enums.Add(builder.Build());
        return this;
    }

    public ObjCModuleBuilder WithEnum(ObjCEnumDecl enumDecl)
    {
        _enums.Add(enumDecl);
        return this;
    }

    public ObjCModuleBuilder WithStruct(string name, params (string fieldName, string fieldType)[] fields)
    {
        _structs.Add(new ObjCStructDecl
        {
            Name = name,
            Fields = fields.Select(f => new ObjCStructField { Name = f.fieldName, Type = SimpleType(f.fieldType) }).ToList()
        });
        return this;
    }

    public ObjCModuleBuilder WithStruct(ObjCStructDecl s)
    {
        _structs.Add(s);
        return this;
    }

    public ObjCModuleBuilder WithFunction(string name, string returnType, params (string paramName, string paramType)[] parameters)
    {
        _functions.Add(new ObjCFunctionDecl
        {
            Name = name,
            ReturnType = SimpleType(returnType),
            Parameters = parameters.Select(p => new ObjCParameterDecl { Name = p.paramName, Type = SimpleType(p.paramType) }).ToList()
        });
        return this;
    }

    public ObjCModuleBuilder WithFunction(ObjCFunctionDecl func)
    {
        _functions.Add(func);
        return this;
    }

    public ObjCModuleBuilder WithConstant(string name, string type, bool isExtern = true)
    {
        _constants.Add(new ObjCConstantDecl { Name = name, Type = SimpleType(type, isPointer: type == "NSString"), IsExtern = isExtern });
        return this;
    }

    public ObjCModuleBuilder WithConstant(ObjCConstantDecl constant)
    {
        _constants.Add(constant);
        return this;
    }

    public ObjCModuleBuilder WithTypedef(string name, string underlyingType)
    {
        _typedefs.Add(new ObjCTypedefDecl { Name = name, UnderlyingType = SimpleType(underlyingType) });
        return this;
    }

    public ObjCModuleBuilder WithTypedef(ObjCTypedefDecl typedef)
    {
        _typedefs.Add(typedef);
        return this;
    }

    public ObjCModuleBuilder WithCategory(string categoryName, string className, Action<CategoryBuilder>? configure = null)
    {
        var builder = new CategoryBuilder(categoryName, className);
        configure?.Invoke(builder);
        _categories.Add(builder.Build());
        return this;
    }

    public ObjCModuleBuilder WithCategory(ObjCCategoryDecl category)
    {
        _categories.Add(category);
        return this;
    }

    public ObjCModule Build() => new()
    {
        ModuleName = _moduleName,
        Classes = _classes,
        Protocols = _protocols,
        Enums = _enums,
        Structs = _structs,
        Functions = _functions,
        Constants = _constants,
        Typedefs = _typedefs,
        Categories = _categories,
    };

    // --- Sub-builders ---

    public class ClassBuilder
    {
        private readonly string _name;
        private readonly string? _superclass;
        private readonly List<string> _protocols = [];
        private readonly List<ObjCMethodDecl> _methods = [];
        private readonly List<ObjCPropertyDecl> _properties = [];
        private readonly List<ObjCAvailability> _availability = [];
        private string? _swiftName;
        private string? _docComment;

        internal ClassBuilder(string name, string? superclass) { _name = name; _superclass = superclass; }

        public ClassBuilder Protocol(string name) { _protocols.Add(name); return this; }
        public ClassBuilder Method(ObjCMethodDecl m) { _methods.Add(m); return this; }
        public ClassBuilder Property(ObjCPropertyDecl p) { _properties.Add(p); return this; }
        public ClassBuilder Availability(ObjCAvailability a) { _availability.Add(a); return this; }
        public ClassBuilder SwiftName(string name) { _swiftName = name; return this; }
        public ClassBuilder DocComment(string comment) { _docComment = comment; return this; }

        public ClassBuilder Method(string selector, string returnType, bool instance = true, params (string name, string type)[] parameters) =>
            Method(new ObjCMethodDecl
            {
                Selector = selector,
                ReturnType = SimpleType(returnType),
                IsInstanceMethod = instance,
                Parameters = parameters.Select(p => new ObjCParameterDecl { Name = p.name, Type = SimpleType(p.type) }).ToList()
            });

        public ClassBuilder Property(string name, string type, bool isReadonly = false, bool isPointer = false) =>
            Property(new ObjCPropertyDecl
            {
                Name = name,
                Type = SimpleType(type, isPointer),
                IsReadonly = isReadonly,
            });

        internal ObjCClassDecl Build() => new()
        {
            Name = _name,
            SuperclassName = _superclass,
            ProtocolNames = _protocols,
            Methods = _methods,
            Properties = _properties,
            Availability = _availability,
            SwiftName = _swiftName,
            DocComment = _docComment,
        };
    }

    public class ProtocolBuilder
    {
        private readonly string _name;
        private readonly List<string> _inherited = [];
        private readonly List<ObjCMethodDecl> _methods = [];
        private readonly List<ObjCPropertyDecl> _properties = [];
        private readonly List<ObjCAvailability> _availability = [];
        private string? _docComment;

        internal ProtocolBuilder(string name) { _name = name; }

        public ProtocolBuilder Inherits(string name) { _inherited.Add(name); return this; }
        public ProtocolBuilder Method(ObjCMethodDecl m) { _methods.Add(m); return this; }
        public ProtocolBuilder Property(ObjCPropertyDecl p) { _properties.Add(p); return this; }
        public ProtocolBuilder Availability(ObjCAvailability a) { _availability.Add(a); return this; }
        public ProtocolBuilder DocComment(string comment) { _docComment = comment; return this; }

        public ProtocolBuilder Method(string selector, string returnType, bool instance = true, params (string name, string type)[] parameters) =>
            Method(new ObjCMethodDecl
            {
                Selector = selector,
                ReturnType = SimpleType(returnType),
                IsInstanceMethod = instance,
                Parameters = parameters.Select(p => new ObjCParameterDecl { Name = p.name, Type = SimpleType(p.type) }).ToList()
            });

        public ProtocolBuilder Property(string name, string type, bool isReadonly = false, bool isPointer = false) =>
            Property(new ObjCPropertyDecl
            {
                Name = name,
                Type = SimpleType(type, isPointer),
                IsReadonly = isReadonly,
            });

        internal ObjCProtocolDecl Build() => new()
        {
            Name = _name,
            InheritedProtocolNames = _inherited,
            Methods = _methods,
            Properties = _properties,
            Availability = _availability,
            DocComment = _docComment,
        };
    }

    public class EnumBuilder
    {
        private readonly string _name;
        private bool _isOptions;
        private ObjCTypeRef? _underlyingType;
        private readonly List<ObjCEnumCaseDecl> _cases = [];
        private readonly List<ObjCAvailability> _availability = [];
        private string? _swiftName;
        private string? _docComment;

        internal EnumBuilder(string name) { _name = name; }

        public EnumBuilder Options() { _isOptions = true; return this; }
        public EnumBuilder UnderlyingType(string type) { _underlyingType = SimpleType(type); return this; }
        public EnumBuilder Case(string name, long? value = null) { _cases.Add(new ObjCEnumCaseDecl { Name = name, Value = value }); return this; }
        public EnumBuilder Availability(ObjCAvailability a) { _availability.Add(a); return this; }
        public EnumBuilder SwiftName(string name) { _swiftName = name; return this; }
        public EnumBuilder DocComment(string comment) { _docComment = comment; return this; }

        internal ObjCEnumDecl Build() => new()
        {
            Name = _name,
            IsOptions = _isOptions,
            UnderlyingType = _underlyingType,
            Cases = _cases,
            Availability = _availability,
            SwiftName = _swiftName,
            DocComment = _docComment,
        };
    }

    public class CategoryBuilder
    {
        private readonly string _categoryName;
        private readonly string _className;
        private readonly List<string> _protocols = [];
        private readonly List<ObjCMethodDecl> _methods = [];
        private readonly List<ObjCPropertyDecl> _properties = [];

        internal CategoryBuilder(string categoryName, string className) { _categoryName = categoryName; _className = className; }

        public CategoryBuilder Protocol(string name) { _protocols.Add(name); return this; }
        public CategoryBuilder Method(ObjCMethodDecl m) { _methods.Add(m); return this; }
        public CategoryBuilder Property(ObjCPropertyDecl p) { _properties.Add(p); return this; }

        public CategoryBuilder Method(string selector, string returnType, bool instance = true, params (string name, string type)[] parameters) =>
            Method(new ObjCMethodDecl
            {
                Selector = selector,
                ReturnType = SimpleType(returnType),
                IsInstanceMethod = instance,
                Parameters = parameters.Select(p => new ObjCParameterDecl { Name = p.name, Type = SimpleType(p.type) }).ToList()
            });

        public CategoryBuilder Property(string name, string type, bool isReadonly = false, bool isPointer = false) =>
            Property(new ObjCPropertyDecl
            {
                Name = name,
                Type = SimpleType(type, isPointer),
                IsReadonly = isReadonly,
            });

        internal ObjCCategoryDecl Build() => new()
        {
            CategoryName = _categoryName,
            ClassName = _className,
            ProtocolNames = _protocols,
            Methods = _methods,
            Properties = _properties,
        };
    }
}
