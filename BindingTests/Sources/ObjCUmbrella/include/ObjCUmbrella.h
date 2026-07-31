// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Umbrella header for the pure-ObjC clang-module BindingTests fixture (W-1). This is the
// first authored `module.modulemap` fixture in the harness — the durable regression gate
// for the generator behaviors a pure-ObjC clang-umbrella library (e.g. MapLibre) depends on.
// Each interface/protocol below is one deliberately-shaped generator stress case; see the
// per-shape comments. Asserted by behavior from RuntimeTestsApp/ObjCInterop.

#import <Foundation/Foundation.h>
// UIKit is imported for Shape 7's UIApplicationState (a UIKit NS_ENUM). It also brings the
// wider UIKit surface into the clang module, which the parser's platform-stub filter drops.
#import <UIKit/UIKit.h>

NS_ASSUME_NONNULL_BEGIN

// MARK: - Shape 1 — a selector exposed as BOTH a property and a method.
//
// `tally` is a readonly property (getter selector `tally`) AND a redeclared method with the
// identical selector. bgen sees two members that flatten onto ONE ObjC selector; the emitter
// must collapse them to a single registration, or the .NET ObjC registrar aborts at launch
// ("cannot register selector 'tally' twice"). Round-trips a known value.
@interface OUCounter : NSObject
@property (nonatomic, readonly) NSInteger tally;
- (NSInteger)tally;
@end

// MARK: - Shape 2 — a `static inline` C function alongside a real exported C function.
//
// `OUInlineSquare` has internal linkage (no exported symbol): binding it would emit a dead
// P/Invoke that fails to link. `OUExportedTriple` is a genuine exported symbol and must bind
// via `DllImport("__Internal")`. The generator must exclude the inline one and keep the export.
static inline int32_t OUInlineSquare(int32_t x) { return x * x; }
extern int32_t OUExportedTriple(int32_t x);

// MARK: - Shape 3 — an ObjC protocol used as a collection element (`NSArray<id<Foo>>`).
//
// `makeElements` returns `NSArray<id<OUElement>>`. The element projects to the C# interface
// `IOUElement` and must round-trip with no double-`I` collapse (`IIOUElement`) and no
// InvalidCastException marshalling the array elements back as the protocol type.
@protocol OUElement <NSObject>
- (NSString *)describeElement;
@end

@interface OUElementBox : NSObject <OUElement>
- (instancetype)initWithName:(NSString *)name;
@end

@interface OUContainer : NSObject
- (NSArray<id<OUElement>> *)makeElements;
@end

// MARK: - Shape 4 — a delegate protocol with an OPTIONAL callback.
//
// `OUNotifier` calls back into its weak `listener` only when the optional selector is
// implemented. A C# subclass overriding `DidReceiveValue:` and installing itself as the
// listener must have its override invoked when `emit:` fires — ObjC→C# reverse dispatch.
@protocol OUListener <NSObject>
@optional
- (void)didReceiveValue:(NSInteger)value;
@end

@interface OUNotifier : NSObject
@property (nonatomic, weak, nullable) id<OUListener> listener;
- (void)emit:(NSInteger)value;
@end

// MARK: - Shape 5 (ML-1 regression, test-only) — a bare property name that collides with the
// FIRST selector segment of an unrelated multi-keyword method.
//
// `camera` (a property → C# `Camera`) sits alongside `camera:fittingX:edgePadding:` (a method
// whose first selector segment is also `camera`). The property must keep `Camera`; the method
// must disambiguate to a distinct C# name. This is a DIFFERENT collision than Shape 1
// (same-selector-as-both) and guards commit 3e5a0a5e (a silent regression once already).
@interface OUCamera : NSObject
@property (nonatomic, readonly) NSInteger altitude;
- (instancetype)initWithAltitude:(NSInteger)altitude;
@end

@interface OUMapView : NSObject
@property (nonatomic, strong) OUCamera *camera;
- (OUCamera *)camera:(NSInteger)bounds fittingX:(NSInteger)x edgePadding:(NSInteger)padding;
@end

// MARK: - Shape 6 — a block whose RETURN type is a protocol (`id<OUElement>`).
//
// `OUElementFactory` is a block that RETURNS `id<OUElement>` — the exact shape of Google
// AdMob's mediation `...LoadCompletionHandler` (which returns `id<...AdEventDelegate>`).
// bgen marshals a block's return value through `Runtime.RetainAndAutoreleaseNSObject(NSObject?)`,
// so the generator MUST widen a protocol-typed block RETURN to `NSObject`; emitting the interface
// `IOUElement` in that slot fails to compile (CS1503) in the generated Trampolines.g.cs. Block
// PARAMETER protocol types are unaffected (Shape 3's `NSArray<id<OUElement>>` covers reading a
// protocol back in). `runFactory:` invokes the block and round-trips the produced element's text.
typedef id<OUElement> _Nonnull (^OUElementFactory)(NSInteger index);

@interface OUFactoryHost : NSObject
- (NSString *)runFactory:(OUElementFactory)factory;
@end

// MARK: - Shape 7 (A2) — members whose signatures reference standard Apple value types that were
// absent from the ObjC type registry.
//
// `NSDataReadingOptions`, `NSURLSessionTaskState`, `UIApplicationState` (enums) and
// `NSOperatingSystemVersion` (a Foundation struct) were all unresolvable, so any ObjC member using
// them was silently dropped as `ObjCSkipReason.UnresolvableType` — genuinely-public API vanishing
// with no persisted diagnostic. Registering them (objc-type-mappings.json) closes the gap; these
// members are the durable proof it stays closed: each must bind AND the emitted C# must resolve the
// type against Microsoft.iOS (Foundation/UIKit). Note the `URL`→`Url` acronym on the projected
// `NSUrlSessionTaskState`. Round-trips a known value per type.
@interface OUSystemTypes : NSObject
- (NSOperatingSystemVersion)minimumVersion;
- (BOOL)acceptsReadingOptions:(NSDataReadingOptions)options;
- (NSURLSessionTaskState)currentTaskState;
- (UIApplicationState)preferredApplicationState;
// NSJSONReadingOptions / NSJSONWritingOptions are the exact FBSDKTypeUtility JSON-utility surface
// (JSONObjectWithData:options: / dataWithJSONObject:options:); they exercise the same objcValueTypes
// registry path, projected with the JSON→Json acronym.
- (NSJSONReadingOptions)defaultReadingOptions;
- (NSJSONWritingOptions)defaultWritingOptions;
@end

// MARK: - Shape 8 — declarations the emitter RENAMES, and the native identity that must survive.
//
// Two independent rename paths change the C# DECLARATION name while the Objective-C runtime name
// must stay put behind `[BaseType(..., Name=)]` / `[Protocol(Name=)]`. This is the one class of
// defect in our half of the ObjC pipeline that a compiler cannot see: drop or misspell the `Name=`
// and the binding still COMPILES, the type merely registers under its managed spelling,
// `objc_getClass` returns nil, and the first message send fails. Only a running test catches it,
// which is why these shapes are here and not emitter unit tests. Every OTHER rename defect
// (a reference left on the raw spelling, a dangling `instancetype`, a duplicated member) is a
// hard compile error and is gated by the unit suite plus the compile gate.

// 8a — the class/protocol clash suffix: `OUBadge` is BOTH a class and a protocol, the shape
// `NSObject` itself has. The class keeps the bare managed name (it carries the superclass); the
// protocol's managed interface is renamed `IOUBadgeProtocol`. This rename is NOT gated on the NS
// prefix, so it fires under the fixture's own prefix.
@protocol OUBadge <NSObject>
- (NSString *)badgeLabel;
@end

@interface OUBadge : NSObject <OUBadge>
- (instancetype)initWithLabel:(NSString *)label;
// Asks the question from the ObjC side, which is the only side that can answer it: does a MANAGED
// adopter of the renamed `IOUBadgeProtocol` register as conforming to the NATIVE protocol `OUBadge`?
// That holds only while `[Protocol(Name = "OUBadge")]` survives the rename — drop it and the
// registrar files the conformance under the managed spelling and this returns NO. Asking the
// C# instance instead would prove nothing: the fixture's own class declares the conformance in
// Objective-C, so it answers YES no matter what the binding says.
+ (BOOL)acceptsBadge:(id<OUBadge>)candidate;
@end

// 8b — the .NET acronym convention (`NSURL*` → `NSUrl*`), which fires ONLY on NS-prefixed names,
// so this fixture deliberately borrows the prefix; no Foundation type of this name exists. Both
// `instancetype` returners resolve to the class's DECLARATION name — emitting the raw ObjC
// spelling in that slot dangles (CS0246) — and the renamed class must still answer to native
// `NSURLBadgeBox`, which the test asserts against `objc_getClass` directly.
@interface NSURLBadgeBox : NSObject
@property (nonatomic, copy) NSString *tag;
+ (instancetype)defaultBox;
- (instancetype)reboxWithTag:(NSString *)tag;
@end

// 8c — a renamed DELEGATE protocol behind a weak `delegate` property: the WeakDelegate pattern.
// bgen generates a `[Model]` CLASS from the protocol declaration, and the `[Wrap]` half of the
// pattern is typed by that class (bare name, NOT `I`-prefixed), so the wrap must use the renamed
// spelling or it dangles. A managed subclass installed as the delegate must have its override
// invoked — proving the renamed protocol kept its native registration in both directions.
@protocol NSURLBadgeObserver <NSObject>
@optional
- (void)badgeDidChange:(NSString *)tag;
@end

@interface NSURLBadgeEmitter : NSObject
@property (nonatomic, weak, nullable) id<NSURLBadgeObserver> delegate;
- (void)changeBadge:(NSString *)tag;
// The same managed→native conformance question for the renamed `[Model]` class: the callback firing
// alone would NOT prove `[Protocol(Name = "NSURLBadgeObserver")]` survived, because the member's own
// `[Export("badgeDidChange:")]` is what makes `respondsToSelector:` true, independent of the
// protocol's registered name. This asks the protocol directly.
- (BOOL)delegateConformsToObserverProtocol;
@end

// MARK: - Shape 9a — a system struct whose PUBLIC name is a typedef over a differently-named
// record tag.
//
// Foundation spells the range struct `typedef struct _NSRange NSRange;`. The header says `NSRange`,
// clang's desugared spelling is the private tag `_NSRange`, and that typedef reaches the generator
// through the system-header typedef set — so a naive typedef hop rewrites every member below to
// `_NSRange`, a name no platform assembly declares. The member then fails the api-definition
// resolvability gate and is silently DROPPED, which is exactly how a real map framework loses its
// whole range-editing surface (coordinate get/replace/remove by range, plus range-carrying delegate
// callbacks). Both parameter and return position are covered, and two same-typed parameters guard
// the multi-parameter case. Round-trips known values so the struct's field layout is asserted, not
// just its name.
@interface OURangeSugarTypes : NSObject
- (NSRange)rangeWithLocation:(NSUInteger)location length:(NSUInteger)length;
- (NSUInteger)endOfRange:(NSRange)range;
- (NSUInteger)combinedLengthOfRange:(NSRange)first andRange:(NSRange)second;
@end

// MARK: - Shape 9b — members typed by Apple SDK enums the platform assembly already declares.
//
// An Apple SDK enum is invisible to the parser's SDK type-name provenance (which collects classes
// and protocols), so before the system-enum vocabulary existed a member typed by one had nothing to
// resolve against and was dropped exactly like Shape 9a's. `NSComparisonResult` (Foundation) and
// `UIUserInterfaceStyle` (UIKit) are deliberately from two different frameworks, so the emitted
// `using` for each owning namespace is exercised, not just one. Neither type is reachable through
// the older value-type registry Shape 7 guards — these bind purely through the enum vocabulary.
@interface OUSystemEnumTypes : NSObject
- (NSComparisonResult)compareLength:(NSUInteger)length toLength:(NSUInteger)other;
- (UIUserInterfaceStyle)preferredInterfaceStyle;
- (BOOL)acceptsInterfaceStyle:(UIUserInterfaceStyle)style;
@end

// MARK: - Shape 10 — a category on a system class mixing class methods with INSTANCE properties.
//
// The boxing/unboxing category shape: the boxing half is class methods, the unboxing half is
// instance properties. bgen compiles a `[Category]` into a static extension class, which cannot
// hold an instance PROPERTY (CS0708) but does hold an instance METHOD (it becomes an extension
// method carrying the receiver) — so the instance properties are projected onto accessor methods
// (`GetOu_spanValue()` / `SetOu_spanRank(…)`) rather than dropped. Filtering them out instead
// silently deletes half a library's surface: consumers could box and never unbox. This shape is
// here because the ONLY proof the projection is legal is bgen itself accepting it, which the
// compile gate runs; the emitter unit tests cover the projection's shape and its skip records.
@interface NSValue (OUBoxing)
+ (NSValue *)ou_valueWithSpan:(double)span;
// Readonly — projects to a getter method alone.
@property (nonatomic, readonly) double ou_spanValue;
// Read-write — projects to the getter/setter method pair, on the property's real selectors.
@property (nonatomic, assign) NSInteger ou_spanRank;
@end

// MARK: - Shape 11 — a subclass re-declaring an inherited property.
//
// ObjC headers routinely re-declare an inherited property to restate a protocol conformance, and
// bgen generates a class binding as a real C# class deriving from its `[BaseType]` — so re-emitting
// the member HIDES the inherited one: CS0108 in every consumer build, and, when the re-declaration
// narrows a read-write base to readonly, the setter becomes unreachable through a subclass-typed
// variable. `title` is that narrowing case (the emitter must defer to the wider inherited member),
// `rank` is the widening case (the subclass member is genuinely wider, so it emits and must carry
// the `new` keyword). Whether bgen accepts `[New]` on a property is only answerable by running it,
// which is what the compile gate does here.
@protocol OUTitled <NSObject>
@property (nonatomic, copy, readonly) NSString *title;
@end

@interface OUShape : NSObject
@property (nonatomic, copy) NSString *title;
@property (nonatomic, readonly) NSInteger rank;
@end

@interface OUPolyShape : OUShape <OUTitled>
// Narrowed to readonly to satisfy OUTitled — must NOT narrow the binding's surface. clang itself
// flags this line (-Wproperty-attribute-mismatch); the warning IS the shape under test, and it is
// exactly what real headers ship.
@property (nonatomic, copy, readonly) NSString *title;
// Widened to read-write — a genuine addition, so it emits over the inherited member.
@property (nonatomic, assign) NSInteger rank;
@end

// MARK: - Shape 12 — a C array of value types passed as an element pointer + `count:` pair.
//
// A pointer to a value type is structurally identical whether it addresses ONE value or the FIRST
// ELEMENT of an array; only the selector's `count:` keyword tells them apart. Getting that wrong is
// silent: projected as a C# `out`, the pointer parameter zeroes the caller's storage before the
// call, so an input array arrives as zeros and an output array is written past its one-element
// slot. Nothing about that fails to compile, which is why the shape is fixtured here rather than
// left to the type mapper's unit coverage. (Found in the wild on a map-rendering framework's
// polyline/polygon constructors, which take `const CLLocationCoordinate2D *` + `count:`.)
@interface OUPointBuffer : NSObject

@property (nonatomic, readonly) NSInteger storedCount;

// 12a — const element pointer + count on a class factory: read-only input, the canonical array shape.
+ (instancetype)bufferWithPoints:(const CGPoint *)points count:(NSUInteger)count;

// 12b — the same pair on an instance method, followed by a value-type parameter that has to pass
// straight through the generated array overload untouched.
- (void)appendPoints:(const CGPoint *)points count:(NSUInteger)count scaledBy:(CGFloat)scale;

// 12c — a MUTABLE element pointer + count: a caller-allocated output buffer the callee fills. The
// pair is still an array — an `out` here would hand the callee room for one element to write
// `count` of.
- (void)copyPointsInto:(CGPoint *)points count:(NSUInteger)count;

// 12d — a const element pointer with NO count sibling. Read-only, so it cannot be an `out`, and
// nothing identifies it as an array either: the member has no sound projection and must drop out of
// the binding with a recorded skip rather than ship as a callable that corrupts its argument.
- (CGFloat)distanceFromOrigin:(const CGPoint *)point;

// 12e — the positive control for the projection that stays: a single MUTABLE element pointer with no
// count really is one caller-allocated slot, and remains an `out` parameter.
- (BOOL)tryFirstPoint:(CGPoint *)outPoint;

// Readback for the round-trip assertions.
- (CGFloat)sumOfX;

@end

NS_ASSUME_NONNULL_END
