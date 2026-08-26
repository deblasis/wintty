const builtin = @import("builtin");

pub const c = @cImport({
    // aro, which backs translate-c in zig 0.16, cannot parse the __ptr64
    // attribute the Windows SDK uses in basetsd.h ("typedef void* POINTER_64
    // HANDLE64;"), so any @cImport that reaches windows.h fails. sentry.h
    // includes windows.h for exactly one declaration, the EXCEPTION_POINTERS
    // field of sentry_ucontext_s, which nothing on the Zig side references.
    // Suppress the include and supply a stand-in for that field.
    if (builtin.target.os.tag == .windows) {
        @cDefine("_WINDOWS_", "1");
        @cDefine("EXCEPTION_POINTERS", "struct { int _unused; }");
    }
    // Matches -DSENTRY_BUILD_STATIC in build.zig: without it sentry.h
    // decorates every function with __declspec(dllimport).
    @cDefine("SENTRY_BUILD_STATIC", "1");
    @cInclude("sentry.h");
});
