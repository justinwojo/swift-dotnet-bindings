// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using BindingsGeneration;
using BindingsGeneration.Demangling;
using Xunit;
using Node = BindingsGeneration.Demangling.Node;

namespace DemanglerTests;

/// <summary>
/// Direct unit tests for Swift5Reducer.Convert() with hand-built Node trees.
/// These test the reduction logic independently from the demangling engine.
/// </summary>
public class Swift5ReducerTests
{
    const string kSymbol = "$s_test_symbol";

    // --- Helper methods for building Node trees ---

    static Node MakeGlobal(Node child)
    {
        var global = new Node(NodeKind.Global);
        global.AddChild(child);
        return global;
    }

    static Node MakeModule(string name)
    {
        return new Node(NodeKind.Module, name);
    }

    static Node MakeIdentifier(string name)
    {
        return new Node(NodeKind.Identifier, name);
    }

    static Node MakeType(Node child)
    {
        var type = new Node(NodeKind.Type);
        type.AddChild(child);
        return type;
    }

    /// <summary>
    /// Builds: Nominal(Module("moduleName"), Identifier("typeName"))
    /// </summary>
    static Node MakeNominal(NodeKind nominalKind, string moduleName, string typeName)
    {
        var nominal = new Node(nominalKind);
        nominal.AddChild(MakeModule(moduleName));
        nominal.AddChild(MakeIdentifier(typeName));
        return nominal;
    }

    /// <summary>
    /// Builds: FunctionType(ArgumentTuple(Type(argType)), ReturnType(Type(returnType)))
    /// </summary>
    static Node MakeFunctionType(Node argType, Node returnType)
    {
        var funcType = new Node(NodeKind.FunctionType);

        var argTuple = new Node(NodeKind.ArgumentTuple);
        argTuple.AddChild(MakeType(argType));
        funcType.AddChild(argTuple);

        var retNode = new Node(NodeKind.ReturnType);
        retNode.AddChild(MakeType(returnType));
        funcType.AddChild(retNode);

        return funcType;
    }

    // --- Nominal type reductions ---

    [Fact]
    public void ConvertNominal_Class()
    {
        var node = MakeGlobal(MakeNominal(NodeKind.Class, "MyModule", "MyClass"));
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var named = Assert.IsType<NamedTypeSpec>(ts.TypeSpec);
        Assert.Equal("MyModule.MyClass", named.Name);
    }

    [Fact]
    public void ConvertNominal_Structure()
    {
        var node = MakeGlobal(MakeNominal(NodeKind.Structure, "Swift", "Int"));
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        Assert.Equal("Swift.Int", ((NamedTypeSpec)ts.TypeSpec).Name);
    }

    [Fact]
    public void ConvertNominal_Enum()
    {
        var node = MakeGlobal(MakeNominal(NodeKind.Enum, "Swift", "Optional"));
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        Assert.Equal("Swift.Optional", ((NamedTypeSpec)ts.TypeSpec).Name);
    }

    [Fact]
    public void ConvertNominal_Protocol()
    {
        var node = MakeGlobal(MakeNominal(NodeKind.Protocol, "Swift", "Equatable"));
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        Assert.Equal("Swift.Equatable", ((NamedTypeSpec)ts.TypeSpec).Name);
    }

    [Fact]
    public void ConvertNominal_NestedType()
    {
        // Outer.Inner:
        // Class
        //   Class
        //     Module("Mod")
        //     Identifier("Outer")
        //   Identifier("Inner")
        var inner = new Node(NodeKind.Class);
        var outer = MakeNominal(NodeKind.Class, "Mod", "Outer");
        inner.AddChild(outer);
        inner.AddChild(MakeIdentifier("Inner"));

        var node = MakeGlobal(inner);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        Assert.Equal("Mod.Outer.Inner", ((NamedTypeSpec)ts.TypeSpec).Name);
    }

    // --- Module reduction ---

    [Fact]
    public void ConvertModule_ProducesProvenance()
    {
        var node = MakeGlobal(MakeModule("Foundation"));
        var result = Swift5Reducer.Convert(node, kSymbol);
        var prov = Assert.IsType<ProvenanceReduction>(result);
        Assert.True(prov.Provenance.IsTopLevel);
        Assert.Equal("Foundation", prov.Provenance.Module);
    }

    // --- Tuple reductions ---

    [Fact]
    public void ConvertTuple_Empty()
    {
        var tuple = new Node(NodeKind.Tuple);
        var node = MakeGlobal(tuple);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var tupleSpec = Assert.IsType<TupleTypeSpec>(ts.TypeSpec);
        Assert.True(tupleSpec.IsEmptyTuple);
    }

    [Fact]
    public void ConvertTuple_SingleElement()
    {
        var tuple = new Node(NodeKind.Tuple);
        var element = new Node(NodeKind.TupleElement);
        element.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        tuple.AddChild(element);

        var node = MakeGlobal(tuple);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var tupleSpec = Assert.IsType<TupleTypeSpec>(ts.TypeSpec);
        Assert.Single(tupleSpec.Elements);
        Assert.Equal("Swift.Int", ((NamedTypeSpec)tupleSpec.Elements[0]).Name);
    }

    [Fact]
    public void ConvertTuple_MultipleElements()
    {
        var tuple = new Node(NodeKind.Tuple);

        var elem1 = new Node(NodeKind.TupleElement);
        elem1.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        tuple.AddChild(elem1);

        var elem2 = new Node(NodeKind.TupleElement);
        elem2.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "String")));
        tuple.AddChild(elem2);

        var node = MakeGlobal(tuple);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var tupleSpec = Assert.IsType<TupleTypeSpec>(ts.TypeSpec);
        Assert.Equal(2, tupleSpec.Elements.Count);
    }

    [Fact]
    public void ConvertTuple_NamedElement()
    {
        var tuple = new Node(NodeKind.Tuple);
        var element = new Node(NodeKind.TupleElement);
        element.AddChild(new Node(NodeKind.TupleElementName, "label"));
        element.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        tuple.AddChild(element);

        var node = MakeGlobal(tuple);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var tupleSpec = Assert.IsType<TupleTypeSpec>(ts.TypeSpec);
        Assert.Single(tupleSpec.Elements);
        Assert.Equal("label", tupleSpec.Elements[0].TypeLabel);
    }

    // --- FunctionType reductions ---

    [Fact]
    public void ConvertFunctionType_Basic()
    {
        // (Int) -> String
        var funcType = MakeFunctionType(
            MakeNominal(NodeKind.Structure, "Swift", "Int"),
            MakeNominal(NodeKind.Structure, "Swift", "String"));

        var node = MakeGlobal(funcType);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var closure = Assert.IsType<ClosureTypeSpec>(ts.TypeSpec);
        Assert.False(closure.Throws);
        Assert.False(closure.IsAsync);
        Assert.True(closure.IsEscaping); // FunctionType (not NoEscape) should be escaping
    }

    [Fact]
    public void ConvertFunctionType_NoEscape()
    {
        var funcType = new Node(NodeKind.NoEscapeFunctionType);
        var argTuple = new Node(NodeKind.ArgumentTuple);
        argTuple.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        funcType.AddChild(argTuple);
        var retNode = new Node(NodeKind.ReturnType);
        retNode.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        funcType.AddChild(retNode);

        var node = MakeGlobal(funcType);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var closure = Assert.IsType<ClosureTypeSpec>(ts.TypeSpec);
        Assert.False(closure.IsEscaping);
    }

    [Fact]
    public void ConvertFunctionType_Throws()
    {
        var funcType = new Node(NodeKind.FunctionType);
        funcType.AddChild(new Node(NodeKind.ThrowsAnnotation));
        var argTuple = new Node(NodeKind.ArgumentTuple);
        argTuple.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        funcType.AddChild(argTuple);
        var retNode = new Node(NodeKind.ReturnType);
        retNode.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "String")));
        funcType.AddChild(retNode);

        var node = MakeGlobal(funcType);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var closure = Assert.IsType<ClosureTypeSpec>(ts.TypeSpec);
        Assert.True(closure.Throws);
        Assert.False(closure.IsAsync);
    }

    [Fact]
    public void ConvertFunctionType_Async()
    {
        var funcType = new Node(NodeKind.FunctionType);
        funcType.AddChild(new Node(NodeKind.AsyncAnnotation));
        var argTuple = new Node(NodeKind.ArgumentTuple);
        argTuple.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        funcType.AddChild(argTuple);
        var retNode = new Node(NodeKind.ReturnType);
        retNode.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Void")));
        funcType.AddChild(retNode);

        var node = MakeGlobal(funcType);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var closure = Assert.IsType<ClosureTypeSpec>(ts.TypeSpec);
        Assert.True(closure.IsAsync);
        Assert.False(closure.Throws);
    }

    [Fact]
    public void ConvertFunctionType_AsyncThrows()
    {
        var funcType = new Node(NodeKind.FunctionType);
        funcType.AddChild(new Node(NodeKind.ThrowsAnnotation));
        funcType.AddChild(new Node(NodeKind.AsyncAnnotation));
        var argTuple = new Node(NodeKind.ArgumentTuple);
        argTuple.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        funcType.AddChild(argTuple);
        var retNode = new Node(NodeKind.ReturnType);
        retNode.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "String")));
        funcType.AddChild(retNode);

        var node = MakeGlobal(funcType);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var closure = Assert.IsType<ClosureTypeSpec>(ts.TypeSpec);
        Assert.True(closure.IsAsync);
        Assert.True(closure.Throws);
    }

    // --- Function reductions ---

    [Fact]
    public void ConvertFunction_WithLabels()
    {
        // Function
        //   Module("MyModule")
        //   Identifier("doSomething")
        //   LabelList
        //     Identifier("with")
        //   Type
        //     FunctionType(Int -> String)
        var func = new Node(NodeKind.Function);
        func.AddChild(MakeModule("MyModule"));
        func.AddChild(MakeIdentifier("doSomething"));

        var labelList = new Node(NodeKind.LabelList);
        labelList.AddChild(MakeIdentifier("with"));
        func.AddChild(labelList);

        func.AddChild(MakeType(MakeFunctionType(
            MakeNominal(NodeKind.Structure, "Swift", "Int"),
            MakeNominal(NodeKind.Structure, "Swift", "String"))));

        var node = MakeGlobal(func);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var funcReduction = Assert.IsType<FunctionReduction>(result);
        Assert.Equal("doSomething", funcReduction.Function.Name);
        Assert.True(funcReduction.Function.Provenance.IsTopLevel);
        Assert.Equal("MyModule", funcReduction.Function.Provenance.Module);
        Assert.Single(funcReduction.Function.ParameterList.Elements);
        Assert.Equal("with", funcReduction.Function.ParameterList.Elements[0].TypeLabel);
    }

    [Fact]
    public void ConvertFunction_WithoutLabels()
    {
        // Function without LabelList
        var func = new Node(NodeKind.Function);
        func.AddChild(MakeModule("Mod"));
        func.AddChild(MakeIdentifier("foo"));
        func.AddChild(MakeType(MakeFunctionType(
            MakeNominal(NodeKind.Structure, "Swift", "Int"),
            MakeNominal(NodeKind.Structure, "Swift", "Int"))));

        var node = MakeGlobal(func);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var funcReduction = Assert.IsType<FunctionReduction>(result);
        Assert.Equal("foo", funcReduction.Function.Name);
    }

    [Fact]
    public void ConvertFunction_InstanceMethod()
    {
        // Function on a Class provenance
        var func = new Node(NodeKind.Function);
        func.AddChild(MakeNominal(NodeKind.Class, "Mod", "MyClass"));
        func.AddChild(MakeIdentifier("bar"));
        func.AddChild(MakeType(MakeFunctionType(
            MakeNominal(NodeKind.Structure, "Swift", "Int"),
            MakeNominal(NodeKind.Structure, "Swift", "Int"))));

        var node = MakeGlobal(func);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var funcReduction = Assert.IsType<FunctionReduction>(result);
        Assert.Equal("bar", funcReduction.Function.Name);
        Assert.True(funcReduction.Function.Provenance.IsInstance);
        Assert.Equal("Mod.MyClass", funcReduction.Function.Provenance.InstanceType.Name);
    }

    // --- Allocator reductions ---

    [Fact]
    public void ConvertAllocator_ProducesAllocatingInit()
    {
        // Allocator
        //   Class("Mod", "Foo")
        //   Type(FunctionType)
        var alloc = new Node(NodeKind.Allocator);
        alloc.AddChild(MakeNominal(NodeKind.Class, "Mod", "Foo"));
        alloc.AddChild(MakeType(MakeFunctionType(
            new Node(NodeKind.Tuple), // empty args
            MakeNominal(NodeKind.Class, "Mod", "Foo"))));

        var node = MakeGlobal(alloc);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var funcReduction = Assert.IsType<FunctionReduction>(result);
        Assert.Equal("__allocating_init", funcReduction.Function.Name);
        Assert.True(funcReduction.Function.Provenance.IsInstance);
    }

    // --- DispatchThunk reductions ---

    [Fact]
    public void ConvertDispatchThunk_WrapsFunction()
    {
        // DispatchThunk
        //   Function
        //     Module("Mod")
        //     Identifier("foo")
        //     Type(FunctionType)
        var func = new Node(NodeKind.Function);
        func.AddChild(MakeModule("Mod"));
        func.AddChild(MakeIdentifier("foo"));
        func.AddChild(MakeType(MakeFunctionType(
            MakeNominal(NodeKind.Structure, "Swift", "Int"),
            MakeNominal(NodeKind.Structure, "Swift", "Int"))));

        var thunk = new Node(NodeKind.DispatchThunk);
        thunk.AddChild(func);

        var node = MakeGlobal(thunk);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var dtReduction = Assert.IsType<DispatchThunkFunctionReduction>(result);
        Assert.Equal("foo", dtReduction.Function.Name);
    }

    [Fact]
    public void ConvertDispatchThunk_WrapsAllocator()
    {
        var alloc = new Node(NodeKind.Allocator);
        alloc.AddChild(MakeNominal(NodeKind.Class, "Mod", "Bar"));
        alloc.AddChild(MakeType(MakeFunctionType(
            new Node(NodeKind.Tuple),
            MakeNominal(NodeKind.Class, "Mod", "Bar"))));

        var thunk = new Node(NodeKind.DispatchThunk);
        thunk.AddChild(alloc);

        var node = MakeGlobal(thunk);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var dtReduction = Assert.IsType<DispatchThunkFunctionReduction>(result);
        Assert.Equal("__allocating_init", dtReduction.Function.Name);
    }

    // --- TypeMetadataAccessFunction reductions ---

    [Fact]
    public void ConvertMetadataAccessor_Class()
    {
        // TypeMetadataAccessFunction
        //   Type
        //     Class("Mod", "Foo")
        var metaAccess = new Node(NodeKind.TypeMetadataAccessFunction);
        metaAccess.AddChild(MakeType(MakeNominal(NodeKind.Class, "Mod", "Foo")));

        var node = MakeGlobal(metaAccess);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var meta = Assert.IsType<MetadataAccessorReduction>(result);
        Assert.Equal("Mod.Foo", meta.TypeSpec.Name);
    }

    [Fact]
    public void ConvertMetadataAccessor_Structure()
    {
        var metaAccess = new Node(NodeKind.TypeMetadataAccessFunction);
        metaAccess.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Array")));

        var node = MakeGlobal(metaAccess);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var meta = Assert.IsType<MetadataAccessorReduction>(result);
        Assert.Equal("Swift.Array", meta.TypeSpec.Name);
    }

    // --- ProtocolWitnessTable reductions ---

    [Fact]
    public void ConvertProtocolWitnessTable()
    {
        // ProtocolWitnessTable
        //   ProtocolConformance
        //     Type(Class("Mod", "Foo"))
        //     Type(Protocol("Mod", "Bar"))
        var conformance = new Node(NodeKind.ProtocolConformance);
        conformance.AddChild(MakeType(MakeNominal(NodeKind.Class, "Mod", "Foo")));
        conformance.AddChild(MakeType(MakeNominal(NodeKind.Protocol, "Mod", "Bar")));

        var pwt = new Node(NodeKind.ProtocolWitnessTable);
        pwt.AddChild(conformance);

        var node = MakeGlobal(pwt);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var witness = Assert.IsType<ProtocolWitnessTableReduction>(result);
        Assert.Equal("Mod.Foo", witness.ImplementingType.Name);
        Assert.Equal("Mod.Bar", witness.ProtocolType.Name);
    }

    // --- ProtocolConformanceDescriptor reductions ---

    [Fact]
    public void ConvertProtocolConformanceDescriptor()
    {
        // ProtocolConformanceDescriptor
        //   ProtocolConformance
        //     Type(Structure("Mod", "Foo"))
        //     Type(Protocol("Mod", "Equatable"))
        //     Module("Mod")
        var conformance = new Node(NodeKind.ProtocolConformance);
        conformance.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Mod", "Foo")));
        conformance.AddChild(MakeType(MakeNominal(NodeKind.Protocol, "Mod", "Equatable")));
        conformance.AddChild(MakeModule("Mod"));

        var pcd = new Node(NodeKind.ProtocolConformanceDescriptor);
        pcd.AddChild(conformance);

        var node = MakeGlobal(pcd);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var desc = Assert.IsType<ProtocolConformanceDescriptorReduction>(result);
        Assert.Equal("Mod.Foo", desc.ImplementingType.Name);
        Assert.Equal("Mod.Equatable", desc.ProtocolType.Name);
        Assert.Equal("Mod", desc.Module);
    }

    // --- BoundGeneric reductions ---

    [Fact]
    public void ConvertBoundGenericStructure()
    {
        // BoundGenericStructure
        //   Type
        //     Structure("Swift", "Array")
        //   TypeList
        //     Type
        //       Structure("Swift", "Int")
        var boundGeneric = new Node(NodeKind.BoundGenericStructure);
        boundGeneric.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Array")));

        var typeList = new Node(NodeKind.TypeList);
        typeList.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        boundGeneric.AddChild(typeList);

        var node = MakeGlobal(boundGeneric);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var named = Assert.IsType<NamedTypeSpec>(ts.TypeSpec);
        Assert.Equal("Swift.Array", named.Name);
        Assert.Single(named.GenericParameters);
        Assert.Equal("Swift.Int", ((NamedTypeSpec)named.GenericParameters[0]).Name);
    }

    [Fact]
    public void ConvertBoundGenericClass()
    {
        var boundGeneric = new Node(NodeKind.BoundGenericClass);
        boundGeneric.AddChild(MakeType(MakeNominal(NodeKind.Class, "Mod", "Box")));

        var typeList = new Node(NodeKind.TypeList);
        typeList.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "String")));
        boundGeneric.AddChild(typeList);

        var node = MakeGlobal(boundGeneric);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var named = Assert.IsType<NamedTypeSpec>(ts.TypeSpec);
        Assert.Equal("Mod.Box", named.Name);
        Assert.Single(named.GenericParameters);
    }

    [Fact]
    public void ConvertBoundGenericEnum()
    {
        var boundGeneric = new Node(NodeKind.BoundGenericEnum);
        boundGeneric.AddChild(MakeType(MakeNominal(NodeKind.Enum, "Swift", "Optional")));

        var typeList = new Node(NodeKind.TypeList);
        typeList.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        boundGeneric.AddChild(typeList);

        var node = MakeGlobal(boundGeneric);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var named = Assert.IsType<NamedTypeSpec>(ts.TypeSpec);
        Assert.Equal("Swift.Optional", named.Name);
        Assert.Single(named.GenericParameters);
    }

    [Fact]
    public void ConvertBoundGeneric_MultipleTypeParams()
    {
        // Dictionary<String, Int>
        var boundGeneric = new Node(NodeKind.BoundGenericStructure);
        boundGeneric.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Dictionary")));

        var typeList = new Node(NodeKind.TypeList);
        typeList.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "String")));
        typeList.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        boundGeneric.AddChild(typeList);

        var node = MakeGlobal(boundGeneric);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var named = Assert.IsType<NamedTypeSpec>(ts.TypeSpec);
        Assert.Equal("Swift.Dictionary", named.Name);
        Assert.Equal(2, named.GenericParameters.Count);
    }

    // --- DependentGenericParamType reductions ---

    [Fact]
    public void ConvertDependentGenericParam()
    {
        // DependentGenericParamType
        //   Index(0)   -- depth
        //   Index(0)   -- index
        var param = new Node(NodeKind.DependentGenericParamType);
        param.AddChild(new Node(NodeKind.Index, 0L));
        param.AddChild(new Node(NodeKind.Index, 0L));

        var node = MakeGlobal(param);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var named = Assert.IsType<NamedTypeSpec>(ts.TypeSpec);
        Assert.Equal("T_0_0", named.Name);
    }

    [Fact]
    public void ConvertDependentGenericParam_SecondParam()
    {
        var param = new Node(NodeKind.DependentGenericParamType);
        param.AddChild(new Node(NodeKind.Index, 0L));
        param.AddChild(new Node(NodeKind.Index, 1L));

        var node = MakeGlobal(param);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        Assert.Equal("T_0_1", ((NamedTypeSpec)ts.TypeSpec).Name);
    }

    // --- DependentMemberType reductions ---

    [Fact]
    public void ConvertDependentMember_WithAssociatedType()
    {
        // DependentMemberType
        //   Type
        //     DependentGenericParamType(0, 0)
        //   DependentAssociatedTypeRef("Element")
        var param = new Node(NodeKind.DependentGenericParamType);
        param.AddChild(new Node(NodeKind.Index, 0L));
        param.AddChild(new Node(NodeKind.Index, 0L));

        var member = new Node(NodeKind.DependentMemberType);
        member.AddChild(MakeType(param));
        member.AddChild(new Node(NodeKind.DependentAssociatedTypeRef, "Element"));

        var node = MakeGlobal(member);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        Assert.Equal("T_0_0.Element", ((NamedTypeSpec)ts.TypeSpec).Name);
    }

    // --- DependentGenericType reductions ---

    [Fact]
    public void ConvertDependentGenericType()
    {
        // DependentGenericType
        //   DependentGenericSignature
        //     DependentGenericParamCount(2)
        //   Type
        //     FunctionType(...)
        var sig = new Node(NodeKind.DependentGenericSignature);
        sig.AddChild(new Node(NodeKind.DependentGenericParamCount, 2L));

        var funcType = MakeFunctionType(
            MakeNominal(NodeKind.Structure, "Swift", "Int"),
            MakeNominal(NodeKind.Structure, "Swift", "Int"));

        var depGeneric = new Node(NodeKind.DependentGenericType);
        depGeneric.AddChild(sig);
        depGeneric.AddChild(MakeType(funcType));

        var node = MakeGlobal(depGeneric);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var closure = Assert.IsType<ClosureTypeSpec>(ts.TypeSpec);
        Assert.Equal(2, closure.GenericParameters.Count);
        Assert.Equal("T_0_0", ((NamedTypeSpec)closure.GenericParameters[0]).Name);
        Assert.Equal("T_0_1", ((NamedTypeSpec)closure.GenericParameters[1]).Name);
    }

    // --- Static reduction ---

    [Fact]
    public void ConvertStatic_PassesThrough()
    {
        // Static
        //   Class("Mod", "Foo")
        var stat = new Node(NodeKind.Static);
        stat.AddChild(MakeNominal(NodeKind.Class, "Mod", "Foo"));

        var node = MakeGlobal(stat);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        Assert.Equal("Mod.Foo", ((NamedTypeSpec)ts.TypeSpec).Name);
    }

    // --- Error cases ---

    [Fact]
    public void Convert_UnknownNodeKind_ReturnsError()
    {
        var node = new Node(NodeKind.Suffix, "test");
        var result = Swift5Reducer.Convert(node, kSymbol);
        var error = Assert.IsType<ReductionError>(result);
        Assert.Contains("No rule for node", error.Message);
    }

    [Fact]
    public void Convert_NominalWithWrongChild_ReturnsError()
    {
        // Class with non-Identifier second child
        var classNode = new Node(NodeKind.Class);
        classNode.AddChild(MakeModule("Mod"));
        classNode.AddChild(new Node(NodeKind.Module, "NotAnIdentifier"));

        var node = MakeGlobal(classNode);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var error = Assert.IsType<ReductionError>(result);
        Assert.Contains("Identifier", error.Message);
    }

    // --- VariadicTupleElement ---

    [Fact]
    public void ConvertVariadicTupleElement()
    {
        // TupleElement
        //   VariadicMarker
        //   Type(Structure("Swift", "Int"))
        var tuple = new Node(NodeKind.Tuple);
        var element = new Node(NodeKind.TupleElement);
        element.AddChild(new Node(NodeKind.VariadicMarker));
        element.AddChild(MakeType(MakeNominal(NodeKind.Structure, "Swift", "Int")));
        tuple.AddChild(element);

        var node = MakeGlobal(tuple);
        var result = Swift5Reducer.Convert(node, kSymbol);
        var ts = Assert.IsType<TypeSpecReduction>(result);
        var tupleSpec = Assert.IsType<TupleTypeSpec>(ts.TypeSpec);
        Assert.Single(tupleSpec.Elements);
        var arrayType = Assert.IsType<NamedTypeSpec>(tupleSpec.Elements[0]);
        Assert.Equal("Swift.Array", arrayType.Name);
        Assert.Single(arrayType.GenericParameters);
    }
}
