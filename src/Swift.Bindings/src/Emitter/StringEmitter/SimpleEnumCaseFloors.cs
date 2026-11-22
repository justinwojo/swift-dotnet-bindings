// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// The managed refusal of a simple enum's floored cases. A Swift enum can add a case after the enum
/// itself shipped (<c>@available(iOS 18, *) case experimental</c>); the C# enum carries that case's
/// floor as a <c>[SupportedOSPlatform]</c> on the constant, but a C# enum value is a bare integer, so
/// nothing stops a consumer passing it to Swift on an older OS. Swift then has to turn the scalar into
/// a case the running OS does not have: the case-by-tag switch of the enum's own wrappers traps in its
/// <c>if #available … else fatalError</c> arm, and the synthesized <c>init(rawValue:)</c> returns nil for
/// a case the running OS is below the floor of, so the wrapper's <c>preconditionFailure</c> traps.
/// Either kills the process.
///
/// <para>Every enum with a case floored above the enum gets one generated method,
/// <c>__{Enum}CaseFloors.Require(value)</c>, in a static class beside its declaration. It throws
/// <see cref="System.PlatformNotSupportedException"/> when the value is a case the running OS is below
/// the floor of, and returns the value unchanged otherwise. Each site that hands a simple-enum value
/// to native code spells the value through <see cref="ToNative"/> or <see cref="RequireAvailable"/>,
/// which route it through that method whenever the enum's <see cref="TypeRecord.CaseFloorGuard"/>
/// names one. The record carries the method so a site needs only the type record it already reads,
/// and the module database persists it so a downstream binding refuses the same values.</para>
///
/// <para>Only the managed-to-Swift direction is refused. A value Swift returns is a case the running OS
/// has (an OS that lacks the case cannot produce it), and the return projection is a plain cast.</para>
/// </summary>
internal static class SimpleEnumCaseFloors
{
    /// <summary>
    /// The enum's cases whose effective floor (the case's own availability, an inferred one included,
    /// merged with the enum's and its enclosing types') is stricter than the enum's on some platform,
    /// with that floor. @_spi cases are not emitted as C# constants and are left out.
    /// </summary>
    internal static List<(EnumCaseDecl Case, IReadOnlyList<AvailabilityAnnotation> Floor)> FlooredCases(EnumDecl enumDecl)
    {
        var enumFloor = AvailabilityHelpers.MergeAvailabilityFromAncestors(null, enumDecl);
        var result = new List<(EnumCaseDecl, IReadOnlyList<AvailabilityAnnotation>)>();
        foreach (var caseDecl in enumDecl.Cases)
        {
            if (caseDecl.IsSpiProtected || caseDecl.AvailabilityAnnotations is not { Count: > 0 })
                continue;
            var caseFloor = AvailabilityHelpers.MergeAvailabilityFromAncestors(caseDecl.AvailabilityAnnotations, enumDecl);
            if (caseFloor is not { Count: > 0 })
                continue;
            if (AvailabilityAttributeEmitter.IsStricterFloor(caseFloor, enumFloor))
                result.Add((caseDecl, caseFloor));
        }
        return result;
    }

    /// <summary>
    /// Whether the enum's values pass through a <c>Require</c> on their way to Swift: it has a case
    /// floored above it, and a static class beside it can name it (no generic type encloses it, whose
    /// type-record name carries no type arguments).
    /// </summary>
    internal static bool NeedsGuard(EnumDecl enumDecl) =>
        !enumDecl.IsGeneric
        && !HasGenericEnclosingType(enumDecl)
        && FlooredCases(enumDecl).Count > 0;

    private static bool HasGenericEnclosingType(TypeDecl typeDecl)
    {
        for (var parent = typeDecl.ParentDecl as TypeDecl; parent != null; parent = parent.ParentDecl as TypeDecl)
        {
            if (parent.IsGeneric)
                return true;
        }
        return false;
    }

    /// <summary>
    /// The fully qualified <c>Require</c> of the enum named <paramref name="csharpTypeName"/>: the
    /// helper class is declared in the same scope as the enum and named after it, so it is as unique
    /// as the enum is. A class shared by every enum of a scope would be declared again by each
    /// assembly binding into that namespace, and a consumer naming it would see two (CS0433).
    /// </summary>
    internal static string GuardName(CSharpTypeName csharpTypeName)
    {
        var full = csharpTypeName.FullyQualifiedName;
        var lastDot = full.LastIndexOf('.');
        var scope = lastDot < 0 ? string.Empty : full.Substring(0, lastDot + 1);
        return $"global::{scope}{GuardClassName(full.Substring(lastDot + 1))}.Require";
    }

    private static string GuardClassName(string enumCSharpName) => $"__{enumCSharpName}CaseFloors";

    /// <summary>
    /// The P/Invoke argument for a simple-enum value: <c>(underlying)value</c>, through the enum's
    /// <c>Require</c> when <paramref name="record"/> names one.
    /// </summary>
    internal static string ToNative(TypeRecord? record, string underlyingType, string valueExpression)
        => $"({underlyingType}){RequireAvailable(record, valueExpression)}";

    /// <summary>
    /// The value itself, through the enum's <c>Require</c> when <paramref name="record"/> names one: for
    /// a site that hands the enum-typed value to native code without a cast.
    /// </summary>
    internal static string RequireAvailable(TypeRecord? record, string valueExpression)
        => record?.CaseFloorGuard is { } guard ? $"{guard}({valueExpression})" : valueExpression;

    /// <summary><see cref="RequireAvailable(TypeRecord?, string)"/> for a site that holds the guard itself.</summary>
    internal static string RequireAvailable(string? guard, string valueExpression)
        => guard is null ? valueExpression : $"{guard}({valueExpression})";

    /// <summary>
    /// <see cref="ToNative(TypeRecord?, string, string)"/> for a site that holds the guard itself (a
    /// <see cref="MarshalledType.SimpleEnum"/> parameter, the enum's own extension members).
    /// </summary>
    internal static string ToNative(string? guard, string underlyingType, string valueExpression)
        => guard is null ? $"({underlyingType}){valueExpression}" : $"({underlyingType}){guard}({valueExpression})";

    /// <summary>
    /// Refuses, before anything reaches Swift, a floored case held inside a parameter's container: an
    /// <c>Optional</c>, <c>Array</c>, <c>Set</c> or <c>Dictionary</c> (keys and values) or a tuple, to
    /// any depth. Those values cross as a container the runtime builds from the C# values, which writes
    /// each enum straight into Swift memory with no <c>init(rawValue:)</c>, so no wrapper refuses them
    /// and Swift receives a case the running OS may not have. Every element is checked before the
    /// container is built: a refusal then leaves nothing half-built in Swift memory to clean up.
    ///
    /// <para>Only a parameter's own value is checked (the managed-to-Swift direction), never a
    /// callback's result: a throw inside a reverse P/Invoke ends the process. A top-level sequence
    /// that is not a collection is materialized first, so the check and the container read the same
    /// elements and a one-shot sequence is enumerated once. An <c>inout</c> parameter is left as the
    /// caller passed it, since reassigning it would hand the caller a different object.</para>
    ///
    /// <para>It is written where the parameter still has its public C# type: in a method body before
    /// the conversions, and in a property or subscript's public accessor before it converts the value
    /// for the raw-typed accessor method (whose Swift containers would already hold the case, and
    /// whose <c>SwiftOptional</c> reads as the enum's default when empty). Its locals are named
    /// <c>__{<paramref name="localBaseName"/>}Floor…</c>; pass the parameter's marshalling base name
    /// so they cannot take a sibling parameter's name.</para>
    ///
    /// <para>Returns whether anything was written: false when the type holds no simple enum with a
    /// floored case, or names it only at the top level, where <see cref="ToNative(string?, string,
    /// string)"/> already refuses it.</para>
    /// </summary>
    internal static bool EmitContainedValueRefusal(CSharpWriter writer, TypeSpec typeSpec, string parameterName,
        string localBaseName, bool isInOut, ITypeDatabase typeDatabase)
    {
        if (!RefusesContainedValues(typeSpec, typeDatabase))
            return false;
        var baseName = NameProvider.StripVerbatimPrefix(localBaseName);
        if (!isInOut && ContainerKind(typeSpec) == Container.Array)
            writer.WriteLine($"{parameterName} = {Materialized(parameterName, typeSpec, baseName, 0)};");
        // The walk tests a copy for null, so the compiler learns nothing about the parameter itself.
        var root = $"{baseName}FloorRoot";
        writer.WriteLine($"var {root} = {parameterName};");
        var counter = 0;
        WriteRefusal(writer, typeSpec, root, baseName, ref counter, typeDatabase);
        return true;
    }

    /// <summary>
    /// <paramref name="expression"/>, an array parameter's sequence, with every sequence the walk
    /// reads made a collection: the array itself, and each array element of it in turn
    /// (<c>[[Mode]]</c> arrives as a sequence of sequences). An array nested in anything else arrives
    /// as a list already. The tests are type tests, not null tests: a null sequence still reaches the
    /// container's ArgumentNullException, and the parameter keeps the non-null state the
    /// marshalling after it reads.
    /// </summary>
    private static string Materialized(string expression, TypeSpec arraySpec, string baseName, int depth)
    {
        // The array holds the guarded enum, so an array element holds it too.
        var element = arraySpec.GenericParameters[0];
        if (ContainerKind(element) == Container.Array)
        {
            var lambda = $"{baseName}FloorSequence{depth}";
            return $"global::System.Linq.Enumerable.ToArray(global::System.Linq.Enumerable.Select({expression}, {lambda} => {Materialized(lambda, element, baseName, depth + 1)}))";
        }
        return $"({expression} is global::System.Collections.ICollection ? {expression} : global::System.Linq.Enumerable.ToArray({expression}))";
    }

    /// <summary>Whether <see cref="EmitContainedValueRefusal"/> writes anything for <paramref name="typeSpec"/>.</summary>
    internal static bool RefusesContainedValues(TypeSpec typeSpec, ITypeDatabase typeDatabase)
        => ContainerKind(typeSpec) != Container.None && HoldsGuardedEnum(typeSpec, typeDatabase);

    /// <summary>
    /// <see cref="EmitContainedValueRefusal"/> as one line of statements, for the accessor bodies
    /// written on a single line; <see langword="null"/> when nothing needs refusing.
    /// </summary>
    internal static string? ContainedValueRefusalLine(TypeSpec typeSpec, string parameterName, string localBaseName,
        ITypeDatabase typeDatabase)
    {
        var output = new System.IO.StringWriter();
        if (!EmitContainedValueRefusal(new CSharpWriter(output), typeSpec, parameterName, localBaseName, isInOut: false, typeDatabase))
            return null;
        return string.Join(" ", output.ToString().Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    }

    private enum Container { None, Optional, Array, Set, Dictionary, Tuple }

    private static Container ContainerKind(TypeSpec typeSpec) => typeSpec switch
    {
        TupleTypeSpec { Elements.Count: > 1 } => Container.Tuple,
        TupleTypeSpec { Elements.Count: 1 } tuple => ContainerKind(tuple.Elements[0]),
        NamedTypeSpec { Name: "Swift.Optional", GenericParameters.Count: 1 } => Container.Optional,
        NamedTypeSpec { Name: "Swift.Array", GenericParameters.Count: 1 } => Container.Array,
        NamedTypeSpec { Name: "Swift.Set", GenericParameters.Count: 1 } => Container.Set,
        NamedTypeSpec { Name: "Swift.Dictionary", GenericParameters.Count: 2 } => Container.Dictionary,
        _ => Container.None,
    };

    private static IEnumerable<TypeSpec> ContainedTypes(TypeSpec typeSpec) => typeSpec switch
    {
        TupleTypeSpec tuple => tuple.Elements,
        _ when ContainerKind(typeSpec) != Container.None => typeSpec.GenericParameters,
        _ => Array.Empty<TypeSpec>(),
    };

    private static bool HoldsGuardedEnum(TypeSpec typeSpec, ITypeDatabase typeDatabase) =>
        ContainerKind(typeSpec) == Container.None
            ? ExtensionMarshallingHelper.SimpleEnumCaseFloorGuard(typeSpec, typeDatabase) != null
            : ContainedTypes(typeSpec).Any(t => HoldsGuardedEnum(t, typeDatabase));

    private static void WriteRefusal(CSharpWriter writer, TypeSpec typeSpec, string expression, string baseName,
        ref int counter, ITypeDatabase typeDatabase)
    {
        if (!HoldsGuardedEnum(typeSpec, typeDatabase))
            return;
        var kind = ContainerKind(typeSpec);
        if (kind == Container.None)
        {
            writer.WriteLine($"{ExtensionMarshallingHelper.SimpleEnumCaseFloorGuard(typeSpec, typeDatabase)}({expression});");
            return;
        }
        if (typeSpec is TupleTypeSpec { Elements.Count: 1 } single)
        {
            WriteRefusal(writer, single.Elements[0], expression, baseName, ref counter, typeDatabase);
            return;
        }
        if (kind == Container.Tuple)
        {
            var elements = ((TupleTypeSpec)typeSpec).Elements;
            for (var i = 0; i < elements.Count; i++)
                WriteRefusal(writer, elements[i], $"{expression}.Item{i + 1}", baseName, ref counter, typeDatabase);
            return;
        }
        // A pattern binds the value once, whatever the expression is: a nullable value, a reference
        // that may be null, or a tuple element.
        var value = $"{baseName}FloorValue{counter++}";
        writer.WriteLine($"if ({expression} is {{ }} {value})");
        writer.WriteLine("{");
        writer.Indent++;
        if (kind == Container.Optional)
        {
            WriteRefusal(writer, typeSpec.GenericParameters[0], value, baseName, ref counter, typeDatabase);
        }
        else
        {
            var element = $"{baseName}FloorElement{counter++}";
            writer.WriteLine($"foreach (var {element} in {value})");
            writer.WriteLine("{");
            writer.Indent++;
            if (kind == Container.Dictionary)
            {
                WriteRefusal(writer, typeSpec.GenericParameters[0], $"{element}.Key", baseName, ref counter, typeDatabase);
                WriteRefusal(writer, typeSpec.GenericParameters[1], $"{element}.Value", baseName, ref counter, typeDatabase);
            }
            else
            {
                WriteRefusal(writer, typeSpec.GenericParameters[0], element, baseName, ref counter, typeDatabase);
            }
            writer.Indent--;
            writer.WriteLine("}");
        }
        writer.Indent--;
        writer.WriteLine("}");
    }

    /// <summary>
    /// Writes the enum's <c>Require</c> into its static guard class beside the enum, in the scope the
    /// enum is declared in (<paramref name="enumCSharpTypeName"/> is the enum's record name). Each
    /// floored case refuses below its floor; every other value, including an undeclared one, passes.
    /// </summary>
    internal static void EmitGuard(CSharpWriter writer, EnumDecl enumDecl, CSharpTypeName enumCSharpTypeName,
        Dictionary<string, string>? caseNameMap)
    {
        var floored = FlooredCases(enumDecl);
        if (floored.Count == 0)
            return;
        var full = enumCSharpTypeName.FullyQualifiedName;
        var enumCSharpName = full.Substring(full.LastIndexOf('.') + 1);
        // Fully qualified: inside the class, a bare enum name equal to a member's (an enum named
        // Require) would name the member instead (CS0119).
        var qualifiedEnum = $"global::{full}";
        writer.WriteLine($"/// <summary>Refuses a case of {XmlDocCommentEmitter.XmlEscape(enumDecl.Name)} the running OS is below the floor of before it reaches Swift, which cannot represent the case there and traps.</summary>");
        writer.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        writer.WriteLine($"public static class {GuardClassName(enumCSharpName)}");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("/// <summary>Returns <paramref name=\"value\"/> when the running OS has that case; throws <see cref=\"global::System.PlatformNotSupportedException\"/> otherwise.</summary>");
        // The case constants carry their floors; the switch names them only to refuse them.
        writer.WriteLine("#pragma warning disable CA1416");
        writer.WriteLine($"public static {qualifiedEnum} Require({qualifiedEnum} value)");
        writer.WriteLine("{");
        writer.Indent++;
        writer.WriteLine("switch (value)");
        writer.WriteLine("{");
        writer.Indent++;
        foreach (var (caseDecl, floor) in floored)
        {
            var caseName = NameProvider.GetCaseName(caseDecl.Name, caseNameMap);
            writer.WriteLine($"case {qualifiedEnum}.{caseName}:");
            writer.Indent++;
            AvailabilityAttributeEmitter.EmitRuntimeAvailabilityGuard(writer, floor, $"{enumDecl.Name}.{caseDecl.Name}");
            writer.WriteLine("break;");
            writer.Indent--;
        }
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine("return value;");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine("#pragma warning restore CA1416");
        writer.Indent--;
        writer.WriteLine("}");
        writer.WriteLine();
    }
}
