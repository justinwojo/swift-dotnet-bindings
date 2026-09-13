// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef void *(*create_fn)(int32_t);
typedef int32_t (*counter_fn)(void);
typedef void (*release_fn)(void *);

extern uint64_t bt_native_thunk_sentinel(void *, void *, int32_t, int32_t *);
extern int32_t bt_native_thunk_bad(int32_t, void *);

static int failures;

static void check(const char *name, int condition, long long actual, long long expected)
{
    printf("[%s] %s: actual=%lld expected=%lld\n",
        condition ? "PASS" : "FAIL", name, actual, expected);
    if (!condition)
        failures++;
}

static void *required_symbol(void *handle, const char *name)
{
    dlerror();
    void *symbol = dlsym(handle, name);
    const char *error = dlerror();
    if (symbol == NULL || error != NULL) {
        fprintf(stderr, "dlsym(%s) failed: %s\n", name, error == NULL ? "null" : error);
        return NULL;
    }
    printf("[PASS] dlsym export: %s=%p\n", name, symbol);
    return symbol;
}

int main(int argc, char **argv)
{
    if (argc != 5) {
        fprintf(stderr, "usage: %s <fixture> <wrapper> <thunk-symbol> <good|bad-control>\n", argv[0]);
        return 64;
    }

    void *fixture = dlopen(argv[1], RTLD_NOW | RTLD_GLOBAL);
    if (fixture == NULL) {
        fprintf(stderr, "dlopen fixture failed: %s\n", dlerror());
        return 65;
    }
    void *wrapper = dlopen(argv[2], RTLD_NOW | RTLD_GLOBAL);
    if (wrapper == NULL) {
        fprintf(stderr, "dlopen wrapper failed: %s\n", dlerror());
        return 66;
    }

    void *thunk = required_symbol(wrapper, argv[3]);
    create_fn create = (create_fn)required_symbol(fixture, "bt_native_thunk_create");
    counter_fn calls = (counter_fn)required_symbol(fixture, "bt_native_thunk_calls");
    counter_fn deinits = (counter_fn)required_symbol(fixture, "bt_native_thunk_deinits");
    release_fn swift_release = (release_fn)required_symbol(RTLD_DEFAULT, "swift_release");
    if (thunk == NULL || create == NULL || calls == NULL || deinits == NULL || swift_release == NULL)
        return 67;

    if (strcmp(argv[4], "bad-control") == 0) {
        int32_t result = 0;
        uint64_t mask = bt_native_thunk_sentinel(
            (void *)(uintptr_t)&bt_native_thunk_bad, NULL, 5, &result);
        printf("bad-control mask=0x%llx result=%d\n", (unsigned long long)mask, result);
        // Deliberately expect the bad callee to be clean. A working sentinel must make this
        // process red; the Nuke target treats that non-zero exit as the expected control result.
        check("bad callee preserves nonvolatile state", mask == 0, (long long)mask, 0);
        return failures == 0 ? 0 : 1;
    }

    if (strcmp(argv[4], "good") != 0) {
        fprintf(stderr, "unknown mode: %s\n", argv[4]);
        return 68;
    }

    check("initial call count", calls() == 0, calls(), 0);
    check("initial deinit count", deinits() == 0, deinits(), 0);

    void *base = create(0);
    void *child = create(1);
    check("base factory", base != NULL, base != NULL, 1);
    check("child factory", child != NULL, child != NULL, 1);

    int32_t result = 0;
    uint64_t mask = bt_native_thunk_sentinel(thunk, base, 5, &result);
    check("base result", result == 22, result, 22);
    check("base callee-saved mask", mask == 0, (long long)mask, 0);

    result = 0;
    mask = bt_native_thunk_sentinel(thunk, child, 5, &result);
    check("child override result", result == 122, result, 122);
    check("child callee-saved mask", mask == 0, (long long)mask, 0);
    check("exact call count", calls() == 2, calls(), 2);
    check("live deinit count", deinits() == 0, deinits(), 0);

    swift_release(child);
    swift_release(base);
    check("exact deinit count", deinits() == 2, deinits(), 2);

    printf(failures == 0 ? "ALL PASS\n" : "%d FAILURE(S)\n", failures);
    return failures == 0 ? 0 : 1;
}
