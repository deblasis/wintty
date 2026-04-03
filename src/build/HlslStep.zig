/// A zig build step that compiles a set of ".hlsl" files into
/// ".dxil" (DirectX Intermediate Language) files using dxc.exe,
/// the DirectX Shader Compiler.
const HlslStep = @This();

const std = @import("std");
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

    // Find dxc.exe from the Windows SDK or PATH.
    const dxc_path = findDxc(b, opts.target.result.cpu.arch) orelse {
        std.log.warn("dxc.exe not found; HLSL shaders will not be compiled", .{});
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

fn findDxc(b: *std.Build, arch: std.Target.Cpu.Arch) ?[]const u8 {
    const arch_str: []const u8 = switch (arch) {
        .x86_64 => "x64",
        .x86 => "x86",
        .aarch64 => "arm64",
        else => return null,
    };

    // Try the Windows SDK first.
    if (findDxcInSdk(b, arch, arch_str)) |path| return path;

    // Fall back to PATH (e.g. Vulkan SDK ships dxc.exe).
    return findDxcInPath(b);
}

fn findDxcInSdk(b: *std.Build, arch: std.Target.Cpu.Arch, arch_str: []const u8) ?[]const u8 {
    const sdk = std.zig.WindowsSdk.find(b.allocator, arch) catch return null;
    const w10 = sdk.windows10sdk orelse return null;

    const path = std.fmt.allocPrint(
        b.allocator,
        "{s}\\bin\\{s}\\{s}\\dxc.exe",
        .{ w10.path, w10.version, arch_str },
    ) catch return null;

    std.fs.accessAbsolute(path, .{}) catch return null;
    return path;
}

fn findDxcInPath(b: *std.Build) ?[]const u8 {
    const result = std.process.Child.run(.{
        .allocator = b.allocator,
        .argv = &.{ "where", "dxc.exe" },
    }) catch return null;

    if (result.term.Exited != 0) return null;

    // "where" returns one path per line; take the first.
    const first_line = std.mem.sliceTo(result.stdout, '\n');
    const trimmed = std.mem.trimRight(u8, first_line, "\r\n ");
    if (trimmed.len == 0) return null;

    // Dupe onto the build allocator so it outlives the process result.
    return b.allocator.dupe(u8, trimmed) catch return null;
}
