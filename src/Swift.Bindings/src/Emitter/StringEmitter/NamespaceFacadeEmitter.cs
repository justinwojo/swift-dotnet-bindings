// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Emits a Swift "namespace facade" struct as a <c>public static partial class</c> that hosts
    /// its nested types, the same shape a caseless namespace enum takes
    /// (<c>EnumHandler.EmitNamespaceEnum</c>). The facade has no member surface, so the class
    /// carries nothing but the nested types, its doc comment and its availability.
    ///
    /// <para>The facade used to be lifted to a C# namespace. That put its nested types at
    /// <c>Module.Facade.Inner</c>, and the first member a newer SDK gave the struct sent it down
    /// the struct handler and moved them to <c>Module.Facade+Inner</c>: a different CLR type to
    /// every assembly compiled against the older copy, so the copies of a package built with two
    /// SDKs could not merge. As a class the nested types are <c>Module.Facade+Inner</c> whichever
    /// members an SDK declares. Consumers import them with <c>using static Module.Facade;</c>.</para>
    ///
    /// <para>The emitter recurses into <see cref="TypeDecl.Types"/> via
    /// <see cref="IHandler.HandleBaseDecl"/> so nested declarations route through their normal
    /// handlers, inside the standard <c>PushTypeNesting</c>/<c>PopTypeNesting</c> bookkeeping the
    /// Swift wrapper generator uses to build module-qualified Swift names.</para>
    ///
    /// See <see cref="NamespaceFacadeDetector.IsNamespaceFacade(TypeDecl)"/> for the predicate gate.
    /// </summary>
    internal static class NamespaceFacadeEmitter
    {
        /// <summary>
        /// Emits the static class. The caller must already be writing
        /// inside the module namespace; the emitter recurses via
        /// <paramref name="recurse"/> to drive nested-type emission through
        /// the normal handler dispatch path.
        /// </summary>
        /// <param name="csWriter">The C# writer (positioned inside the outer module namespace).</param>
        /// <param name="swiftWriter">The Swift writer for nested wrapper emission.</param>
        /// <param name="typeDecl">The facade type declaration.</param>
        /// <param name="conductor">The handler conductor for nested dispatch.</param>
        /// <param name="typeDatabase">The type database for nested marshal/emit.</param>
        /// <param name="context">The current type handler context.</param>
        /// <param name="recurse">Callback that drives <c>HandleBaseDecl</c> on the nested decls.</param>
        public static void Emit(
            CSharpWriter csWriter,
            SwiftWriter swiftWriter,
            TypeDecl typeDecl,
            Conductor conductor,
            ITypeDatabase typeDatabase,
            TypeHandlerContext context,
            System.Action<IEnumerable<BaseDecl>, TypeHandlerContext> recurse)
        {
            ReportCollector.RecordTypeEmitted(typeDecl);

            var typeNameWithGenerics = GenericTypeEmitter.GetTypeNameWithGenerics(typeDecl, typeDatabase);

            XmlDocCommentEmitter.EmitDocComment(csWriter, typeDecl);
            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, typeDecl, emitObsolete: true);
            if (SwiftDeclSpelling.HasUnderscoreSwiftName(typeDecl))
                csWriter.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
            csWriter.WriteLine($"public static partial class {typeNameWithGenerics}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Push the facade name onto the type-nesting stack so nested-type
            // Swift wrappers see `Module.FacadeName.NestedType` when building
            // module-qualified Swift identifiers — matching the `swiftinterface`
            // declaration and the Swift mangled-symbol path. Without this push,
            // wrappers for nested types reference `Module.NestedType`, which
            // doesn't resolve at the Swift @_cdecl wrapper compile step.
            var emissionCtx = context.GetEmissionContext();
            emissionCtx?.PushTypeNesting(typeNameWithGenerics);
            recurse(typeDecl.Types, context);
            emissionCtx?.PopTypeNesting();

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();

            // The extension classes its nested types queued (CS1109 keeps them at namespace
            // scope) follow the facade inside its own file span, as after any outermost type.
            NamespaceScopeEmission.DrainAfterOutermost(csWriter, typeDecl, context);
        }
    }
}
