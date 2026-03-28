/// A zig build step that compiles a set of ".hlsl" files into
/// ".cso" (compiled shader object) files using fxc.exe from the
/// Windows SDK.
const HlslStep = @This();

const std = @import("std");
const Step = std.Build.Step;
const RunStep = std.Build.Step.Run;
const LazyPath = std.Build.LazyPath;

pub const ShaderEntry = struct {
    /// The HLSL source file.
    source: LazyPath,
    /// Shader profile (e.g. "vs_5_0", "ps_5_0").
    profile: []const u8,
    /// Entry point function name (e.g. "VSMain", "PSMain").
    entry_point: []const u8,
    /// Output name (e.g. "cell_vs" -> "cell_vs.cso").
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

    // Find fxc.exe from the Windows SDK.
    const fxc_path = findFxc(b, opts.target.result.cpu.arch) orelse {
        std.log.warn("fxc.exe not found; HLSL shaders will not be compiled", .{});
        return null;
    };

    var outputs: std.StringHashMapUnmanaged(LazyPath) = .empty;
    var step_wip = Step.init(.{
        .id = .custom,
        .name = "hlsl",
        .owner = b,
    });

    for (opts.shaders) |shader| {
        const run = RunStep.create(
            b,
            b.fmt("hlsl {s}", .{shader.output_name}),
        );
        run.addArgs(&.{
            fxc_path,
            "/T",
            shader.profile,
            "/E",
            shader.entry_point,
            "/Fo",
        });
        const output = run.addOutputFileArg(
            b.fmt("{s}.cso", .{shader.output_name}),
        );
        run.addFileArg(shader.source);

        outputs.put(b.allocator, shader.output_name, output) catch @panic("OOM");
        step_wip.dependOn(&run.step);
    }

    self.* = .{
        .step = b.allocator.create(Step) catch @panic("OOM"),
        .outputs = outputs,
    };
    self.step.* = step_wip;

    return self;
}

fn findFxc(b: *std.Build, arch: std.Target.Cpu.Arch) ?[]const u8 {
    const arch_str: []const u8 = switch (arch) {
        .x86_64 => "x64",
        .x86 => "x86",
        .aarch64 => "arm64",
        else => return null,
    };

    const sdk = std.zig.WindowsSdk.find(b.allocator, arch) catch return null;
    const w10 = sdk.windows10sdk orelse return null;

    const path = std.fmt.allocPrint(
        b.allocator,
        "{s}\\bin\\{s}\\{s}\\fxc.exe",
        .{ w10.path, w10.version, arch_str },
    ) catch return null;

    std.fs.accessAbsolute(path, .{}) catch return null;
    return path;
}
