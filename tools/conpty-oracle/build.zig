const std = @import("std");

pub fn build(b: *std.Build) void {
    // The oracle is Windows-only (ConPTY). We always build it with
    // `-Dtarget=x86_64-windows-gnu`; the default host target will fail
    // to link the kernel32 externs on non-Windows hosts.
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const exe_mod = b.createModule(.{
        .root_source_file = b.path("src/main.zig"),
        .target = target,
        .optimize = optimize,
    });

    // Forward target/optimize so the ghostty-vt module is built for our
    // (Windows) target, and set emit-lib-vt so the parent build only
    // configures the vt library and skips the full app (which requires
    // a native SDK lookup that fails when cross-compiling).
    if (b.lazyDependency("ghostty", .{
        .target = target,
        .optimize = optimize,
        .@"emit-lib-vt" = true,
    })) |dep| {
        exe_mod.addImport(
            "ghostty-vt",
            dep.module("ghostty-vt"),
        );
    }

    const exe = b.addExecutable(.{
        .name = "conpty-oracle",
        .root_module = exe_mod,
    });
    b.installArtifact(exe);
}
