// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.CodeDom.Compiler;

namespace BindingsGeneration
{
    /// <summary>
    /// Emits a Swift "namespace facade" type as a real C# nested namespace
    /// instead of the default <c>partial class</c> / <c>static partial class</c>
    /// emission. The outer module namespace is already open at the call site
    /// (<c>namespace BlinkID { … }</c>), so this emitter writes a
    /// <c>namespace {Name} { … }</c> block at the current indent — the
    /// resulting output compiles identically to <c>namespace BlinkID.{Name}</c>.
    ///
    /// The emitter recurses into <see cref="TypeDecl.Types"/> via
    /// <see cref="IHandler.HandleBaseDecl"/> so nested struct/enum/class
    /// declarations route through their normal handlers and emit at the
    /// new namespace level (i.e. as top-level types in the lifted
    /// namespace), while still benefiting from the standard
    /// <c>PushTypeNesting</c>/<c>PopTypeNesting</c> bookkeeping the Swift
    /// wrapper generator uses to build module-qualified Swift names like
    /// <c>BlinkID.BlinkIDSDK.StringResult</c>.
    ///
    /// See <see cref="NamespaceFacadeDetector.IsNamespaceFacade(TypeDecl)"/>
    /// for the predicate gate. The canonical case is BlinkID 7.7.0's
    /// <c>BlinkIDSDK</c> outer struct containing ~25 nested types.
    /// </summary>
    internal static class NamespaceFacadeEmitter
    {
        /// <summary>
        /// Emits the namespace block. The caller must already be writing
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

            // Note: C# does not allow XML doc comments or attributes (e.g.
            // `[SupportedOSPlatform]`) on namespace declarations — attaching
            // them produces CS0116 ("namespace cannot directly contain
            // members"). Availability annotations from the facade type are
            // intentionally dropped at this scope; the same annotations
            // re-attach to each nested type through its own handler, which
            // is where consumers see them in IDE tooling anyway.
            csWriter.WriteLine($"namespace {typeNameWithGenerics}");
            csWriter.WriteLine("{");
            csWriter.Indent++;

            // Push the facade name onto the type-nesting stack so nested-type
            // Swift wrappers see `BlinkID.BlinkIDSDK.NestedType` when building
            // module-qualified Swift identifiers — matching the `swiftinterface`
            // declaration and the Swift mangled-symbol path. Without this push,
            // wrappers for nested types reference `BlinkID.NestedType`, which
            // doesn't resolve at the Swift @_cdecl wrapper compile step.
            var emissionCtx = context.GetEmissionContext();
            emissionCtx?.PushTypeNesting(typeNameWithGenerics);
            recurse(typeDecl.Types, context);
            emissionCtx?.PopTypeNesting();

            csWriter.Indent--;
            csWriter.WriteLine("}");
            csWriter.WriteLine();
        }
    }
}
