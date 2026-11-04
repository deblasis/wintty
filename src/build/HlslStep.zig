/// A zig build step that compiles a set of ".hlsl" files into
/// ".dxil" (DirectX Intermediate Language) files using dxc.exe,
/// the DirectX Shader Compiler.
const HlslStep = @This();

const std = @import("std");
const builtin = @import("builtin");
const Step = std.Build.Step;
const RunStep = std.Build.Step.Run;
const LazyPath = std.Build.LazyPath;

pub const ShaderEntry = struct {
    /// The HLSL source file.
    source: LazyPath,
    /// Shader profile (e.g. "vs_6_0", "ps_6_0").
    profile: []const u8,
    /// Entry point function name (e.g. "VSMain", "PSMain").
    entry_point: []const u8,
    /// Output name (e.g. "cell_vs" -> "cell_vs.dxil").
    output_name: []const u8,
};

pub const Options = struct {
    target: std.Build.ResolvedTarget,
    shaders: []const ShaderEntry,
};

step: *Step,
/// String-keyed outputs so callers look up by name, not index.
outputs: std.StringHashMapUnmanaged(LazyPath),

pub fn create(b: *std.Build, opts: Options) ?*HlslStep {
    if (opts.target.result.os.tag != .windows) return null;

    const self = b.allocator.create(HlslStep) catch @panic("OOM");

    // Find dxc.exe from the Windows SDK or PATH. dxc runs on the build
    // host and its DXIL output is arch-independent, so resolve the host
    // arch: the target arch can pick an exe the host cannot run
    // (InvalidExe when cross-compiling for aarch64).
    const dxc_path = findDxc(b, b.graph.host.result.cpu.arch) orelse {
        std.log.warn("dxc.exe not found; HLSL shaders will not be compiled", .{});
        return null;
    };

    // 0.17 removed custom build steps (`StepOptions.id`/`.custom`): the build
    // graph is serialized to the maker process, so steps must be built-ins.
    // Aggregate the per-shader Run steps by chaining them and exposing the last
    // link as `step`; depending on it guarantees every shader was built.
    var outputs: std.StringHashMapUnmanaged(LazyPath) = .empty;
    var last_step: ?*Step = null;

    for (opts.shaders) |shader| {
        const run = RunStep.create(
            b,
            b.fmt("hlsl {s}", .{shader.output_name}),
        );
        run.addArgs(&.{
            dxc_path,
            "-T",
            shader.profile,
            "-E",
            shader.entry_point,
            "-Fo",
        });
        const output = run.addOutputFileArg(
            b.fmt("{s}.dxil", .{shader.output_name}),
        );
        run.addFileArg(shader.source);

        if (last_step) |prev| run.step.dependOn(prev);
        last_step = &run.step;
        outputs.put(b.allocator, shader.output_name, output) catch @panic("OOM");
    }

    self.* = .{
        .step = last_step orelse {
            std.log.warn("hlsl: no shaders requested", .{});
            return null;
        },
        .outputs = outputs,
    };

    return self;
}

fn findDxc(b: *std.Build, arch: std.Target.Cpu.Arch) ?[]const u8 {
    const arch_str: []const u8 = switch (arch) {
        .x86_64 => "x64",
        .x86 => "x86",
        .aarch64 => "arm64",
        else => return null,
    };

    // The Windows SDK probe is Windows-host-only: on other hosts the SDK
    // discovery walks registry-shaped paths that 0.17 asserts are absolute
    // (measured: panic in Dir.openDirAbsolute during configure on macOS).
    if (builtin.target.os.tag == .windows) {
        if (findDxcInSdk(b, arch, arch_str)) |path| return path;
    }

    // Fall back to PATH (e.g. Vulkan SDK ships dxc.exe).
    return findDxcInPath(b);
}

fn findDxcInSdk(b: *std.Build, arch: std.Target.Cpu.Arch, arch_str: []const u8) ?[]const u8 {
    const sdk = std.zig.WindowsSdk.find(
        b.allocator,
        b.graph.io,
        arch,
        &b.graph.environ_map,
    ) catch return null;
    const w10 = sdk.windows10sdk orelse return null;

    const path = b.allocator.print(
        "{s}\\bin\\{s}\\{s}\\dxc.exe",
        .{ w10.path, w10.version, arch_str },
    ) catch return null;

    std.Io.Dir.accessAbsolute(b.graph.io, path, .{}) catch return null;
    return path;
}

fn findDxcInPath(b: *std.Build) ?[]const u8 {
    var code: u8 = undefined;
    const stdout = b.runAllowFail(
        &.{ "where", "dxc.exe" },
        &code,
        .ignore,
    ) catch return null;

    if (code != 0) return null;

    // "where" returns one path per line; take the first.
    const first_line = std.mem.sliceTo(stdout, '\n');
    const trimmed = std.mem.trimEnd(u8, first_line, "\r\n ");
    if (trimmed.len == 0) return null;

    // Dupe onto the build allocator so it outlives the process result.
    return b.allocator.dupe(u8, trimmed) catch return null;
}
