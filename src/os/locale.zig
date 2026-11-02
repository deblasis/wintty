const std = @import("std");
const builtin = @import("builtin");
const assert = @import("../quirks.zig").inlineAssert;
const macos = @import("macos");
const objc = @import("objc");
const internal_os = @import("main.zig");
const i18n = internal_os.i18n;

const log = std.log.scoped(.os_locale);

/// Ensure that the locale is set.
pub fn ensureLocale() !void {
    // This does a lot of in-process mutation of the environment and can't be
    // run in a test as a result.
    assert(!builtin.is_test);
    return ensureLocaleInner();
}

/// The body of `ensureLocale`, split out so the Windows numeric-locale
/// contract can be tested. On Windows this only mutates the CRT locale,
/// which a test can save and restore.
fn ensureLocaleInner() !void {
    if (builtin.os.tag == .windows) {
        // libghostty is a library inside a WinUI host. In the shipped
        // NativeAOT build it is linked statically, so its CRT calls resolve
        // to the process UCRT that WinUI also uses; taking the locale from
        // the system therefore changes WinUI's numeric formatting too. That
        // gives the user's language and character settings, which the shell
        // integrations want, but it also gives their decimal comma.
        //
        // WinUI formats its own XAML through that CRT. TabView's item
        // template builds a Path geometry with StringCchPrintf("%f") and
        // parses the result, so a comma makes the first window throw
        // XamlParseException during the first window's layout pass. Keep the
        // other categories and pin numeric formatting to the invariant
        // locale; every number libghostty writes or parses is written that
        // way.
        if (setlocale(LC_ALL, "")) |v| {
            log.info("setlocale from system result={s}", .{v});
        } else {
            log.warn("setlocale from system defaults failed", .{});
        }
        if (setlocale(LC_NUMERIC, "C") == null) return error.NumericLocaleFailed;
        return;
    }

    // On macOS, pre-populate the LANG env var with system preferences.
    // When launching the .app, LANG is not set so we must query it from the
    // OS. When launching from the CLI, LANG is usually set by the parent
    // process.
    if (comptime builtin.target.os.tag.isDarwin()) {
        // Set the lang if it is not set or if its empty.
        const lang = std.posix.system.getenv("LANG");
        if (lang == null or lang.?[0] == 0) {
            setLangFromCocoa();
        }
    }

    // Set the locale to whatever is set in env vars.
    if (setlocale(LC_ALL, "")) |v| {
        log.info("setlocale from env result={s}", .{v});
        return;
    }

    // setlocale failed. This is probably because the LANG env var is
    // invalid. Try to set it without the LANG var set to use the system
    // default.
    if (std.posix.system.getenv("LANG")) |lang| {
        if (lang[0] != 0) {
            // We don't need to do both of these things but we do them
            // both to be sure that lang is either empty or unset completely.
            _ = setenv("LANG", "", 1);
            _ = unsetenv("LANG");

            if (setlocale(LC_ALL, "")) |v| {
                log.info("setlocale after unset lang result={s}", .{v});

                // If we try to setlocale to an unsupported locale it'll return "C"
                // as the POSIX/C fallback, if that's the case we want to not use
                // it and move to our fallback of en_US.UTF-8
                if (!std.mem.eql(u8, std.mem.sliceTo(v, 0), "C")) return;
            }
        }
    }

    // Failure again... fallback to en_US.UTF-8
    log.warn("setlocale failed with LANG and system default. Falling back to en_US.UTF-8", .{});
    if (setlocale(LC_ALL, "en_US.UTF-8")) |v| {
        _ = setenv("LANG", "en_US.UTF-8", 1);
        log.info("setlocale default result={s}", .{v});
        return;
    } else log.warn("setlocale failed even with the fallback, uncertain results", .{});
}

/// This sets the LANG environment variable based on the macOS system
/// preferences selected locale settings.
fn setLangFromCocoa() void {
    const pool = objc.AutoreleasePool.init();
    defer pool.deinit();

    // The classes we're going to need.
    const NSLocale = objc.getClass("NSLocale") orelse {
        log.warn("NSLocale class not found. Locale may be incorrect.", .{});
        return;
    };

    // Get our current locale and extract the language code ("en") and
    // country code ("US")
    const locale = NSLocale.msgSend(objc.Object, objc.sel("currentLocale"), .{});
    const lang = locale.getProperty(objc.Object, "languageCode");
    const country = locale.getProperty(objc.Object, "countryCode");

    if (lang.value == null or country.value == null) {
        log.warn("languageCode or countryCode not found. Locale may be incorrect.", .{});
        return;
    }

    // Get our UTF8 string values
    const c_lang = lang.getProperty([*:0]const u8, "UTF8String");
    const c_country = country.getProperty([*:0]const u8, "UTF8String");

    // Convert them to Zig slices
    const z_lang = std.mem.sliceTo(c_lang, 0);
    const z_country = std.mem.sliceTo(c_country, 0);

    // Format our locale as "<lang>_<country>.UTF-8" and set it as LANG.
    {
        var buf: [128]u8 = undefined;
        const env_value = std.fmt.bufPrintZ(&buf, "{s}_{s}.UTF-8", .{ z_lang, z_country }) catch |err| {
            log.warn("error setting locale from system. err={}", .{err});
            return;
        };
        log.info("detected system locale={s}", .{env_value});

        // Set it onto our environment
        if (setenv("LANG", @ptrCast(env_value), 1) < 0) {
            log.warn("error setting locale env var", .{});
            return;
        }
    }

    // Get our preferred languages and set that to the LANGUAGE
    // env var in case our language differs from our locale.
    language: {
        var buf: [1024]u8 = undefined;
        const pref_ = preferredLanguageFromCocoa(
            &buf,
            NSLocale,
        ) catch |err| {
            log.warn("error getting preferred languages. err={}", .{err});
            break :language;
        };

        const pref = pref_ orelse break :language;
        log.debug(
            "setting LANGUAGE from preferred languages value={s}",
            .{pref},
        );
        _ = setenv("LANGUAGE", @ptrCast(pref), 1);
    }
}

/// Sets the LANGUAGE environment variable based on the preferred languages
/// as reported by NSLocale.
///
/// macOS has a concept of preferred languages separate from the system
/// locale. The set of preferred languages is a list in priority order
/// of what translations the user prefers. A user can have, for example,
/// "fr_FR" as their locale but "en" as their preferred language. This would
/// mean that they want to use French units, date formats, etc. but they
/// prefer English translations.
///
/// gettext uses the LANGUAGE environment variable to override only
/// translations and a priority order can be specified by separating
/// the languages with colons. For example, "en:fr" would mean that
/// English translations are preferred but if they are not available
/// then French translations should be used.
///
/// To further complicate things, Apple reports the languages in BCP-47
/// format which is not compatible with gettext's POSIX locale format so
/// we have to canonicalize them.
fn preferredLanguageFromCocoa(
    buf: []u8,
    NSLocale: objc.Class,
) error{NoSpaceLeft}!?[:0]const u8 {
    var writer: std.Io.Writer = .fixed(buf);

    // We need to get our app's preferred languages. These may not
    // match the system locale (NSLocale.currentLocale).
    const preferred: *macos.foundation.Array = array: {
        const ns = NSLocale.msgSend(
            objc.Object,
            objc.sel("preferredLanguages"),
            .{},
        );
        break :array @ptrCast(ns.value);
    };
    for (0..preferred.getCount()) |i| {
        var str_buf: [255:0]u8 = undefined;
        const str = preferred.getValueAtIndex(macos.foundation.String, i);
        const c_str = str.cstring(&str_buf, .utf8) orelse {
            // I don't think this can happen but if it does then I want
            // to know about it if a user has translation issues.
            log.warn("failed to convert a preferred language to UTF-8", .{});
            continue;
        };

        // Append our separator if we have any previous languages
        if (writer.end > 0) {
            _ = writer.writeByte(':') catch
                return error.NoSpaceLeft;
        }

        // Apple languages are in BCP-47 format, and we need to
        // canonicalize them to the POSIX format.
        const canon = try i18n.canonicalizeLocale(
            writer.buffer[writer.end..],
            c_str,
        );
        writer.end += canon.len;

        // The canonicalized locale never contains the encoding and
        // all of our translations require UTF-8 so we add that.
        _ = writer.writeAll(".UTF-8") catch return error.NoSpaceLeft;
    }

    // If we had no preferred languages then we return nothing.
    if (writer.end == 0) return null;

    // Null terminate it
    _ = writer.writeByte(0) catch return error.NoSpaceLeft;

    // Get our slice, this won't be null terminated so we have to
    // reslice it with the null terminator.
    const slice = writer.buffered();
    return slice[0 .. slice.len - 1 :0];
}

/// Copy a `setlocale` result before the next call invalidates it. Returns
/// null when the value does not fit the fixed buffer.
fn copyLocaleString(ptr: [*:0]const u8, buf: *[128:0]u8) ?[:0]const u8 {
    const value = std.mem.sliceTo(ptr, 0);
    if (value.len >= buf.len) return null;
    @memcpy(buf[0..value.len], value);
    buf[value.len] = 0;
    return buf[0..value.len :0];
}

test "windows pins LC_NUMERIC to the invariant locale" {
    if (builtin.os.tag != .windows) return error.SkipZigTest;

    // setlocale returns a pointer that the next call invalidates, so the
    // saved value has to be copied before anything else runs.
    const saved_ptr = setlocale(LC_ALL, null) orelse return error.SkipZigTest;
    var saved_buf: [128:0]u8 = undefined;
    const saved = copyLocaleString(saved_ptr, &saved_buf) orelse return error.SkipZigTest;
    defer if (setlocale(LC_ALL, saved.ptr) == null)
        @panic("failed to restore the process locale after the test");

    // A machine whose system locale is already C cannot tell the defect from
    // the fix, so skip instead of passing vacuously.
    if (setlocale(LC_ALL, "")) |_| {
        const numeric = setlocale(LC_NUMERIC, null) orelse return error.SkipZigTest;
        if (std.mem.eql(u8, std.mem.sliceTo(numeric, 0), "C")) return error.SkipZigTest;
    } else return error.SkipZigTest;

    // The system values for the categories the fix must preserve.
    var ctype_buf: [128:0]u8 = undefined;
    var time_buf: [128:0]u8 = undefined;
    const system_ctype = copyLocaleString(
        setlocale(LC_CTYPE, null) orelse return error.TestUnexpectedResult,
        &ctype_buf,
    ) orelse return error.TestUnexpectedResult;
    const system_time = copyLocaleString(
        setlocale(LC_TIME, null) orelse return error.TestUnexpectedResult,
        &time_buf,
    ) orelse return error.TestUnexpectedResult;

    // Start from C so a dropped system load is visible: the categories have
    // to come back as the system values, not stay C. A refactor that pins
    // with `setlocale(LC_ALL, "C")` fails the same way.
    if (setlocale(LC_ALL, "C") == null) return error.TestUnexpectedResult;

    try ensureLocaleInner();

    const numeric = setlocale(LC_NUMERIC, null) orelse return error.TestUnexpectedResult;
    try std.testing.expectEqualStrings("C", std.mem.sliceTo(numeric, 0));
    try std.testing.expectEqualStrings(
        system_ctype,
        std.mem.sliceTo(
            setlocale(LC_CTYPE, null) orelse return error.TestUnexpectedResult,
            0,
        ),
    );
    try std.testing.expectEqualStrings(
        system_time,
        std.mem.sliceTo(
            setlocale(LC_TIME, null) orelse return error.TestUnexpectedResult,
            0,
        ),
    );
}

const c = @import("locale-c");
const LC_ALL: c_int = c.LC_ALL;
const LC_NUMERIC: c_int = c.LC_NUMERIC;
const LC_CTYPE: c_int = c.LC_CTYPE;
const LC_TIME: c_int = c.LC_TIME;
const LC_ALL_MASK: c_int = c.LC_ALL_MASK;
const locale_t = c.locale_t;
const setlocale = c.setlocale;
const newlocale = c.newlocale;
const freelocale = c.freelocale;

extern "c" fn setenv(name: ?[*]const u8, value: ?[*]const u8, overwrite: c_int) c_int;
extern "c" fn unsetenv(name: ?[*]const u8) c_int;
