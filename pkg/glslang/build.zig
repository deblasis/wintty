const std = @import("std");

pub fn build(b: *std.Build) !void {
    const target = b.standardTargetOptions(.{});
    const optimize = b.standardOptimizeOption(.{});

    const module = b.addModule("glslang", .{
        .root_source_file = b.path("main.zig"),
        .target = target,
        .optimize = optimize,
    });

    const upstream = b.lazyDependency("glslang", .{});
    const lib = try buildGlslang(b, upstream, target, optimize);
    b.installArtifact(lib);

    if (upstream) |v| module.addIncludePath(v.path(""));
    module.addIncludePath(b.path("override"));

    if (target.query.isNative()) {
        const test_exe = b.addTest(.{
            .name = "test",
            .root_module = b.createModule(.{
                .root_source_file = b.path("main.zig"),
                .target = target,
                .optimize = optimize,
            }),
        });
        test_exe.linkLibrary(lib);
        const tests_run = b.addRunArtifact(test_exe);
        const test_step = b.step("test", "Run tests");
        test_step.dependOn(&tests_run.step);

        // Uncomment this if we're debugging tests
        // b.installArtifact(test_exe);
    }
}

fn buildGlslang(
    b: *std.Build,
    upstream_: ?*std.Build.Dependency,
    target: std.Build.ResolvedTarget,
    optimize: std.builtin.OptimizeMode,
) !*std.Build.Step.Compile {
    const lib = b.addLibrary(.{
        .name = "glslang",
        .root_module = b.createModule(.{
            .target = target,
            .optimize = optimize,
        }),
        .linkage = .static,
    });
    lib.linkLibC();

    // On Windows MSVC, use a pre-built static library compiled with MSVC cl.exe
    // to avoid C++ ABI issues between Zig's bundled Clang and MSVC's C++ runtime
    // when the resulting DLL is loaded by .NET.
    if (target.result.abi == .msvc) {
        // Merge the MSVC-compiled objects directly into this library.
        // We use a dummy C file so the library has at least one compilation unit.
        lib.addCSourceFiles(.{
            .root = b.path("msvc_build"),
            .flags = &.{},
            .files = &.{"dummy.c"},
        });
        // Add each MSVC object file individually
        const msvc_build = b.path("msvc_build");
        lib.addObjectFile(msvc_build.path(b, "CodeGen.obj"));
        lib.addObjectFile(msvc_build.path(b, "Link.obj"));
        lib.addObjectFile(msvc_build.path(b, "glslang_tab.obj"));
        lib.addObjectFile(msvc_build.path(b, "attribute.obj"));
        lib.addObjectFile(msvc_build.path(b, "Constant.obj"));
        lib.addObjectFile(msvc_build.path(b, "iomapper.obj"));
        lib.addObjectFile(msvc_build.path(b, "InfoSink.obj"));
        lib.addObjectFile(msvc_build.path(b, "Initialize.obj"));
        lib.addObjectFile(msvc_build.path(b, "IntermTraverse.obj"));
        lib.addObjectFile(msvc_build.path(b, "Intermediate.obj"));
        lib.addObjectFile(msvc_build.path(b, "ParseContextBase.obj"));
        lib.addObjectFile(msvc_build.path(b, "ParseHelper.obj"));
        lib.addObjectFile(msvc_build.path(b, "PoolAlloc.obj"));
        lib.addObjectFile(msvc_build.path(b, "RemoveTree.obj"));
        lib.addObjectFile(msvc_build.path(b, "Scan.obj"));
        lib.addObjectFile(msvc_build.path(b, "ShaderLang.obj"));
        lib.addObjectFile(msvc_build.path(b, "SpirvIntrinsics.obj"));
        lib.addObjectFile(msvc_build.path(b, "SymbolTable.obj"));
        lib.addObjectFile(msvc_build.path(b, "Versions.obj"));
        lib.addObjectFile(msvc_build.path(b, "intermOut.obj"));
        lib.addObjectFile(msvc_build.path(b, "limits.obj"));
        lib.addObjectFile(msvc_build.path(b, "linkValidate.obj"));
        lib.addObjectFile(msvc_build.path(b, "parseConst.obj"));
        lib.addObjectFile(msvc_build.path(b, "reflection.obj"));
        lib.addObjectFile(msvc_build.path(b, "Pp.obj"));
        lib.addObjectFile(msvc_build.path(b, "PpAtom.obj"));
        lib.addObjectFile(msvc_build.path(b, "PpContext.obj"));
        lib.addObjectFile(msvc_build.path(b, "PpScanner.obj"));
        lib.addObjectFile(msvc_build.path(b, "PpTokens.obj"));
        lib.addObjectFile(msvc_build.path(b, "propagateNoContraction.obj"));
        lib.addObjectFile(msvc_build.path(b, "glslang_c_interface.obj"));
        lib.addObjectFile(msvc_build.path(b, "ResourceLimits.obj"));
        lib.addObjectFile(msvc_build.path(b, "resource_limits_c.obj"));
        lib.addObjectFile(msvc_build.path(b, "ossource.obj"));
        lib.addObjectFile(msvc_build.path(b, "GlslangToSpv.obj"));
        lib.addObjectFile(msvc_build.path(b, "InReadableOrder.obj"));
        lib.addObjectFile(msvc_build.path(b, "Logger.obj"));
        lib.addObjectFile(msvc_build.path(b, "SpvBuilder.obj"));
        lib.addObjectFile(msvc_build.path(b, "SpvPostProcess.obj"));
        lib.addObjectFile(msvc_build.path(b, "doc.obj"));
        lib.addObjectFile(msvc_build.path(b, "disassemble.obj"));
        lib.addObjectFile(msvc_build.path(b, "spirv_c_interface.obj"));
        // Static C++ stdlib for MSVC-compiled objects (/MT)
        lib.linkSystemLibrary("libcpmt");
    } else {
        lib.linkLibCpp();
    }

    if (upstream_) |upstream| lib.addIncludePath(upstream.path(""));
    lib.addIncludePath(b.path("override"));

    if (target.result.os.tag.isDarwin()) {
        const apple_sdk = @import("apple_sdk");
        try apple_sdk.addPaths(b, lib);
    }

    // Non-MSVC: compile C++ sources with Zig's Clang
    if (target.result.abi != .msvc) {
        var flags: std.ArrayList([]const u8) = .empty;
        defer flags.deinit(b.allocator);
        try flags.appendSlice(b.allocator, &.{
            "-fno-sanitize=undefined",
            "-fno-sanitize-trap=undefined",
        });
        try flags.append(b.allocator, "-std=c++17");
        try flags.append(b.allocator, "-DNDEBUG");

        if (target.result.os.tag == .freebsd or target.result.abi == .musl) {
            try flags.append(b.allocator, "-fPIC");
        }

        if (upstream_) |upstream| {
            lib.addCSourceFiles(.{
                .root = upstream.path(""),
                .flags = flags.items,
                .files = &.{
                    "glslang/GenericCodeGen/CodeGen.cpp",
                    "glslang/GenericCodeGen/Link.cpp",
                    "glslang/MachineIndependent/glslang_tab.cpp",
                    "glslang/MachineIndependent/attribute.cpp",
                    "glslang/MachineIndependent/Constant.cpp",
                    "glslang/MachineIndependent/iomapper.cpp",
                    "glslang/MachineIndependent/InfoSink.cpp",
                    "glslang/MachineIndependent/Initialize.cpp",
                    "glslang/MachineIndependent/IntermTraverse.cpp",
                    "glslang/MachineIndependent/Intermediate.cpp",
                    "glslang/MachineIndependent/ParseContextBase.cpp",
                    "glslang/MachineIndependent/ParseHelper.cpp",
                    "glslang/MachineIndependent/PoolAlloc.cpp",
                    "glslang/MachineIndependent/RemoveTree.cpp",
                    "glslang/MachineIndependent/Scan.cpp",
                    "glslang/MachineIndependent/ShaderLang.cpp",
                    "glslang/MachineIndependent/SpirvIntrinsics.cpp",
                    "glslang/MachineIndependent/SymbolTable.cpp",
                    "glslang/MachineIndependent/Versions.cpp",
                    "glslang/MachineIndependent/intermOut.cpp",
                    "glslang/MachineIndependent/limits.cpp",
                    "glslang/MachineIndependent/linkValidate.cpp",
                    "glslang/MachineIndependent/parseConst.cpp",
                    "glslang/MachineIndependent/reflection.cpp",
                    "glslang/MachineIndependent/preprocessor/Pp.cpp",
                    "glslang/MachineIndependent/preprocessor/PpAtom.cpp",
                    "glslang/MachineIndependent/preprocessor/PpContext.cpp",
                    "glslang/MachineIndependent/preprocessor/PpScanner.cpp",
                    "glslang/MachineIndependent/preprocessor/PpTokens.cpp",
                    "glslang/MachineIndependent/propagateNoContraction.cpp",
                    "glslang/CInterface/glslang_c_interface.cpp",
                    "glslang/ResourceLimits/ResourceLimits.cpp",
                    "glslang/ResourceLimits/resource_limits_c.cpp",
                    "SPIRV/GlslangToSpv.cpp",
                    "SPIRV/InReadableOrder.cpp",
                    "SPIRV/Logger.cpp",
                    "SPIRV/SpvBuilder.cpp",
                    "SPIRV/SpvPostProcess.cpp",
                    "SPIRV/doc.cpp",
                    "SPIRV/disassemble.cpp",
                    "SPIRV/CInterface/spirv_c_interface.cpp",
                },
            });

            if (target.result.os.tag != .windows) {
                lib.addCSourceFiles(.{
                    .root = upstream.path(""),
                    .flags = flags.items,
                    .files = &.{
                        "glslang/OSDependent/Unix/ossource.cpp",
                    },
                });
            } else {
                lib.addCSourceFiles(.{
                    .root = upstream.path(""),
                    .flags = flags.items,
                    .files = &.{
                        "glslang/OSDependent/Windows/ossource.cpp",
                    },
                });
            }

            lib.installHeadersDirectory(
                upstream.path(""),
                "",
                .{ .include_extensions = &.{".h"} },
            );
        }
    }

    return lib;
}
