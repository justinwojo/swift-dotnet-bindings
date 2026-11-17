// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration;

/// <summary>
/// C# expressions that read an object's native pointer for a call into Swift and refuse a
/// disposed object before native code runs.
/// </summary>
/// <remarks>
/// A disposed wrapper's handle reads zero: <c>NSObject.Handle</c> after Dispose, and
/// <c>SafeHandle.DangerousGetHandle()</c> once the handle is released. Reading it raw sends a
/// null <c>self</c> or a null non-optional argument into Swift, and turns a disposed optional
/// argument into <c>nil</c>. Every read that is not already inside a <c>DangerousAddRef</c>
/// bracket goes through one of these, which throw <see cref="System.ObjectDisposedException"/>
/// (or <see cref="System.ArgumentNullException"/> for a null non-optional argument) instead.
/// </remarks>
internal static class LiveHandleExpressions
{
    private const string Guard = "global::Swift.Runtime.SwiftHandleGuard";

    /// <summary>Self of an Objective-C-rooted object (<paramref name="receiver"/> is an INativeObject).</summary>
    public static string ObjCSelf(string receiver = "this")
        => $"{Guard}.GetLiveObjCHandle({receiver})";

    /// <summary>A non-optional Objective-C object argument: null and disposed are both refused.</summary>
    public static string ObjCArgument(string expression, string parameterName)
        => $"{Guard}.GetLiveObjCArgumentHandle({expression}, \"{DisplayName(parameterName)}\")";

    /// <summary>An optional Objective-C object argument: null is <c>nil</c>, disposed is refused.</summary>
    public static string ObjCOptionalArgument(string expression)
        => $"{Guard}.GetLiveObjCHandleOrZero({expression})";

    /// <summary>The pointer a SafeHandle-typed handle or payload holds (e.g. <c>_handle</c>, <c>x.Payload</c>).</summary>
    /// <remarks>
    /// The cast picks the SafeHandle overload: a key path is both a SafeHandle and an
    /// <c>ISwiftObject</c>, which makes the bare call ambiguous (CS0121).
    /// </remarks>
    public static string SafeHandle(string handleExpression)
        => $"{Guard}.GetLiveHandle((global::System.Runtime.InteropServices.SafeHandle)({handleExpression}))";

    /// <summary>Self of any generated Swift object, read through <c>ISwiftObject.SwiftHandle</c>.</summary>
    public static string SwiftObject(string expression)
        => $"{Guard}.GetLiveHandle((global::Swift.Runtime.ISwiftObject){expression})";

    /// <summary>A non-optional Swift object argument: null and disposed are both refused.</summary>
    public static string SwiftObjectArgument(string expression, string parameterName)
        => $"{Guard}.GetLiveArgumentHandle({expression}, \"{DisplayName(parameterName)}\")";

    /// <summary>An optional Swift object argument: null is <c>nil</c>, disposed is refused.</summary>
    public static string SwiftObjectOptionalArgument(string expression)
        => $"{Guard}.GetLiveHandleOrZero({expression})";

    // A verbatim identifier (`@class`) names the parameter `class`.
    private static string DisplayName(string parameterName) => parameterName.TrimStart('@');
}
