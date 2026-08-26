const std = @import("std");

pub fn build(b: *std.Build) !void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});
    const backend = b.option(Backend, "backend", "Backend") orelse .inproc;
    const transport = b.option(Transport, "transport", "Transport") orelse .none;

    const module = b.addModule("sentry", .{
        .root_source_file = b.path("main.zig"),
        .target = target,
        .optimize = optimize,
    });

    const lib = b.addLibrary(.{
        .name = "sentry",
        .root_module = b.createModule(.{
            .target = target,
            .optimize = optimize,
            .link_libc = true,
        }),
        .linkage = .static,
    });
    if (target.result.os.tag.isDarwin()) {
        const apple_sdk = @import("apple_sdk");
        try apple_sdk.addPaths(b, lib);
    }

    var flags: std.ArrayList([]const u8) = .empty;
    defer flags.deinit(b.allocator);
    // The library is built with .linkage = .static, so sentry.h must not
    // decorate its API with __declspec(dllimport); without this every
    // sentry_* symbol comes out as an unresolved dllimport on Windows.
    try flags.append(b.allocator, "-DSENTRY_BUILD_STATIC=1");
    if (target.result.os.tag == .windows) {
        try flags.appendSlice(b.allocator, &.{
            "-DSENTRY_WITH_UNWINDER_DBGHELP",
        });
    } else {
        try flags.appendSlice(b.allocator, &.{
            "-DSENTRY_WITH_UNWINDER_LIBBACKTRACE",
        });
    }
    switch (backend) {
        .crashpad => try flags.append(b.allocator, "-DSENTRY_BACKEND_CRASHPAD"),
        .breakpad => try flags.append(b.allocator, "-DSENTRY_BACKEND_BREAKPAD"),
        .inproc => try flags.append(b.allocator, "-DSENTRY_BACKEND_INPROC"),
        .none => {},
    }

    if (b.lazyDependency("sentry", .{})) |upstream| {
        module.addIncludePath(upstream.path("include"));
        lib.root_module.addIncludePath(upstream.path("include"));
        lib.root_module.addIncludePath(upstream.path("src"));
        lib.root_module.addCSourceFiles(.{
            .root = upstream.path(""),
            .files = srcs,
            .flags = flags.items,
        });

        // Linux-only
        if (target.result.os.tag == .linux) {
            lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "vendor/stb_sprintf.c",
                },
                .flags = flags.items,
            });
        }

        // Symbolizer + Unwinder
        if (target.result.os.tag == .windows) {
            // dbghelp:  symbolizer, dbghelp unwinder, modulefinder.
            // version:  sentry_os.c version lookup.
            // advapi32: sentry_os.c reads the OS build from the registry with
            //           RegGetValueA. The app pulls advapi32 in by other
            //           routes, so this only shows up as an undefined symbol
            //           in targets that link sentry and little else, which is
            //           what the test target is.
            lib.root_module.linkSystemLibrary("dbghelp", .{});
            lib.root_module.linkSystemLibrary("version", .{});
            lib.root_module.linkSystemLibrary("advapi32", .{});
            module.linkSystemLibrary("dbghelp", .{});
            module.linkSystemLibrary("version", .{});
            module.linkSystemLibrary("advapi32", .{});
            lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/sentry_windows_dbghelp.c",
                    "src/path/sentry_path_windows.c",
                    "src/symbolizer/sentry_symbolizer_windows.c",
                    "src/unwinder/sentry_unwinder_dbghelp.c",
                },
                .flags = flags.items,
            });
        } else {
            lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/sentry_unix_pageallocator.c",
                    "src/path/sentry_path_unix.c",
                    "src/symbolizer/sentry_symbolizer_unix.c",
                    "src/unwinder/sentry_unwinder_libbacktrace.c",
                },
                .flags = flags.items,
            });
        }

        // Module finder
        switch (target.result.os.tag) {
            .windows => lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/modulefinder/sentry_modulefinder_windows.c",
                },
                .flags = flags.items,
            }),

            .macos, .ios => lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/modulefinder/sentry_modulefinder_apple.c",
                },
                .flags = flags.items,
            }),

            .linux => lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/modulefinder/sentry_modulefinder_linux.c",
                },
                .flags = flags.items,
            }),

            .freestanding => {},

            else => {
                std.log.warn("target={} not supported", .{target.result.os.tag});
                return error.UnsupportedTarget;
            },
        }

        // Transport
        switch (transport) {
            .curl => lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/transports/sentry_transport_curl.c",
                },
                .flags = flags.items,
            }),

            .winhttp => lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/transports/sentry_transport_winhttp.c",
                },
                .flags = flags.items,
            }),

            .none => lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/transports/sentry_transport_none.c",
                },
                .flags = flags.items,
            }),
        }

        // Backend
        switch (backend) {
            .crashpad => lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/backends/sentry_backend_crashpad.cpp",
                },
                .flags = flags.items,
            }),

            .breakpad => {
                lib.root_module.addCSourceFiles(.{
                    .root = upstream.path(""),
                    .files = &.{
                        "src/backends/sentry_backend_breakpad.cpp",
                    },
                    .flags = flags.items,
                });

                if (b.lazyDependency("breakpad", .{
                    .target = target,
                    .optimize = optimize,
                })) |breakpad_dep| {
                    lib.root_module.linkLibrary(breakpad_dep.artifact("breakpad"));

                    // We need to add this because Sentry includes some breakpad
                    // headers that include this vendored file...
                    lib.root_module.addIncludePath(breakpad_dep.path("vendor"));
                }
            },

            .inproc => lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/backends/sentry_backend_inproc.c",
                },
                .flags = flags.items,
            }),

            .none => lib.root_module.addCSourceFiles(.{
                .root = upstream.path(""),
                .files = &.{
                    "src/backends/sentry_backend_none.c",
                },
                .flags = flags.items,
            }),
        }

        lib.installHeadersDirectory(
            upstream.path("include"),
            "",
            .{ .include_extensions = &.{".h"} },
        );
    }

    b.installArtifact(lib);
}

const srcs: []const []const u8 = &.{
    "src/sentry_alloc.c",
    "src/sentry_backend.c",
    "src/sentry_core.c",
    "src/sentry_database.c",
    "src/sentry_envelope.c",
    "src/sentry_info.c",
    "src/sentry_json.c",
    "src/sentry_logger.c",
    "src/sentry_options.c",
    "src/sentry_os.c",
    "src/sentry_random.c",
    "src/sentry_ratelimiter.c",
    "src/sentry_scope.c",
    "src/sentry_session.c",
    "src/sentry_slice.c",
    "src/sentry_string.c",
    "src/sentry_sync.c",
    "src/sentry_transport.c",
    "src/sentry_utils.c",
    "src/sentry_uuid.c",
    "src/sentry_value.c",
    "src/sentry_tracing.c",
    "src/path/sentry_path.c",
    "src/transports/sentry_disk_transport.c",
    "src/transports/sentry_function_transport.c",
    "src/unwinder/sentry_unwinder.c",
    "vendor/mpack.c",
};

pub const Backend = enum { crashpad, breakpad, inproc, none };
pub const Transport = enum { curl, winhttp, none };
