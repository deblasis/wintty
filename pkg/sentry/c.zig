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
        // Two pointers, matching the real EXCEPTION_POINTERS
        // (PEXCEPTION_RECORD, PCONTEXT). The stand-in used to be a single
        // int, which made Zig's view of sentry_ucontext_s four bytes where
        // the C side's is sixteen. Nothing on the Zig side references that
        // field today, which is the only reason it worked; getting the size
        // right costs nothing and removes a landmine for whoever does.
        @cDefine("EXCEPTION_POINTERS", "struct { void *_record; void *_context; }");
    }
    // Matches -DSENTRY_BUILD_STATIC in build.zig: without it sentry.h
    // decorates every function with __declspec(dllimport).
    @cDefine("SENTRY_BUILD_STATIC", "1");
    @cInclude("sentry.h");
});
