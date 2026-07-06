// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#import "ObjCUmbrella.h"

// Shape 2 — the genuinely-exported C function (the inline sibling is header-only).
int32_t OUExportedTriple(int32_t x) { return x * 3; }

// Shape 1 — the single `tally` implementation backing both the property and the method.
@implementation OUCounter
- (NSInteger)tally { return 7; }
@end

// Shape 3 — a concrete protocol conformer + a container returning a protocol-typed array.
@implementation OUElementBox {
    NSString *_name;
}
- (instancetype)initWithName:(NSString *)name {
    if ((self = [super init])) { _name = [name copy]; }
    return self;
}
- (NSString *)describeElement { return [NSString stringWithFormat:@"element:%@", _name]; }
@end

@implementation OUContainer
- (NSArray<id<OUElement>> *)makeElements {
    return @[[[OUElementBox alloc] initWithName:@"alpha"],
             [[OUElementBox alloc] initWithName:@"beta"]];
}
@end

// Shape 4 — fire the optional callback only when the listener implements it.
@implementation OUNotifier
- (void)emit:(NSInteger)value {
    if ([self.listener respondsToSelector:@selector(didReceiveValue:)]) {
        [self.listener didReceiveValue:value];
    }
}
@end

// Shape 5 — the ML-1 collision fixture types.
@implementation OUCamera {
    NSInteger _altitude;
}
- (instancetype)initWithAltitude:(NSInteger)altitude {
    if ((self = [super init])) { _altitude = altitude; }
    return self;
}
- (NSInteger)altitude { return _altitude; }
@end

@implementation OUMapView
- (OUCamera *)camera:(NSInteger)bounds fittingX:(NSInteger)x edgePadding:(NSInteger)padding {
    return [[OUCamera alloc] initWithAltitude:bounds + x + padding];
}
@end

// Shape 6 — invoke the protocol-returning factory block and round-trip the element's text.
@implementation OUFactoryHost
- (NSString *)runFactory:(OUElementFactory)factory {
    id<OUElement> element = factory(3);
    return [element describeElement];
}
@end

// Shape 7 — return known values of each formerly-unresolvable Apple system type.
@implementation OUSystemTypes
- (NSOperatingSystemVersion)minimumVersion {
    NSOperatingSystemVersion v;
    v.majorVersion = 15;
    v.minorVersion = 2;
    v.patchVersion = 0;
    return v;
}
- (BOOL)acceptsReadingOptions:(NSDataReadingOptions)options {
    return (options & NSDataReadingMappedIfSafe) != 0;
}
- (NSURLSessionTaskState)currentTaskState { return NSURLSessionTaskStateSuspended; }
- (UIApplicationState)preferredApplicationState { return UIApplicationStateBackground; }
- (NSJSONReadingOptions)defaultReadingOptions { return NSJSONReadingMutableContainers; }
- (NSJSONWritingOptions)defaultWritingOptions { return NSJSONWritingPrettyPrinted; }
@end
