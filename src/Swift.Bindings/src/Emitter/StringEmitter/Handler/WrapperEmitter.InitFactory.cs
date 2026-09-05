// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    internal partial class WrapperEmitter
    {
        /// <summary>
        /// True while the constructor emission path is writing a STATIC FACTORY instead of a constructor.
        ///
        /// <para>Two Swift initializers that differ only by argument label —
        /// <c>init(paymentIntentClientSecret:configuration:)</c> beside
        /// <c>init(setupIntentClientSecret:configuration:)</c> — project to one C# constructor signature.
        /// They are different operations, so keeping the first and dropping the second deletes half the
        /// type's construction surface. A constructor's C# name is the type's and cannot be changed, but a
        /// static factory's can, so the colliding member is recovered as <c>CreateWith{Labels}</c>. The
        /// call, the marshalling and the cleanup are byte-identical to the constructor's — only the
        /// declaration line and the terminal differ — so this flag steers the shared body rather than
        /// duplicating it.</para>
        /// </summary>
        private bool _emittingInitFactory;

        /// <summary>The local the factory body builds and returns; unused while emitting a constructor.</summary>
        private string _initFactoryResultName = "__created";

        /// <summary>
        /// What the shared constructor body calls the instance it just built: <c>this</c> in a constructor,
        /// the result local in a factory. Only the dispose-scope registration needs it — a constructor's
        /// other instance writes go through <see cref="EmitReturnConstructor"/>, which has its own arm.
        /// </summary>
        private string InitFactoryInstanceExpression => _emittingInitFactory ? _initFactoryResultName : "this";

        /// <summary>
        /// Whether this initializer's parent shape can express the constructor's work as a static factory.
        ///
        /// <para>A factory hands back a finished instance from a STATIC body, so it needs a terminal that
        /// writes no instance state. A class has one — the internal handle-taking constructor, which adopts
        /// the Swift initializer's +1 exactly as the constructor's own <c>_handle = new SwiftClassHandle…</c>
        /// does — and so does a frozen blittable struct, whose value is simply returned. A non-frozen struct
        /// and a frozen struct projected as a class do not: their indirect-result setup allocates into the
        /// <c>_payload</c> instance field BEFORE the call, and the handle-taking constructor of a
        /// frozen-struct-as-class COPIES its buffer rather than adopting it, so a factory built on it would
        /// leak the source's +1. Those shapes keep the pre-existing outcome — the first claimant emits as
        /// the constructor, the rest are recorded as duplicate signatures — rather than trading a dropped
        /// member for a leaking one.</para>
        ///
        /// <para>Checked at the emission site rather than only at name resolution because the indirect-result
        /// decision is an emitter-side fact: a shape whose terminal this method cannot name would otherwise
        /// leave the result local unassigned (a compile error in the generated binding instead of an honest
        /// skip).</para>
        /// </summary>
        internal bool CanEmitInitFactory()
        {
            switch (_env.ParentDecl)
            {
                case ClassDecl:
                    // Both class arms deliver the instance pointer through a local: the ObjC-rooted helper
                    // returns a NativeHandle, the Swift-native call returns the pointer in a register.
                    return true;
                case StructDecl structDecl:
                {
                    var typeRecord = _env.TypeDatabase.GetTypeRecordOrThrow(structDecl.SwiftTypeName);
                    if (!MarshallingHelpers.IsTypeFrozen(typeRecord) || MarshallingHelpers.RequiresMemoryManagement(typeRecord))
                        return false;
                    // A frozen blittable struct arrives either directly in the return local or, through a
                    // @_cdecl wrapper, in the pre-declared _cdeclResult. Any other indirect arrangement has
                    // no terminal expression here.
                    return !_requiresIndirectResult
                        || (_env.MethodDecl.UsesCdeclConstructorWrapper && structDecl.IsFrozen);
                }
                default:
                    return false;
            }
        }

        /// <summary>
        /// Emits a colliding non-failable initializer as a static factory. Reuses the constructor body
        /// wholesale; see <see cref="_emittingInitFactory"/> for why the two are one path.
        /// </summary>
        internal void EmitInitFactory(CSharpWriter csWriter)
        {
            _emittingInitFactory = true;
            _initFactoryResultName = ResolveInitFactoryResultName();
            try
            {
                if (_env.ParentDecl is ClassDecl objCRooted && objCRooted.IsObjCRooted)
                    EmitObjCRootedInitFactory(csWriter);
                else
                    EmitConstructorCore(csWriter);
            }
            finally
            {
                _emittingInitFactory = false;
            }
        }

        /// <summary>
        /// The ObjC-rooted factory: the same <c>CreateSwiftInstance_…</c> helper the constructor uses, then
        /// a tail that wraps its handle. The internal <c>(SwiftHandle)</c> constructor performs the
        /// <c>DangerousRelease</c> that balances NSObject's retain against the initializer's +1, so the
        /// factory must not repeat it.
        /// </summary>
        private void EmitObjCRootedInitFactory(CSharpWriter csWriter)
        {
            EmitObjCRootedStaticHelper(csWriter);

            var helperName = $"CreateSwiftInstance_{NameProvider.GetPInvokeName(_env.EmissionSymbol, (MethodDecl)_env.MethodDecl)}";
            var paramArgs = string.Join(", ", _wrapperSignature.Parameters.Select(p => p.Name));

            EmitFallbackAttribute(csWriter);
            var preSignatureCheckpoint = csWriter.Checkpoint();
            AvailabilityAttributeEmitter.EmitAvailabilityAttributes(csWriter, _env.MethodDecl, _env.ParentDecl, emitObsolete: false);
            EmitSafetyObsolete(csWriter);
            XmlDocCommentEmitter.EmitMethodDocComment(csWriter, _env.MethodDecl, isConstructor: true);
            EmitMainActorMemberAnnotation(csWriter);
            EmitSignatureConstructor(csWriter);
            EmitBodyStart(csWriter);
            // No main-thread guard here: the Swift init already ran inside the static helper, which carries
            // the guard at its top.
            csWriter.WriteLine($"return new {GetInitFactoryReturnTypeName()}((SwiftHandle)(IntPtr){helperName}({paramArgs}));");
            EmitBodyEnd(csWriter);
            AssertRawBufferFixedDepthZero();
            InjectConsumeDegradedMarker(csWriter, preSignatureCheckpoint);
        }

        /// <summary>Declares the factory's result local ahead of the try block that assigns it.</summary>
        private void EmitInitFactoryResultDeclaration(CSharpWriter csWriter)
        {
            if (!_emittingInitFactory)
                return;
            csWriter.WriteLine($"{GetInitFactoryReturnTypeName()} {_initFactoryResultName};");
        }

        /// <summary>Returns the instance the shared body built.</summary>
        private void EmitInitFactoryReturn(CSharpWriter csWriter)
        {
            if (!_emittingInitFactory)
                return;
            csWriter.WriteLine($"return {_initFactoryResultName};");
        }

        /// <summary>
        /// The factory's declaration line, standing in for the constructor's. Records the emitted API shape
        /// under the factory's own name and parameter list, so the manifest describes the member a consumer
        /// can actually call rather than a constructor that was never written.
        /// </summary>
        private void EmitSignatureInitFactory(CSharpWriter csWriter, string accessModifier)
        {
            var typeName = GetInitFactoryReturnTypeName();
            var factoryName = _env.InitFactoryName!;

            _emissionContext.RecordEmittedApiShape(
                _env.MethodDecl,
                csharpName: factoryName,
                parameterPortion: BuildEmittedParameterPortion());

            csWriter.WriteLine($"{accessModifier} static {typeName} {factoryName}({_wrapperSignature.ParametersString(BuildOriginalSwiftTypeAttributes())})");
        }

        /// <summary>
        /// Assigns the built instance to the factory's result local. Mirrors
        /// <see cref="EmitReturnConstructor"/>'s terminals one for one — a class adopts the returned +1
        /// through its internal handle constructor (which is literally the constructor's
        /// <c>_handle = new SwiftClassHandle…</c> body), and a frozen blittable struct is copied out of the
        /// return location. Shapes with no such terminal never reach here: <see cref="CanEmitInitFactory"/>
        /// declines them at the emission site.
        /// </summary>
        private void EmitReturnInitFactory(CSharpWriter csWriter)
        {
            var typeName = GetInitFactoryReturnTypeName();

            if (_env.ParentDecl is ClassDecl)
            {
                csWriter.WriteLine($"{_initFactoryResultName} = new {typeName}((SwiftHandle){ReturnLocalName});");
                return;
            }

            if (_requiresIndirectResult)
                csWriter.WriteLine($"{_initFactoryResultName} = _cdeclResult;");
            else
                csWriter.WriteLine($"{_initFactoryResultName} = {ReturnLocalName};");
        }

        /// <summary>
        /// The factory's return type. A generic parent must carry its parameter list — the bare leaf name
        /// has the wrong arity and binds to nothing (or, when a namespace shares the type's name, resolves
        /// to the namespace instead).
        /// </summary>
        private string GetInitFactoryReturnTypeName()
        {
            var typeName = GetResolvedTypeName();
            if (_env.ParentDecl is TypeDecl parentTypeDecl && parentTypeDecl.IsGeneric)
                typeName += GenericTypeEmitter.GetGenericParameterList(parentTypeDecl);
            return typeName;
        }

        /// <summary>
        /// Picks a result-local name no projected parameter has taken. The double underscore already puts it
        /// outside the Swift-label namespace; the walk closes the residual rather than trusting that.
        /// </summary>
        private string ResolveInitFactoryResultName()
        {
            var taken = new HashSet<string>(
                _env.MethodDecl.CSSignature.Skip(1).Select(NameProvider.GetCSharpParameterName));
            var name = "__created";
            for (var i = 1; taken.Contains(name); i++)
                name = $"__created{i}";
            return name;
        }
    }
}
