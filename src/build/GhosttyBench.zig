//! GhosttyBench generates all the Ghostty benchmark helper binaries.
const GhosttyBench = @This();

const std = @import("std");
const SharedDeps = @import("SharedDeps.zig");

steps: []*std.Build.Step.Compile,

pub fn init(
    b: *std.Build,
    deps: *const SharedDeps,
) !GhosttyBench {
    var steps: std.ArrayList(*std.Build.Step.Compile) = .empty;
    errdefer steps.deinit(b.allocator);

    // Both binaries here pin their optimize mode instead of following
    // -Doptimize, so this is also the mode their dependencies are built in:
    // -Dvt-safe may raise the Zig half below, but simdutf, highway and
    // src/simd/*.cpp are the VT hot path, and recompiling them in a second
    // mode would put a C++ codegen change inside the number the option exists
    // to report.
    const pinned: std.builtin.OptimizeMode = .ReleaseFast;

    // Our synthetic data generator
    {
        const exe = b.addExecutable(.{
            .name = "ghostty-gen",
            .root_module = b.createModule(.{
                .root_source_file = b.path("src/main_gen.zig"),
                .target = deps.config.target,
                // We always want our datagen to be fast because it
                // takes awhile to run.
                .optimize = pinned,
                .link_libc = true,
            }),
        });
        _ = try deps.add(exe, pinned);
        try steps.append(b.allocator, exe);
    }

    // Our benchmarking application.
    {
        const exe = b.addExecutable(.{
            .name = "ghostty-bench",
            .root_module = b.createModule(.{
                .root_source_file = b.path("src/main_bench.zig"),
                .target = deps.config.target,
                // We always want our benchmarks to be in release mode,
                // regardless of -Doptimize, so that timings are
                // comparable. -Dvt-safe is the exception: measuring what
                // runtime safety costs the VT hot path is the reason that
                // option exists.
                .optimize = if (deps.config.vt_safe)
                    .ReleaseSafe
                else
                    pinned,
                .link_libc = true,
            }),
        });
        _ = try deps.add(exe, pinned);
        try steps.append(b.allocator, exe);
    }

    return .{ .steps = steps.items };
}

pub fn install(self: *const GhosttyBench) void {
    const b = self.steps[0].step.owner;
    for (self.steps) |step| b.installArtifact(step);
}
