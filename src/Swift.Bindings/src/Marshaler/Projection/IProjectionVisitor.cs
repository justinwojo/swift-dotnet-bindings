// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// Visitor pattern for compile-time exhaustive dispatch over projection types.
/// Adding a new projection type without implementing all visitors causes a compile error.
/// </summary>
public interface IProjectionVisitor<T>
{
    T Visit(StringProjection p);
    T Visit(BlittableProjection p);
    T Visit(BoolProjection p);
    T Visit(SimpleEnumProjection p);
    T Visit(ClassProjection p);
    T Visit(NonFrozenStructProjection p);
    T Visit(FrozenWithMemoryProjection p);
    T Visit(ArrayProjection p);
    T Visit(DictionaryProjection p);
    T Visit(SetProjection p);
    T Visit(DataProjection p);
    T Visit(OptionalProjection p);
    T Visit(ExistentialProjection p);
    T Visit(ClosureProjection p);
    T Visit(AsyncProjection p);
    T Visit(ObjCBridgedProjection p);
    T Visit(ObjCBridgeableProjection p);
    T Visit(ObjCRootedClassProjection p);
    T Visit(NativeRemappedProjection p);
    T Visit(TupleProjection p);
    T Visit(DateProjection p);
    T Visit(ResultProjection p);
    T Visit(KeyPathProjection p);
}
