// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#import "ObjCUmbrella.h"
// Shape 10's read-write category property needs associated-object storage: an ObjC category
// cannot add an ivar.
#import <objc/runtime.h>

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

// Shape 8a — the class half of the class/protocol name clash.
@implementation OUBadge {
    NSString *_label;
}
- (instancetype)initWithLabel:(NSString *)label {
    if ((self = [super init])) { _label = [label copy]; }
    return self;
}
- (NSString *)badgeLabel { return [NSString stringWithFormat:@"badge:%@", _label]; }
+ (BOOL)acceptsBadge:(id<OUBadge>)candidate {
    return [candidate conformsToProtocol:@protocol(OUBadge)];
}
@end

// Shape 8b — the acronym-renamed class; both `instancetype` returners hand back a real instance.
@implementation NSURLBadgeBox
+ (instancetype)defaultBox {
    NSURLBadgeBox *box = [[NSURLBadgeBox alloc] init];
    box.tag = @"default";
    return box;
}
- (instancetype)reboxWithTag:(NSString *)tag {
    NSURLBadgeBox *box = [[NSURLBadgeBox alloc] init];
    box.tag = [NSString stringWithFormat:@"%@+%@", self.tag, tag];
    return box;
}
@end

// Shape 8c — fire the renamed observer protocol's optional callback when it is implemented.
@implementation NSURLBadgeEmitter
- (void)changeBadge:(NSString *)tag {
    if ([self.delegate respondsToSelector:@selector(badgeDidChange:)]) {
        [self.delegate badgeDidChange:tag];
    }
}
- (BOOL)delegateConformsToObserverProtocol {
    return [self.delegate conformsToProtocol:@protocol(NSURLBadgeObserver)];
}
@end

// Shape 9a — build and read back the typedef-spelled Foundation range struct.
@implementation OURangeSugarTypes
- (NSRange)rangeWithLocation:(NSUInteger)location length:(NSUInteger)length {
    return NSMakeRange(location, length);
}
- (NSUInteger)endOfRange:(NSRange)range { return NSMaxRange(range); }
- (NSUInteger)combinedLengthOfRange:(NSRange)first andRange:(NSRange)second {
    return first.length + second.length;
}
@end

// Shape 9b — return and consume enums the platform assembly already declares.
@implementation OUSystemEnumTypes
- (NSComparisonResult)compareLength:(NSUInteger)length toLength:(NSUInteger)other {
    if (length < other) return NSOrderedAscending;
    if (length > other) return NSOrderedDescending;
    return NSOrderedSame;
}
- (UIUserInterfaceStyle)preferredInterfaceStyle { return UIUserInterfaceStyleDark; }
- (BOOL)acceptsInterfaceStyle:(UIUserInterfaceStyle)style {
    return style == UIUserInterfaceStyleDark;
}
@end

// Shape 10 — the boxing half is a class method, the unboxing half instance properties.
@implementation NSValue (OUBoxing)
+ (NSValue *)ou_valueWithSpan:(double)span {
    return [NSValue valueWithBytes:&span objCType:@encode(double)];
}
- (double)ou_spanValue {
    double span = 0;
    if (strcmp(self.objCType, @encode(double)) == 0) {
        [self getValue:&span size:sizeof(span)];
    }
    return span;
}
- (NSInteger)ou_spanRank {
    NSNumber *rank = objc_getAssociatedObject(self, @selector(ou_spanRank));
    return rank.integerValue;
}
- (void)setOu_spanRank:(NSInteger)rank {
    objc_setAssociatedObject(self, @selector(ou_spanRank), @(rank), OBJC_ASSOCIATION_RETAIN_NONATOMIC);
}
@end

// Shape 11 — the base owns the wide `title`; the subclass owns the widened `rank`.
@implementation OUShape
- (NSInteger)rank { return 1; }
@end

@implementation OUPolyShape
// `title` is the superclass's member — the subclass declaration only restates the conformance.
@dynamic title;
@synthesize rank = _rank;
@end
