// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#define UNW_LOCAL_ONLY
#include <dlfcn.h>
#include <libunwind.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef void *(*create_fn)(int32_t);
typedef int32_t (*counter_fn)(void);
typedef void (*release_fn)(void *);
typedef void (*hook_fn)(void);
typedef void (*set_hook_fn)(hook_fn);

extern uint64_t bt_native_thunk_sentinel(void *, void *, int32_t, int32_t *, void *);
extern uint64_t bt_native_thunk_sentinel_sp;
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

// ---- unwind walk ----
//
// The hook runs inside the Swift member the thunk calls. It walks the stack with libunwind, the
// unwinder crash reporters, debuggers and runtimes use, reading each frame's rule from the unwind
// information the linker wrote. A thunk whose frame that information does not describe stops the
// walk at the thunk or resumes the caller with the wrong stack or registers.

static const char *walk_thunk;
static int walk_saw_thunk, walk_saw_sentinel, walk_saw_main, walk_sp_ok, walk_regs_ok;

static void unwind_hook(void)
{
    unw_context_t context;
    unw_cursor_t cursor;
    unw_getcontext(&context);
    unw_init_local(&cursor, &context);
    walk_saw_thunk = walk_saw_sentinel = walk_saw_main = walk_sp_ok = walk_regs_ok = 0;
    for (int frame = 0; frame < 32; frame++) {
        unw_word_t pc = 0, sp = 0;
        unw_get_reg(&cursor, UNW_REG_IP, &pc);
        unw_get_reg(&cursor, UNW_REG_SP, &sp);
        Dl_info info;
        const char *name = dladdr((void *)(uintptr_t)pc, &info) && info.dli_sname ? info.dli_sname : "?";
        printf("    #%d pc=0x%llx sp=0x%llx %s\n", frame, (unsigned long long)pc, (unsigned long long)sp, name);
        if (walk_thunk != NULL && strcmp(name, walk_thunk) == 0)
            walk_saw_thunk = 1;
        if (strcmp(name, "bt_native_thunk_sentinel") == 0 && !walk_saw_sentinel) {
            walk_saw_sentinel = 1;
            walk_sp_ok = sp == bt_native_thunk_sentinel_sp;
#if defined(__arm64__)
            unw_word_t x19 = 0, x20 = 0, x21 = 0, x22 = 0, fp = 0;
            unw_get_reg(&cursor, UNW_ARM64_X19, &x19);
            unw_get_reg(&cursor, UNW_ARM64_X20, &x20);
            unw_get_reg(&cursor, UNW_ARM64_X21, &x21);
            unw_get_reg(&cursor, UNW_ARM64_X22, &x22);
            unw_get_reg(&cursor, UNW_ARM64_FP, &fp);
            printf("      sentinel frame: x19=%llu x20=%llu x21=%llu x22=%llu fp-sp=%lld\n",
                (unsigned long long)x19, (unsigned long long)x20, (unsigned long long)x21,
                (unsigned long long)x22, (long long)(fp - sp));
            walk_regs_ok = x19 == 275 && x20 == 276 && x21 == 277 && x22 == 278 && fp == sp + 160;
#elif defined(__x86_64__)
            unw_word_t rbx = 0, r12 = 0, r13 = 0, rbp = 0;
            unw_get_reg(&cursor, UNW_X86_64_RBX, &rbx);
            unw_get_reg(&cursor, UNW_X86_64_R12, &r12);
            unw_get_reg(&cursor, UNW_X86_64_R13, &r13);
            unw_get_reg(&cursor, UNW_X86_64_RBP, &rbp);
            printf("      sentinel frame: rbx=%llu r12=%llu r13=%llu rbp-sp=%lld\n",
                (unsigned long long)rbx, (unsigned long long)r12, (unsigned long long)r13,
                (long long)(rbp - sp));
            walk_regs_ok = rbx == 768 && r12 == 770 && r13 == 771 && rbp == sp + 48;
#else
#error Unsupported probe architecture
#endif
        }
        if (strcmp(name, "main") == 0)
            walk_saw_main = 1;
        int step = unw_step(&cursor);
        if (step <= 0) {
            printf("    unw_step -> %d after frame #%d (%s)\n", step, frame, name);
            break;
        }
    }
}

static void check_walk(const char *label)
{
    char name[160];
    snprintf(name, sizeof name, "%s: the unwind passes through the thunk", label);
    check(name, walk_saw_thunk, walk_saw_thunk, 1);
    snprintf(name, sizeof name, "%s: the unwind crosses the thunk into its caller", label);
    check(name, walk_saw_sentinel, walk_saw_sentinel, 1);
    snprintf(name, sizeof name, "%s: the caller's unwound sp is the sp at its call", label);
    check(name, walk_sp_ok, walk_sp_ok, 1);
    snprintf(name, sizeof name, "%s: the caller's unwound callee-saved registers are its own", label);
    check(name, walk_regs_ok, walk_regs_ok, 1);
    snprintf(name, sizeof name, "%s: the unwind reaches main", label);
    check(name, walk_saw_main, walk_saw_main, 1);
}

int main(int argc, char **argv)
{
    if (argc != 6) {
        fprintf(stderr, "usage: %s <fixture> <wrapper> <add-thunk> <add-throwing-thunk> <good|bad-control>\n", argv[0]);
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

    void *add = required_symbol(wrapper, argv[3]);
    void *add_throwing = required_symbol(wrapper, argv[4]);
    create_fn create = (create_fn)required_symbol(fixture, "bt_native_thunk_create");
    counter_fn calls = (counter_fn)required_symbol(fixture, "bt_native_thunk_calls");
    counter_fn deinits = (counter_fn)required_symbol(fixture, "bt_native_thunk_deinits");
    set_hook_fn set_hook = (set_hook_fn)required_symbol(fixture, "bt_native_thunk_set_hook");
    release_fn swift_release = (release_fn)required_symbol(RTLD_DEFAULT, "swift_release");
    release_fn swift_error_release = (release_fn)required_symbol(RTLD_DEFAULT, "swift_errorRelease");
    if (add == NULL || add_throwing == NULL || create == NULL || calls == NULL || deinits == NULL
        || set_hook == NULL || swift_release == NULL || swift_error_release == NULL)
        return 67;

    if (strcmp(argv[5], "bad-control") == 0) {
        int32_t result = 0;
        uint64_t mask = bt_native_thunk_sentinel(
            (void *)(uintptr_t)&bt_native_thunk_bad, NULL, 5, &result, NULL);
        printf("bad-control mask=0x%llx result=%d\n", (unsigned long long)mask, result);
        // Deliberately expect the bad callee to be clean. A working sentinel must make this
        // process red; the Nuke target treats that non-zero exit as the expected control result.
        check("bad callee preserves nonvolatile state", mask == 0, (long long)mask, 0);
        return failures == 0 ? 0 : 1;
    }

    if (strcmp(argv[5], "good") != 0) {
        fprintf(stderr, "unknown mode: %s\n", argv[5]);
        return 68;
    }

    check("initial call count", calls() == 0, calls(), 0);
    check("initial deinit count", deinits() == 0, deinits(), 0);

    void *base = create(0);
    void *child = create(1);
    check("base factory", base != NULL, base != NULL, 1);
    check("child factory", child != NULL, child != NULL, 1);

    // Non-throwing thunk: base and override dispatch.
    int32_t result = 0;
    uint64_t mask = bt_native_thunk_sentinel(add, base, 5, &result, NULL);
    check("add: base result", result == 22, result, 22);
    check("add: base callee-saved mask", mask == 0, (long long)mask, 0);
    result = 0;
    mask = bt_native_thunk_sentinel(add, child, 5, &result, NULL);
    check("add: child override result", result == 122, result, 122);
    check("add: child callee-saved mask", mask == 0, (long long)mask, 0);

    // Throwing thunk: success on both receivers, then the error path.
    void *error = (void *)(uintptr_t)0xdead;
    result = 0;
    mask = bt_native_thunk_sentinel(add_throwing, base, 5, &result, &error);
    check("addThrowing: base result", result == 22, result, 22);
    check("addThrowing: base callee-saved mask", mask == 0, (long long)mask, 0);
    check("addThrowing: base stores no error", error == NULL, (long long)(uintptr_t)error, 0);
    error = (void *)(uintptr_t)0xdead;
    result = 0;
    mask = bt_native_thunk_sentinel(add_throwing, child, 5, &result, &error);
    check("addThrowing: child override result", result == 122, result, 122);
    check("addThrowing: child callee-saved mask", mask == 0, (long long)mask, 0);
    check("addThrowing: child stores no error", error == NULL, (long long)(uintptr_t)error, 0);
    error = NULL;
    mask = bt_native_thunk_sentinel(add_throwing, base, -1, &result, &error);
    check("addThrowing: throwing callee-saved mask", mask == 0, (long long)mask, 0);
    check("addThrowing: the thrown error is stored", error != NULL, error != NULL, 1);
    if (error != NULL)
        swift_error_release(error);

    // Unwind from inside each thunk's target, through the thunk, to main.
    set_hook(unwind_hook);
    walk_thunk = argv[3];
    result = 0;
    mask = bt_native_thunk_sentinel(add, base, 5, &result, NULL);
    check("add walk: result", result == 22, result, 22);
    check("add walk: callee-saved mask", mask == 0, (long long)mask, 0);
    check_walk("add");
    walk_thunk = argv[4];
    error = (void *)(uintptr_t)0xdead;
    result = 0;
    mask = bt_native_thunk_sentinel(add_throwing, base, 5, &result, &error);
    check("addThrowing walk: result", result == 22, result, 22);
    check("addThrowing walk: callee-saved mask", mask == 0, (long long)mask, 0);
    check("addThrowing walk: stores no error", error == NULL, (long long)(uintptr_t)error, 0);
    check_walk("addThrowing");
    error = NULL;
    mask = bt_native_thunk_sentinel(add_throwing, base, -1, &result, &error);
    check("addThrowing throwing walk: callee-saved mask", mask == 0, (long long)mask, 0);
    check("addThrowing throwing walk: the thrown error is stored", error != NULL, error != NULL, 1);
    check_walk("addThrowing (throws)");
    if (error != NULL)
        swift_error_release(error);
    set_hook(NULL);

    check("exact call count", calls() == 8, calls(), 8);
    check("live deinit count", deinits() == 0, deinits(), 0);

    swift_release(child);
    swift_release(base);
    check("exact deinit count", deinits() == 2, deinits(), 2);

    printf(failures == 0 ? "ALL PASS\n" : "%d FAILURE(S)\n", failures);
    return failures == 0 ? 0 : 1;
}
