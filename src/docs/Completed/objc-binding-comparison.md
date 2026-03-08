# ObjC Binding Comparison: Our Generator vs Objective Sharpie

**Date:** 2026-03-07
**Sharpie Version:** 3.5.116
**Our Generator:** swift-bindings (current main branch, commit f2da98a)
**Xcode:** 26.2 (iphoneos26.2 SDK)

## Libraries Tested

| Library | Type | Headers | Notes |
|---------|------|---------|-------|
| BlinkID V6 | Pure ObjC (ID scanning) | 191 | Large SDK, many delegates/protocols |
| BRLMPrinterKit | Pure ObjC (Brother printers) | 286 | Hardware SDK, many settings classes |
| Realm | Pure ObjC (database) | 640 | Complex generics, categories, extern constants |
| Stripe3DS2 | ObjC (3D Secure) | 165 | UI customization protocols |
| FirebaseMessaging | ObjC (push notifications) | 5 | Small, clean API surface |

---

## Summary Scorecard

| Dimension | Our Tool | Sharpie | Winner |
|-----------|----------|---------|--------|
| Compilation success | 5/5 | N/A (needs manual fixes) | **Ours** |
| Doc comments | Rich XML `<summary>` + `<param>` | None (only raw ObjC header lines) | **Ours** |
| [Verify] attributes (manual work) | 0 | 119 total across 5 libs | **Ours** |
| Project scaffolding | Full .csproj + .targets + metadata | Nothing | **Ours** |
| Enum member naming | Prefix-stripped (C#-idiomatic) | Type-prefixed or inconsistent | **Ours** |
| Enum backing types | Preserves `: int`, `: long`, `: ulong` | Sometimes omits backing type | **Ours** |
| Enum explicit values | Preserves explicit values | Preserves explicit values | Tie |
| Typed arrays/generics | Preserves `NSArray<T>` as `T[]` | Typed `T[]` / `NSDictionary<K,V>` | Tie |
| Optional vs required protocol members | Correct `[Abstract]` on `@required` only | Correctly distinguishes @optional/@required | Tie |
| [DesignatedInitializer] | Emitted where declared | Emitted where declared | Tie |
| Platform type stub conflicts | Filtered out (uses SDK types) | Uses platform namespace types | Tie |
| Platform availability ([iOS(x,y)]) | Emitted from ObjC annotations | Emitted from ObjC annotations | Tie |
| P/Invoke pointer params | `out T` for value-type output params | Correct unsafe pointers | Tie |
| Variadic method handling | `[Internal]` + `IsVariadic = true` | `[Internal]` + `IsVariadic` | Tie |
| ObjC method out-params | `out T` / `ref T` for out-params | `bool*` / unsafe pointer | Tie |
| Foreign-type categories | Emitted with `[Category]` (methods only) | Emitted with `[Category]` | Comparable |
| [NullAllowed] accuracy | 601 total | 645 total | Comparable |
| WeakDelegate/Wrap pattern | Emitted on delegate protocols | Emitted correctly | Tie |
| [Model] on protocols | Emitted on delegate protocols | Emitted on delegate protocols | Tie |
| ArgumentSemantic | Emitted (Copy/Assign/Weak/Strong) | Emitted (Copy/Assign/Weak/Strong) | Tie |
| [Bind] for custom getters | Emitted (e.g., `isAutoInitEnabled`) | Emitted (e.g., `isAutoInitEnabled`) | Tie |
| ObjC header comment lines | Not emitted | Preserved as `// @property ...` | **Sharpie** |
| [Field] constants | Grouped in `Constants` class in StructsAndEnums | Placed near related interfaces | Mixed |
| Extra [Export] coverage | 2823 total | 1796 total | **Ours** |
| Class/interface count | Higher (more protocols surfaced) | Lower | **Ours** |
| Delegate extraction | Named delegates in BgenDelegates.cs | Inline in ApiDefinitions | **Ours** |

---

## Detailed Findings

> **Note:** These findings describe the **initial comparison** before any fixes. Items addressed in Sessions 1 and 2 are marked as ✅ Done in the [Recommendations](#recommendations-for-our-generator) section below. The [Summary Scorecard](#summary-scorecard) reflects the current state after all fixes.

### 1. Documentation Quality

**Our tool wins decisively.** We generate rich XML doc comments from ObjC header documentation:

```csharp
// === Our output (FirebaseMessaging) ===
/// <summary>
/// Firebase Messaging lets you reliably deliver messages. To send or receive
/// messages, the app must get a registration token...
/// </summary>
[DisableDefaultCtor]
[BaseType(typeof(NSObject))]
partial interface FIRMessaging
{
    /// <summary>
    /// Set the APNs token for the application. This token will be used to register
    /// with Firebase Messaging...
    /// </summary>
    /// <param name="apnsToken">The APNs token for the application.</param>
    /// <param name="type">The type of APNs token.</param>
    [Export("setAPNSToken:type:")]
    void SetAPNSToken(NSData apnsToken, FIRMessagingAPNSTokenType type);
```

```csharp
// === Sharpie output (FirebaseMessaging) ===
// @interface FIRMessaging : NSObject
[BaseType(typeof(NSObject))]
[DisableDefaultCtor]
interface FIRMessaging
{
    // -(void)setAPNSToken:(NSData *)apnsToken type:(FIRMessagingAPNSTokenType)type;
    [Export("setAPNSToken:type:")]
    void SetAPNSToken(NSData apnsToken, FIRMessagingAPNSTokenType type);
```

Sharpie preserves only the raw ObjC declaration as a comment (`// @interface ...`), with no structured documentation. Our tool produces proper `<summary>` and `<param>` tags that show up in IDE tooltips and generated API docs.

**Doc comment counts:**

| Library | Ours | Sharpie |
|---------|------|---------|
| BlinkID V6 | 3098 | 0 (1159 raw ObjC lines) |
| BRLMPrinterKit | 100 | 0 (403 raw ObjC lines) |
| Realm | 3208 | 0 (917 raw ObjC lines) |
| Stripe3DS2 | 507 | 0 (175 raw ObjC lines) |
| FirebaseMessaging | 94 | 0 (33 raw ObjC lines) |

### 2. Zero [Verify] Attributes

Sharpie emits `[Verify]` attributes that **intentionally cause build failures** until a human reviews each one. These represent uncertain bindings that need manual verification. Our tool resolves all ambiguities at generation time.

| Library | Sharpie [Verify] count |
|---------|----------------------|
| BlinkID V6 | 41 |
| BRLMPrinterKit | 21 |
| Realm | 45 |
| Stripe3DS2 | 9 |
| FirebaseMessaging | 3 |
| **Total** | **119** |

Our tool: **0** across all libraries.

### 3. Compilation Results

Our tool emits a ready-to-build `.csproj`:

| Library | Errors | Warnings | Compiles? |
|---------|--------|----------|-----------|
| BlinkID V6 | 4 | 5 | No (dup ctors) |
| BRLMPrinterKit | 0 | 103 | **Yes** |
| Realm | 0 | 156 | **Yes** |
| Stripe3DS2 | 0 | 16 | **Yes** |
| FirebaseMessaging | 0 | 1 | **Yes** |

The BlinkID V6 errors are duplicate constructor definitions on 4 UIView subclasses (`MBGlareStatusSubview`, `MBTapToFocusSubview`, `MBDocumentSubview`, `MBDotsSubview`) — a known edge case with multiple `initWithFrame:` / `initWithCoder:` paths.

Sharpie output **cannot compile at all** without first removing all 119 `[Verify]` attributes manually. Even after that, additional manual work is needed (delegate stubs, type corrections, etc).

Warnings in our output are mostly `CS0114` (hiding inherited members, needs `new`/`override`) and `CS8618` (non-nullable constants not initialized in constructor). These are cosmetic and don't affect runtime behavior.

### 4. Project Scaffolding

Our tool emits a complete build-ready package:

| Artifact | Our Tool | Sharpie |
|----------|----------|---------|
| ApiDefinition.cs | Yes | Yes |
| StructsAndEnums.cs | Yes | Yes |
| BgenDelegates.cs | Yes (extracted delegates) | No |
| Module.cs (DllImportResolver) | Yes | No |
| .csproj (binding project) | Yes | No |
| .targets (consumer integration) | Yes | No |
| binding-metadata.json | Yes | No |
| binding-metadata.props | Yes | No |
| dependency-manifest.json | Yes | No |
| .tbd (text-based stub) | Yes (when available) | No |

### 5. Enum Member Naming

Our tool strips the ObjC type prefix from enum members, producing idiomatic C#:

```csharp
// === Our output ===
public enum MBCameraPreset : long
{
    _480p,      // Leading digit → underscore prefix
    _720p,
    _1080p,
    _4K,
    Optimal,
    Max,
    Photo,
}
```

```csharp
// === Sharpie output ===
public enum MBCameraPreset : long
{
    MBCameraPreset480p,    // Keeps full ObjC prefix
    MBCameraPreset720p,
    MBCameraPreset1080p,
    MBCameraPreset4K,
    Optimal,               // Only strips when coincidental
    Max,
    Photo
}
```

Our approach is more C#-native. Using `MBCameraPreset.Optimal` reads better than `MBCameraPreset.MBCameraPreset480p`.

Notably, **Sharpie has a prefix-stripping bug** in BlinkID's `MBDataMatchField` — overly aggressive stripping produces `ateOfBirth`, `ateOfExpiry`, `ocumentNumber` (first character truncated). Our tool correctly produces `DateOfBirth`, `DateOfExpiry`, `DocumentNumber`.

Both tools correctly match enum backing types (`: int`, `: long`, `: ulong`) and `[Native]` attributes for `NS_ENUM`/`NS_OPTIONS` declarations.

**Critical bug: Missing explicit enum values.** For enums with non-sequential values, our tool emits implicit sequential numbering starting at 0, while Sharpie preserves the actual ObjC values. This is a **correctness bug** that causes runtime value mismatches:

```csharp
// === Our output (Stripe3DS2) ===
public enum STDSErrorCode : long
{
    AssertionFailed,     // Implicit: 0 (WRONG — should be 204)
    UnrecognizedID,      // Implicit: 1 (WRONG — should be 203)
    RuntimeParsing,      // Implicit: 2 (WRONG — should be 201)
    // ...
}

// === Sharpie output (correct) ===
public enum STDSErrorCode : long
{
    AssertionFailed = 204,
    UnrecognizedID = 203,
    RuntimeParsing = 201,
    RuntimeErrorEvent = 202,
    UnrecognizedCriticalMessageExtension = 302,
    // ...
}
```

Similarly in BRLMPrinterKit, `BRLMGetStatusErrorCode` starts at 20000 in ObjC but our tool emits it starting at 0. This affects at least 10+ enums across the tested libraries. (Note: some enums like `RLMPropertyType` with explicit `= 0, = 1, = 5` are handled correctly by our tool.)

### 6. Protocol Handling (Major Difference)

This is the most significant semantic difference between the tools.

**Sharpie** emits `[Protocol, Model]` which creates a concrete base class that can be subclassed in C#:

```csharp
// === Sharpie ===
[Protocol, Model]
[BaseType(typeof(NSObject))]
interface FIRMessagingDelegate
{
    [Export("messaging:didReceiveRegistrationToken:")]
    void DidReceiveRegistrationToken(FIRMessaging messaging, [NullAllowed] string fcmToken);
}

// Usage in parent class:
[Wrap("WeakDelegate")]
[NullAllowed]
FIRMessagingDelegate Delegate { get; set; }

[NullAllowed, Export("delegate", ArgumentSemantic.Weak)]
NSObject WeakDelegate { get; set; }
```

**Our tool** emits `[Protocol]` only (no `[Model]`), uses the `I` prefix convention, and doesn't emit the WeakDelegate/Wrap pattern:

```csharp
// === Our output ===
[Protocol]
[BaseType(typeof(NSObject))]
partial interface IFIRMessagingDelegate
{
    [Abstract]
    [Export("messaging:didReceiveRegistrationToken:")]
    void Messaging(FIRMessaging messaging, [NullAllowed] string fcmToken);
}

// Usage in parent class:
[Export("delegate")]
[NullAllowed]
IFIRMessagingDelegate Delegate {
    get;
    [Export("setDelegate:")] set;
}
```

**Impact:**
- Without `[Model]`, delegate protocols become pure C# interfaces. Users must implement all abstract members — they can't partially override like with Sharpie's model classes.
- Without the `WeakDelegate`/`Wrap` pattern, the binding doesn't properly handle Objective-C's weak reference semantics for delegates, which can lead to premature garbage collection.
- Sharpie's approach matches the established Xamarin.iOS convention that the entire .NET ecosystem expects.

**Counts across all libraries:**

| Pattern | Our Tool | Sharpie |
|---------|----------|---------|
| [Protocol] only | 94 | 0 |
| [Protocol, Model] | 0 | 94 |
| WeakDelegate/Wrap pairs | 0 | 35 |
| [Bind("isXxx")] getter names | 0 | 29 |

### 7. ArgumentSemantic Annotations

Sharpie preserves Objective-C property memory semantics (`copy`, `assign`, `weak`, `strong`):

```csharp
// === Sharpie ===
[Export("APNSToken", ArgumentSemantic.Copy)]
NSData APNSToken { get; set; }

[NullAllowed, Export("delegate", ArgumentSemantic.Weak)]
NSObject WeakDelegate { get; set; }
```

```csharp
// === Our output ===
[Export("APNSToken")]
NSData APNSToken { get; set; }
```

| Library | Ours | Sharpie |
|---------|------|---------|
| BlinkID V6 | 0 | 293 |
| BRLMPrinterKit | 0 | 50 |
| Realm | 0 | 58 |
| Stripe3DS2 | 0 | 29 |
| FirebaseMessaging | 0 | 3 |

Missing `ArgumentSemantic` can cause subtle memory management bugs. For example, without `Copy` on an `NSString` property, the binding may hold a mutable reference that the caller later modifies. Without `Weak` on delegate properties, retain cycles can occur.

### 8. Category / Extension Handling

Sharpie correctly identifies ObjC categories and emits them with `[Category]`:

```csharp
// === Sharpie (Realm) ===
[Category]
[BaseType(typeof(RLMArray))]
interface RLMArray_Swift
{
    [Export("initWithObjectClassName:")]
    NativeHandle Constructor(string objectClassName);
}

[Category]
[BaseType(typeof(NSNull))]
interface NSNull_RLMValue : IRLMValue
{
}
```

Our tool merges category methods on the library's own types into the main class definition. For **foreign-type categories** — categories on platform-owned types like `NSNull`, `UIButton`, `NSNumber` — we emit `[Category]` interfaces with `[BaseType]` pointing to the platform type. Due to MAUI bgen constraints (categories compile as static extension classes), protocol conformance is stripped (CS0714) and instance properties are skipped (CS0708). Categories with no emittable methods or class properties are skipped with diagnostics.

Sharpie emits all categories uniformly, including protocol conformance declarations (e.g., `NSNull_RLMValue : IRLMValue`). Our tool strips these because static extension classes cannot implement interfaces — a MAUI bgen limitation, not a generator issue.

| Library | Our [Category] | Sharpie [Category] |
|---------|---------------|-------------------|
| BlinkID V6 | 0 | 0 |
| BRLMPrinterKit | 0 | 0 |
| Realm | 20 (methods only) | 34 (with protocols) |
| Stripe3DS2 | 0 | 0 |
| FirebaseMessaging | 1 | 1 |

### 9. Export Coverage

Interestingly, our tool generates significantly more `[Export]` bindings:

| Library | Ours | Sharpie | Delta |
|---------|------|---------|-------|
| BlinkID V6 | 1302 | 698 | +87% |
| BRLMPrinterKit | 500 | 309 | +62% |
| Realm | 826 | 652 | +27% |
| Stripe3DS2 | 170 | 117 | +45% |
| FirebaseMessaging | 25 | 20 | +25% |

This is because our tool:
1. Emits explicit `[Export("setX:")]` on property setters (Sharpie relies on implicit setter selectors)
2. Surfaces more protocol members (Sharpie may group some behind `[Model]` abstract implementations)
3. Emits inherited protocol members on conforming classes

### 10. Field / Extern Constant Handling

Both tools handle `[Field]` constants, but organize them differently:

**Sharpie:** Places `[Field]` properties inside a partial interface near the related class:
```csharp
// In ApiDefinitions.cs, inside a [Static] partial interface
[Field("RLMRealmRefreshRequiredNotification", "__Internal")]
NSString RLMRealmRefreshRequiredNotification { get; }
```

**Our tool:** Groups all constants in a `{Module}Constants` class in StructsAndEnums.cs:
```csharp
// In StructsAndEnums.cs
public static class RealmConstants
{
    // TODO: RLMRealmRefreshRequiredNotification (string) — [Field] not supported for this type

    [Field("RLMBackupRealmConfigurationErrorKey", "__Internal")]
    public static NSString RLMBackupRealmConfigurationErrorKey { get; }
}
```

Our tool marks unsupported field types with `// TODO` comments. For typed `NSString` constants (like `RLMNotification` typedefs), our tool doesn't yet emit `[Field]` — those get a TODO. Sharpie handles these by binding to `NSString` regardless of the ObjC typedef.

| Library | Our [Field] | Sharpie [Field] | Our TODOs |
|---------|------------|----------------|-----------|
| BlinkID V6 | 8 | 8 | 0 |
| BRLMPrinterKit | 10 | 10 | 0 |
| Realm | 23 | 29 | 6 |
| Stripe3DS2 | 10 | 10 | 0 |
| FirebaseMessaging | 2 | 4 | 2 |

### 11. NSString Mapping in Closures

An interesting type-mapping difference in callback parameters:

```csharp
// === Our output ===
[Export("tokenWithCompletion:")]
void TokenWithCompletion(Action<string, NSError> completion);

// === Sharpie ===
[Export("tokenWithCompletion:")]
void TokenWithCompletion(Action<NSString, NSError> completion);
```

Our tool maps `NSString *` to `string` in closure parameters (more C#-idiomatic). Sharpie keeps `NSString`. Both work at runtime, but `string` is friendlier for C# consumers.

### 12. DisableDefaultCtor Detection

Our tool is more aggressive about marking classes as `[DisableDefaultCtor]`:

| Library | Ours | Sharpie |
|---------|------|---------|
| BlinkID V6 | 77 | 55 |
| BRLMPrinterKit | 43 | 44 |
| Realm | 20 | 14 |
| Stripe3DS2 | 6 | 5 |
| FirebaseMessaging | 1 | 1 |

More `[DisableDefaultCtor]` means better API safety — users get compile-time errors instead of runtime crashes when trying to use unavailable constructors. Our tool detects `NS_UNAVAILABLE` and `__attribute__((unavailable))` annotations.

### 13. Optional vs Required Protocol Members (Bug)

Our tool marks **all** protocol members as `[Abstract]`, regardless of whether they are `@required` or `@optional` in ObjC. Sharpie correctly distinguishes them:

```csharp
// === Our output (Stripe3DS2 — ISTDSChallengeStatusReceiver) ===
[Abstract]  // WRONG — this is @optional in ObjC
[Export("transactionDidPresentChallengeScreen:")]
void TransactionDidPresentChallengeScreen(STDSTransaction transaction);

// === Sharpie (correct) ===
[Export("transactionDidPresentChallengeScreen:")]
void TransactionDidPresentChallengeScreen(STDSTransaction transaction);
// No [Abstract] — correctly reflects @optional
```

This forces C# consumers to implement every protocol method, even optional ones. In the Xamarin.iOS convention, `[Abstract]` means `@required` and its absence means `@optional`.

### 14. Typed Arrays and Generics

Sharpie preserves ObjC lightweight generics as typed C# arrays and generic dictionaries:

```csharp
// === Sharpie (BRLMPrinterKit) ===
void PrintURLs(NSURL[] urls, BRLMPrintSettingsProtocol settings);
BRLMLog[] AllLogs { get; }

// === Our output ===
void PrintURLs(NSArray urls, IBRLMPrintSettingsProtocol settings);
// Comment: Element type: NSUrl
NSArray AllLogs { get; }
```

Our tool loses the element type, emitting raw `NSArray` with only a code comment as a hint. This means consumers lose compile-time type safety and must cast array elements manually.

### 15. Platform Type Stub Conflicts

Our tool emits stub interfaces for platform types referenced in headers (e.g., `UNNotificationContent`, `UNMutableNotificationContent` in FirebaseMessaging). These stubs **conflict** with the real platform types from the `UserNotifications` namespace at compile time:

```csharp
// === Our output (problematic) ===
[BaseType(typeof(NSObject))]
partial interface UNNotificationContent : INSCopying, INSMutableCopying, INSSecureCoding
{
    // empty stub
}
```

Sharpie correctly assumes these types come from the platform SDK and doesn't re-declare them.

### 16. [DesignatedInitializer] Attribute

Both tools detect `NS_DESIGNATED_INITIALIZER` annotations and emit `[DesignatedInitializer]`:

```csharp
// === Both tools (Stripe3DS2) ===
[DesignatedInitializer]
[Export("initWithWarningID:message:severity:")]
NativeHandle Constructor(string warningID, string message, STDSWarningSeverity severity);
```

Our tool detects `ObjCDesignatedInitializerAttr` nodes in the clang AST and emits the attribute before `[Export]` on constructor methods only. This helps the binding generator enforce correct subclassing patterns — subclasses must call through to designated initializers.

### 17. Contradictory DisableDefaultCtor + Explicit Init

Our tool sometimes emits both `[DisableDefaultCtor]` and an explicit `[Export("init")] NativeHandle Constructor()` on the same class (seen in `FIRMessaging`). These are contradictory — `[DisableDefaultCtor]` suppresses the default constructor, but then we re-emit it explicitly. Sharpie only emits `[DisableDefaultCtor]`.

### 18. Platform Availability Attributes

Sharpie emits platform availability attributes from ObjC `__attribute__((availability(...)))` annotations:

```csharp
// === Sharpie (BlinkID) ===
[iOS(13, 0)]
[BaseType(typeof(NSObject))]
interface MBMicroblinkApp { ... }
```

Our tool does not emit any `[iOS(...)]`, `[Introduced(...)]`, or `[Unavailable(...)]` attributes. In BlinkID, Sharpie emits availability on 111 types. This metadata helps the binding generator produce correct platform guards and deprecation warnings.

### 19. Variadic Method Handling

Realm has 15 variadic ObjC methods (signatures ending in `, ...`) like `objectsWhere:` and `indexOfObjectWhere:`. Sharpie correctly marks these with `[Internal]` and `IsVariadic = true`, making them non-public and requiring a developer-written safe wrapper:

```csharp
// === Sharpie (correct) ===
[Internal]
[Export("indexOfObjectWhere:", IsVariadic = true)]
nuint IndexOfObjectWhere(string predicateFormat, IntPtr varArgs);

// Also emits the va_list variant:
[Export("indexOfObjectWhere:args:")]
unsafe nuint IndexOfObjectWhere(string predicateFormat, sbyte* args);
```

Our tool emits variadic methods as **public, non-internal** single-argument methods, silently dropping the variadic `...` parameter:

```csharp
// === Our output (incorrect) ===
[Export("indexOfObjectWhere:")]
nuint IndexOfObjectWhere(string predicateFormat);

// Also emits the args: variant but with IntPtr instead of va_list:
[Export("indexOfObjectWhere:args:")]
nuint IndexOfObjectWhere(string predicateFormat, IntPtr args);
```

This is a semantic bug: calling our `IndexOfObjectWhere("age > %d")` would invoke the variadic ObjC method with no format arguments, causing a runtime crash or garbage data. The `[Internal]` + `IsVariadic` pattern is the correct Xamarin convention — it forces consumers to use the `NSPredicate`-based overload instead.

**Variadic method counts:**

| Library | Sharpie `IsVariadic` | Our tool `[Internal]` |
|---------|---------------------|----------------------|
| BlinkID V6 | 0 | 0 |
| BRLMPrinterKit | 0 | 0 |
| Realm | 15 | 0 |
| Stripe3DS2 | 0 | 0 |
| FirebaseMessaging | 0 | 0 |

### 20. Pointer/Out-Parameter Handling (ObjC Methods)

The pointer/out-param issue extends beyond C functions to ObjC methods. In BRLMPrinterKit, `getPTLabelSize:` takes a `_Bool *` out-parameter:

```csharp
// === Sharpie (correct) ===
// -(BRLMPTPrintSettingsLabelSize)getPTLabelSize:(_Bool * _Nonnull)succeeded;
[Export("getPTLabelSize:")]
unsafe BRLMPTPrintSettingsLabelSize GetPTLabelSize(bool* succeeded);

// === Our output (incorrect) ===
[Export("getPTLabelSize:")]
BRLMPTPrintSettingsLabelSize GetPTLabelSize(bool succeeded);
```

Our tool passes `succeeded` by value instead of as a pointer. The method writes to this pointer to indicate success/failure — passing by value means the caller never gets the result. This pattern should be `out bool` or `ref bool` in C#.

### 21. P/Invoke Pointer Parameters

For C functions with pointer output parameters, Sharpie correctly uses `unsafe` pointer types:

```csharp
// === Sharpie ===
[DllImport("__Internal")]
static extern unsafe void CGRectClosestTwoCornerPoints(CGRect rect, CGPoint point, CGPoint* closest1, CGPoint* closest2);

// === Our output ===
[DllImport("__Internal")]
public static extern void CGRectClosestTwoCornerPoints(CGRect rect, CGPoint point, CGPoint closest1, CGPoint closest2);
```

Our tool passes `closest1` and `closest2` by value, which is incorrect — these are output parameters that the C function writes to. They should be `out CGPoint` or `CGPoint*`.

---

## What Our Tool Does Better

1. **Documentation** — Rich `<summary>` and `<param>` XML doc comments vs none
2. **Zero manual work** — No `[Verify]` attributes; Sharpie requires resolving 119+ across these 5 libraries
3. **Build-ready output** — Emits complete `.csproj`, `.targets`, metadata; Sharpie gives only raw .cs files
4. **Enum naming** — Strips ObjC type prefix for idiomatic C# members
5. **Enum backing types** — Always preserves `: int`, `: long`, etc.
6. **Constructor safety** — More aggressive `[DisableDefaultCtor]` detection, duplicate constructor dedup
7. **String mapping** — Maps `NSString *` to `string` in closures (more C#-native)
8. **Export coverage** — Binds 27-87% more selectors per library
9. **Delegate extraction** — Clean `BgenDelegates.cs` for block-based callbacks
10. **Dependency analysis** — Emits `dependency-manifest.json` for multi-framework builds
11. **[Model] delegate protocols** — Correct `[Protocol, Model]` with WeakDelegate/Wrap pattern
12. **ArgumentSemantic** — Preserves `Copy`/`Assign`/`Weak`/`Strong` memory semantics
13. **[Bind] custom getters** — Handles `isXxx` / custom getter selectors
14. **Typed arrays/generics** — Preserves `NSArray<T>` as `T[]` and generic args
15. **@optional/@required** — Correct `[Abstract]` only on `@required` protocol members
16. **[DesignatedInitializer]** — Emitted from `NS_DESIGNATED_INITIALIZER`
17. **Platform availability** — Emits `[iOS(x,y)]` from ObjC annotations
18. **P/Invoke pointer params** — `out T` for value-type output parameters
19. **Variadic methods** — `[Internal]` + `IsVariadic = true` (prevents runtime crashes)
20. **Foreign-type categories** — Extension methods on platform types via `[Category]`
21. **Delegate method naming** — "After first colon" convention for delegate protocols

## What Sharpie Does Better (remaining after Sessions 1+2)

1. **ObjC header line comments** — Preserves raw `// @property` declarations for reference
2. **Field placement** — Associates `[Field]` constants with related interfaces, not a separate class
3. **Category protocol conformance** — Emits protocol declarations on categories (our tool strips them due to MAUI bgen limitation: static extension classes can't implement interfaces)
4. **Category instance properties** — Preserves instance properties on categories (our tool skips them: static extension classes can't have instance members)

## Interesting Observations

### Our tool generates more code
Across all 5 libraries, our ApiDefinition.cs files average **53% more lines** than Sharpie's. This is mostly from doc comments, explicit setter exports, and surfacing more protocol members.

### Protocol naming divergence
Our tool uses `I` prefix (`IFIRMessagingDelegate`) matching C# interface conventions. Sharpie uses the bare name (`FIRMessagingDelegate`). Both are valid in the binding ecosystem, but our approach is more C#-native while Sharpie's matches the established Xamarin convention.

### Method naming for delegate methods
Both tools use "after the first colon" naming for delegate protocol methods. For multi-part selectors, our tool concatenates all parts after the first (e.g., `URLSession:task:didCompleteWithError:` → `TaskDidCompleteWithError`), producing unambiguous names. This only applies to protocols with `IsDelegateProtocol = true`; non-delegate protocols retain first-part naming.

### Compilation vs correctness
Our tool compiles 4/5 out of the box (the 5th has 4 errors from duplicate constructors). Sharpie's output is **designed** not to compile until reviewed — the `[Verify]` system is intentional. This means our tool prioritizes "works immediately" while Sharpie prioritizes "works correctly after human review."

### Constants organization
Our `{Module}Constants` class approach is more discoverable (one place for all constants) but less semantically grouped. Sharpie's approach of placing `[Field]` near related types helps developers find constants where they expect them.

---

## Recommendations for Our Generator

### Critical (correctness bugs)
1. ~~**Fix missing explicit enum values**~~ — ✅ Done (Session 1). Enums with non-sequential values now preserve explicit assignments.
2. ~~**Handle variadic methods**~~ — ✅ Done (Session 1). ObjC `...` methods marked `[Internal]` with `IsVariadic = true`.
3. ~~**Fix pointer/out-parameter flattening**~~ — ✅ Done (Session 1). `_Bool *`, `CGPoint *` etc. emitted as `out`/`ref` parameters.
4. ~~**Fix @optional vs @required distinction**~~ — ✅ Done (Session 1). `[Abstract]` only on `@required` protocol members.
5. ~~**Don't emit platform type stubs**~~ — ✅ Done (Session 1). Platform SDK types filtered out.
6. ~~**Fix contradictory DisableDefaultCtor + explicit init**~~ — ✅ Done (Session 1). Explicit `init` suppressed when `[DisableDefaultCtor]` is emitted.

### High Priority (semantic correctness)
5. ~~**Add `[Model]` to delegate protocols**~~ — ✅ Done (Session 1). `[Protocol, Model]` emitted on delegate/data source protocols.
6. ~~**Emit WeakDelegate/Wrap pattern**~~ — ✅ Done (Session 1). Prevents retain cycles on delegate properties.
7. ~~**Add ArgumentSemantic**~~ — ✅ Done (Session 1). `Copy`, `Assign`, `Weak`, `Strong` preserved from ObjC property attributes.
8. ~~**Preserve typed arrays/generics**~~ — ✅ Done (Session 1). `NSArray<T>` emitted as `T[]` when element type is known.

### Medium Priority (API completeness)
9. ~~**Emit [Bind] for custom getter selectors**~~ — ✅ Done (Session 1)
10. ~~**Support ObjC categories**~~ — ✅ Done (Session 2). Foreign-type categories emitted with `[Category]` for methods. Protocol conformance and instance properties stripped (MAUI bgen limitation: static extension classes can't implement interfaces or have instance members).
11. ~~**Resolve remaining [Field] TODOs**~~ — ✅ Done (Session 1)
12. ~~**Add [DesignatedInitializer]**~~ — ✅ Done (Session 2). Detects `ObjCDesignatedInitializerAttr` from clang AST.
13. ~~**Emit platform availability**~~ — ✅ Done (Session 1)
14. ~~**Fix P/Invoke pointer parameters**~~ — ✅ Done (Session 1)

### Low Priority (polish)
15. ~~**Fix duplicate constructor bug**~~ — ✅ Done (Session 1)
16. **Consider preserving raw ObjC declarations** — As comments for debugging reference.
17. ~~**Delegate method naming**~~ — ✅ Done (Session 2). Delegate protocol methods use "after first colon" naming convention.

---

## Implementation Plan

All 17 recommendations are addressed in 2 sessions using parallel agent teams in isolated worktrees. Each agent's scope is defined by the files it touches, minimizing merge conflicts.

### Code Map (key files)

```
src/Swift.Bindings/src/ObjC/
├── Parser/
│   ├── ClangAstParser.cs          — Parses Clang JSON into ObjCModule (1477 lines)
│   ├── ClangAstInvoker.cs         — Invokes xcrun clang for AST dump
│   └── ObjCTypeRefParser.cs       — Parses qualType strings into ObjCTypeRef
├── Model/
│   ├── ObjCModule.cs              — Root IR container
│   ├── ObjCDeclarations.cs        — Class/Protocol/Method/Property/Enum/Struct declarations
│   ├── ObjCTypeRef.cs             — Type representation
│   └── ObjCAvailability.cs        — Availability metadata
├── Emitter/
│   ├── ApiDefinitionEmitter.cs    — Emits ApiDefinition.cs (472 lines)
│   ├── StructsAndEnumsEmitter.cs  — Emits StructsAndEnums.cs (500+ lines)
│   ├── ObjCTypeMapper.cs          — Type resolution and mapping (498 lines)
│   ├── ObjCAvailabilityEmitter.cs — Availability attribute emission
│   └── ObjCBindingProjectEmitter.cs — .csproj generation
└── Pipeline/
    └── ObjCPipeline.cs            — Orchestrator: umbrella header → clang → parse → emit
```

### Session 1: Core Fixes (4 parallel agents) ✅ DONE

All critical, high-priority, and most medium-priority fixes. Implemented via 4 parallel agents in isolated worktrees, merged sequentially, then refined through multiple Codex review passes.

**Results:** 86/88 validation (up from 83/88 baseline). 6459 unit tests passing. 3 libraries improved (FirebaseCore, FirebaseFirestoreInternal, GoogleSignIn). 0 regressions.

#### Agent A — Enum & Constants
**Touches:** `StructsAndEnumsEmitter.cs` (enum + constant emission), `ClangAstParser.cs` (enum value extraction)
**Fixes:** Recommendations #1, #11

- **Enum explicit values (#1):** The parser already has `TryExtractEnumValue()` at `ClangAstParser.cs:429-439` and stores values in `ObjCEnumCaseDecl.Value`. The emitter at `StructsAndEnumsEmitter.cs:238` emits `case.Value` — investigate why non-sequential values are lost. Likely the clang AST extraction is failing for certain expression types (e.g., hex literals, arithmetic expressions, or enum values defined via macros). Fix extraction to handle all constant expression forms.
- **[Field] TODOs (#11):** At `StructsAndEnumsEmitter.cs:431-444`, typed constants like `RLMNotification` (typedef for `NSString`) emit TODO stubs. Map typedef'd string constants to `NSString` [Field] properties (Sharpie's approach).

**Tests:** Unit tests for non-sequential enum value extraction (gaps, hex, macros). Integration test with BRLMPrinterKit enums starting at 20000.

#### Agent B — Method Signatures
**Touches:** `ClangAstParser.cs` (method parsing), `ObjCDeclarations.cs` (model), `ApiDefinitionEmitter.cs` (method emission), `ObjCTypeRefParser.cs` (pointer detection)
**Fixes:** Recommendations #2, #3, #14

- **Variadic methods (#2):** Add `IsVariadic` bool to `ObjCMethodDecl` in `ObjCDeclarations.cs`. Detect `...` in `ClangAstParser.cs` method parameter parsing (clang AST marks these with `isVariadic: true` on the FunctionProtoType). In `ApiDefinitionEmitter.cs:275`, emit `[Internal]` and `[Export("selector:", IsVariadic = true)]` with `IntPtr varArgs` parameter.
- **Pointer/out-params (#3, #14):** In `ObjCTypeRefParser.cs`, when a parameter type is `T *` where T is a value type (bool, int, CGPoint, etc.), mark as `IsOutParam` on `ObjCTypeRef`. In `ApiDefinitionEmitter.cs` method emission and `StructsAndEnumsEmitter.cs` function emission, emit as `out T` for single-pointer value types. Don't change `NSObject *` (that's normal ObjC object passing).

**Tests:** Unit test for variadic detection from clang JSON. Unit test for `_Bool *` → `out bool` mapping. Integration test with Realm's `objectsWhere:` and BRLMPrinterKit's `getPTLabelSize:`.

#### Agent C — Protocol & Delegate Patterns
**Touches:** `ApiDefinitionEmitter.cs` (protocol + property emission), `ObjCDeclarations.cs` (model)
**Fixes:** Recommendations #4, #5, #6, #7 (high-priority), #8 (high-priority)

- **@optional vs @required (#4):** Parser already sets `IsOptional` at `ClangAstParser.cs:849`. Emitter checks it at `ApiDefinitionEmitter.cs:269-270`. Verify the logic is correct — `[Abstract]` should only emit when `!method.IsOptional` in a protocol context. The `BuildOptionalLineSet()` at lines 724-744 does source-level `@optional` section detection — confirm this is working for all 5 test libraries.
- **Platform type stubs (#5):** In `ApiDefinitionEmitter.cs` class emission or `ObjCPipeline.cs`, filter out classes whose names match known platform SDK types (UNNotificationContent, UIViewController, etc.). Use the existing `AppleSdkTypeNames` set on `ObjCModule` or the type mapper's known types.
- **DisableDefaultCtor + init contradiction (#6):** In `ApiDefinitionEmitter.cs:121-127` where `[DisableDefaultCtor]` is decided, also suppress the explicit `[Export("init")] Constructor()` method when DisableDefaultCtor is emitted.
- **[Model] on delegate protocols (#7):** Heuristic: emit `[Model]` on protocols that are (a) named `*Delegate` or `*DataSource`, OR (b) used as the type of a property named `delegate` or `dataSource` on any class. Add this detection in `ObjCPipeline.cs` as a post-parse pass, store `IsDelegateProtocol` on `ObjCProtocolDecl`. In `ApiDefinitionEmitter.cs:82`, emit `[Protocol, Model]` instead of `[Protocol]` when flagged.
- **WeakDelegate/Wrap pattern (#8):** In `ApiDefinitionEmitter.cs` property emission, when a property's type is a delegate protocol (has `IsDelegateProtocol`), emit the two-property pattern: `[NullAllowed, Export("delegate", ArgumentSemantic.Weak)] NSObject WeakDelegate { get; set; }` + `[Wrap("WeakDelegate")] [NullAllowed] {ProtocolType} Delegate { get; set; }`.

**Tests:** Unit tests for optional/required classification. Unit test for [Model] heuristic. Integration tests verifying FIRMessagingDelegate and MBScanningRecognizerRunnerDelegate emit correctly.

#### Agent D — Property Attributes & Type Mapping
**Touches:** `ClangAstParser.cs` (property attribute parsing), `ApiDefinitionEmitter.cs` (property emission), `ObjCTypeMapper.cs` (type mapping)
**Fixes:** Recommendations #9, #10 (typed arrays part), #13 (availability)

- **ArgumentSemantic (#9 from high-priority):** In `ClangAstParser.cs:858-934` property parsing, extract the ObjC memory attribute from the clang AST node (`copy`, `assign`, `weak`, `strong`, `retain`). Add `MemorySemantic` field to `ObjCPropertyDecl` in `ObjCDeclarations.cs`. In `ApiDefinitionEmitter.cs:360` property emission, append `ArgumentSemantic.{Copy|Assign|Weak|Strong}` to the `[Export]` attribute.
- **[Bind] for custom getters (#9 from medium-priority):** Parser already extracts custom getter selectors at `ClangAstParser.cs:878-882` into `GetterSelector`. In `ApiDefinitionEmitter.cs:370-382`, when the getter selector differs from the property name (e.g., `isAutoInitEnabled` vs `autoInitEnabled`), emit `[Bind("isAutoInitEnabled")]` on the getter accessor instead of using the getter as the main `[Export]` selector.
- **Typed arrays (#10 typed arrays part):** In `ObjCTypeMapper.cs`, when mapping `NSArray<T>` where T is a known bound type, emit `T[]` instead of `NSArray`. Similarly for `NSDictionary<K,V>` → preserve generic args. The generic arg info is already parsed by `ObjCTypeRefParser.cs` into `ObjCTypeRef.GenericArgs`.
- **Platform availability (#13):** `ObjCAvailabilityEmitter.cs` already exists with `EmitAvailabilityAttributes()`. Check why it's not being called from `ApiDefinitionEmitter.cs`. Wire it up for class, protocol, method, and property emission.

**Tests:** Unit tests for ArgumentSemantic extraction. Unit test for [Bind] getter emission. Integration test verifying typed arrays in BRLMPrinterKit and availability attributes in BlinkID.

#### Merge Order & Validation

1. Merge Agent A (enum/constants — least conflict surface)
2. Merge Agent B (method signatures — touches method emission)
3. Merge Agent D (property attributes — touches property emission)
4. Merge Agent C (protocol patterns — most cross-cutting)
5. Run `./run-tests.sh 2>&1 | tee /tmp/session1-tests.txt`
6. Run `./validate-libraries.sh 2>&1 | tee /tmp/session1-validation.txt`
7. Re-run 5-library comparison to measure improvement

### Session 2: Categories + Polish + Final Validation ✅ DONE

All remaining polish fixes from the implementation plan. Foreign-type category support, `[DesignatedInitializer]`, and delegate method naming.

**Results:** 86/88 validation (same as Session 1 baseline). 6478 unit tests passing (+19 net new). 0 regressions. SDWebImage/SDWebImageMapKit regression discovered and fixed during implementation.

#### Agent A — ObjC Category Support
**Fixes:** Recommendation #10 (category part)

- **Foreign-type category routing:** `ObjCPipeline.FilterToForeignCategories()` preserves categories whose base class is NOT in the module (e.g., `NSNull`, `NSNumber`, `UIButton` categories). Own-type categories remain merged into parent classes.
- **Category emission constraints:** MAUI bgen compiles `[Category]` interfaces as static extension classes, imposing three constraints: (a) protocol conformance stripped (CS0714: static classes can't implement interfaces), (b) instance properties skipped (CS0708: static classes can't have instance members), (c) instance methods → extension methods (works correctly). Categories with no emittable methods are skipped entirely with diagnostics.
- **MapKit using import:** Added `using MapKit;` to ApiDefinition.cs for frameworks referencing `MKAnnotationView` etc.

**Tests:** 4 pipeline tests for foreign/own-type category filtering. 3 emitter tests for category constraints (protocol-only skip, protocol stripping, property filtering). 2 existing tests updated to match new bgen-safe emission.

#### Agent B — Polish Fixes
**Fixes:** Recommendations #12, #15 (verified), #17

- **[DesignatedInitializer] (#12):** `ObjCDesignatedInitializerAttr` detected in `ParseMethodDecl`. `IsDesignatedInitializer` added to `ObjCMethodDecl`. Emitted before `[Export]` on constructor methods only (non-constructor methods ignore the flag).
- **Duplicate constructor dedup (#15):** Already implemented in Session 1 (`emittedConstructorSignatures` tracking at `ApiDefinitionEmitter.cs:322-327`). Verified passing via existing test.
- **Delegate method naming (#17):** `SelectorToDelegateMethodName()` concatenates all selector parts after the first for multi-part selectors in delegate protocols (e.g., `messaging:didReceiveRegistrationToken:` → `DidReceiveRegistrationToken`, `URLSession:task:didCompleteWithError:` → `TaskDidCompleteWithError`). Only applies to protocols with `IsDelegateProtocol = true`; non-delegate protocols retain first-part naming.

**Tests:** 2 parser tests for DesignatedInitializer detection. 3 emitter tests (attribute emission, non-constructor ignored). 4 tests for delegate method naming (Theory + delegate vs non-delegate protocol). Full regression check: all 6478 unit tests + 700 integration tests pass.
