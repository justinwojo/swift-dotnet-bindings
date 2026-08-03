// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>
/// Synthesizes the bitwise surface a Swift <c>OptionSet</c> gets for free on the Swift side —
/// <c>|</c>, <c>&amp;</c>, <c>^</c>, <c>~</c> and a <c>Contains</c> membership test — purely in C#
/// over the type's own emitted <c>RawValue</c> property and <c>rawValue:</c> initializer.
///
/// Swift's operators here come from protocol extensions on <c>SetAlgebra</c>/<c>OptionSet</c>, so
/// they carry no ABI symbols of their own and nothing in the parsed surface can be bound directly.
/// Without this, a consumer combining two members has to hand-write
/// <c>new Style(a.RawValue | b.RawValue)</c> — which is exactly what the emitted bodies do, so this
/// moves a documented workaround into the binding at identical runtime cost (two getter calls plus
/// one initializer call) rather than adding a new one.
///
/// Emission is gated on what the type ACTUALLY emitted, not on what the Swift declaration promises:
/// both the <c>rawValue</c> property and the one-argument <c>init(rawValue:)</c> must have been
/// emitted, and the raw type must project to an integral C# type. That is why the call site sits
/// after the property and method loops have run.
/// </summary>
public static class OptionSetOperatorEmitter
{
    /// <summary>
    /// C# types whose values support <c>|</c>, <c>&amp;</c>, <c>^</c> and <c>~</c> directly.
    /// A raw type outside this set (a string-backed RawRepresentable, a floating-point raw value)
    /// has no bitwise meaning, so nothing is synthesized for it.
    /// </summary>
    private static readonly HashSet<string> IntegralRawTypes = new(StringComparer.Ordinal)
    {
        "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte", "nint", "nuint",
    };

    /// <summary>The binary operators synthesized, paired with the C# expression that combines two raw values.</summary>
    private static readonly (string Symbol, string Summary)[] BinaryOperators =
    {
        ("|", "Returns the union of two option sets — every option present in either operand."),
        ("&", "Returns the intersection of two option sets — only the options present in both operands."),
        ("^", "Returns the symmetric difference — the options present in exactly one of the two operands."),
    };

    /// <summary>
    /// Emits the synthesized bitwise members for <paramref name="structDecl"/> when it conforms to
    /// <c>OptionSet</c> and its emitted surface can support them. A no-op otherwise.
    /// </summary>
    /// <param name="csWriter">The C# writer, positioned inside the type body.</param>
    /// <param name="structDecl">The struct being emitted.</param>
    /// <param name="typeNameWithGenerics">The emitted C# type name to use in signatures.</param>
    /// <param name="typeDatabase">Type database used to resolve the raw value's C# type.</param>
    /// <param name="isReferenceType">True when the struct projects as a C# class (needs null guards).</param>
    /// <param name="emittedOperatorSymbols">Operator symbols already emitted for this type.</param>
    /// <param name="reservedPropertyNames">Property and nested-type names already claimed on this type — one half of the <c>Contains</c> collision guard; emitted method names are checked separately against <paramref name="structDecl"/>.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    public static void EmitIfOptionSet(
        CSharpWriter csWriter,
        StructDecl structDecl,
        string typeNameWithGenerics,
        ITypeDatabase typeDatabase,
        bool isReferenceType,
        IReadOnlySet<string> emittedOperatorSymbols,
        IReadOnlySet<string> reservedPropertyNames,
        ILogger logger)
    {
        // Match the standard library's protocol specifically. A bare name match would also fire on
        // a third-party protocol that happens to be called `OptionSet`, which promises none of the
        // bitwise semantics synthesized below. The empty-module arm mirrors the `Hashable` checks
        // elsewhere in the emitter: a conformance whose protocol identity came from the
        // printedName fallback rather than a mangled name carries no module.
        if (!structDecl.Conformances.Any(c =>
                c.Protocol.ModuleQualifiedName == "Swift.OptionSet" ||
                (c.Protocol.Name == "OptionSet" && string.IsNullOrEmpty(c.Protocol.Module))))
            return;

        // Swift does allow a generic OptionSet (`struct Tagged<T>: OptionSet` over a concrete raw
        // value), so this is a deliberate surface omission, not an impossible shape. It is declined
        // because nothing in the corpus exercises the generic arm end to end, and emitting operators
        // that don't compile for a whole category is worse than leaving the RawValue workaround in
        // place. Reopen with a fixture that binds the generic shape first.
        if (structDecl.IsGeneric)
            return;

        var rawValueProperty = structDecl.Properties.FirstOrDefault(p =>
            p.Name == "rawValue" && !p.IsStatic && p.WasEmitted && p.EmittedCSharpName is not null);
        if (rawValueProperty is null)
            return;

        if (!typeDatabase.TryGetTypeRecord(rawValueProperty.SwiftTypeSpec, out var rawTypeRecord))
            return;

        // The property is emitted with the narrowed type (Swift `Int` → `int`), so that is what
        // reading `RawValue` yields on the left of every expression below.
        var rawCSharpType = NativeIntOverloadEmitter.NarrowNativeIntType(rawTypeRecord.CSharpTypeName.FullyQualifiedName);
        if (!IntegralRawTypes.Contains(rawCSharpType))
            return;

        var rawValueName = rawValueProperty.EmittedCSharpName!;

        // The initializer has to have survived emission too — `new T(raw)` is the only way these
        // bodies can produce a value. CSSignature[0] is the return type, so a single-argument
        // initializer has exactly two entries. Its parameter type must be the property's Swift
        // type: a type is free to declare additional `init(rawValue:)` overloads on wider integers,
        // and picking one of those would build the option set through an unrelated initializer.
        var rawValueInitializer = structDecl.Methods.FirstOrDefault(m =>
            m.IsConstructor && m.WasEmitted && m.CSSignature.Count == 2 &&
            (m.CSSignature[1].Name == "rawValue" || m.CSSignature[1].PrivateName == "rawValue") &&
            m.CSSignature[1].SwiftTypeSpec.ToString() == rawValueProperty.SwiftTypeSpec.ToString());
        if (rawValueInitializer is null)
            return;

        // Initializer parameters are NOT narrowed the way properties are, so even for that one
        // Swift type the argument is wider than what `RawValue` returns (Swift `Int` → `nint`
        // parameter but `int` property). The `new` expression therefore casts to the PARAMETER's
        // type, not the property's: handing `new T(...)` an `int` and letting C# widen it
        // implicitly makes the call ambiguous with the projection's own `T(SwiftHandle)`
        // constructor, which an `int` also reaches (standard conversion to the handle's underlying
        // integer, then one user-defined conversion) — CS0121, and only where the two differ.
        //
        // Reading through the narrowed property means an option declared above bit 31 of a Swift
        // `Int`/`UInt` raw value is already gone before it reaches these bodies. That loss belongs
        // to the property projection, not to this synthesis: the hand-written combination a
        // consumer would otherwise write over the same `RawValue` truncates identically.
        var initArgCSharpType = rawTypeRecord.CSharpTypeName.FullyQualifiedName;
        if (!IntegralRawTypes.Contains(initArgCSharpType))
            return;

        var declaredOperatorSymbols = new HashSet<string>(
            structDecl.Operators.Select(o => o.OperatorSymbol), StringComparer.Ordinal);

        bool IsAvailable(string symbol) =>
            !emittedOperatorSymbols.Contains(symbol) && !declaredOperatorSymbols.Contains(symbol);

        // `unchecked` so a full-width complement or a byte/short combination that overflows the
        // narrow raw type wraps instead of throwing under `<CheckForOverflowUnderflow>`.
        string Combine(string expression) => $"unchecked(({initArgCSharpType})({expression}))";

        int emittedCount = 0;

        foreach (var (symbol, summary) in BinaryOperators)
        {
            if (!IsAvailable(symbol))
                continue;

            csWriter.WriteLine();
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine($"/// {summary}");
            csWriter.WriteLine("/// </summary>");
            csWriter.WriteLine($"public static {typeNameWithGenerics} operator {symbol}({typeNameWithGenerics} left, {typeNameWithGenerics} right)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            if (isReferenceType)
            {
                csWriter.WriteLine("global::System.ArgumentNullException.ThrowIfNull(left);");
                csWriter.WriteLine("global::System.ArgumentNullException.ThrowIfNull(right);");
            }
            csWriter.WriteLine($"return new {typeNameWithGenerics}({Combine($"left.{rawValueName} {symbol} right.{rawValueName}")});");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            emittedCount++;
        }

        if (IsAvailable("~"))
        {
            csWriter.WriteLine();
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine("/// Returns the complement — every option representable in the raw value that is not in this set.");
            csWriter.WriteLine("/// </summary>");
            csWriter.WriteLine($"public static {typeNameWithGenerics} operator ~({typeNameWithGenerics} value)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            if (isReferenceType)
                csWriter.WriteLine("global::System.ArgumentNullException.ThrowIfNull(value);");
            csWriter.WriteLine($"return new {typeNameWithGenerics}({Combine($"~value.{rawValueName}")});");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            emittedCount++;
        }

        // `Contains` is the readable form of the `(a & b) == b` membership test. Skipped when the
        // name is already taken — a Swift `contains` that bound on its own, or any other member
        // projecting to the same C# name, would be a CS0111 duplicate.
        bool containsNameTaken = reservedPropertyNames.Contains("Contains") ||
            structDecl.Methods.Any(m => m.WasEmitted && m.EmittedCSharpName == "Contains");
        if (!containsNameTaken)
        {
            csWriter.WriteLine();
            csWriter.WriteLine("/// <summary>");
            csWriter.WriteLine("/// Returns true when every option in <paramref name=\"other\"/> is present in this set.");
            csWriter.WriteLine("/// </summary>");
            csWriter.WriteLine($"public bool Contains({typeNameWithGenerics} other)");
            csWriter.WriteLine("{");
            csWriter.Indent++;
            if (isReferenceType)
                csWriter.WriteLine("global::System.ArgumentNullException.ThrowIfNull(other);");
            csWriter.WriteLine($"return ({rawValueName} & other.{rawValueName}) == other.{rawValueName};");
            csWriter.Indent--;
            csWriter.WriteLine("}");
            emittedCount++;
        }

        if (emittedCount > 0)
            logger.LogInformation($"Synthesized {emittedCount} OptionSet bitwise member(s) for '{structDecl.Name}'.");
    }
}
