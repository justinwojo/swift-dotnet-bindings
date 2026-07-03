// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Umbrella header for the pure-ObjC clang-module BindingTests fixture (W-1). This is the
// first authored `module.modulemap` fixture in the harness — the durable regression gate
// for the generator behaviors a pure-ObjC clang-umbrella library (e.g. MapLibre) depends on.
// Each interface/protocol below is one deliberately-shaped generator stress case; see the
// per-shape comments. Asserted by behavior from RuntimeTestsApp/ObjCInterop.

#import <Foundation/Foundation.h>

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

NS_ASSUME_NONNULL_END
