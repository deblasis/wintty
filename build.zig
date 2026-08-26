const std = @import("std");
const assert = std.debug.assert;
const builtin = @import("builtin");
const buildpkg = @import("src/build/main.zig");

/// App version from build.zig.zon.
const app_zon_version = @import("build.zig.zon").version;

/// Libghostty version. We use a separate version from the app.
const lib_version = "0.1.0-dev";

/// Minimum required zig version.
const minimum_zig_version = @import("build.zig.zon").minimum_zig_version;

/// Install prefix subdirectory for the `test-binaries` step.
const test_binaries_dir = "test-binaries";

comptime {
    buildpkg.requireZig(minimum_zig_version);
}

pub fn build(b: *std.Build) !void {
    // This defines all the available build options (e.g. `-D`). If you
    // want to know what options are available, you can run `--help` or
    // you can read `src/build/Config.zig`.

    // If we have a VERSION file (present in source tarballs) then we
    // use that as the version source of truth. Otherwise we fall back
    // to what is in the build.zig.zon.
    const file_version: ?[]const u8 = if (b.build_root.handle.readFileAlloc(
        b.graph.io,
        "VERSION",
        b.allocator,
        .limited(128),
    )) |content| std.mem.trim(
        u8,
        content,
        &std.ascii.whitespace,
    ) else |_| null;

    const config = try buildpkg.Config.init(
        b,
        file_version orelse app_zon_version,
        lib_version,
    );
    const test_filters = b.option(
        [][]const u8,
        "test-filter",
        "Filter for test. Only applies to Zig tests.",
    ) orelse &[0][]const u8{};

    // Ghostty dependencies used by many artifacts.
    const deps = try buildpkg.SharedDeps.init(b, &config);

    // The modules exported for Zig consumers of libghostty. If you're
    // writing a Zig program that uses libghostty, read this file.
    const mod = try buildpkg.GhosttyZig.init(
        b,
        &config,
        &deps,
    );

    // All our steps which we'll hook up later. The steps are shown
    // up here just so that they are more self-documenting.
    const run_step = b.step("run", "Run the app");
    const run_valgrind_step = b.step(
        "run-valgrind",
        "Run the app under valgrind",
    );
    const test_step = b.step("test", "Run tests");
    const test_lib_vt_step = b.step(
        "test-lib-vt",
        "Run libghostty-vt tests",
    );
    const test_lib_vt_build_step = b.step(
        "test-lib-vt-build",
        "Build libghostty-vt tests without running them (compile check)",
    );
    const test_lib_vt_schema_step = b.step(
        "test-lib-vt-schema",
        "Validate the libghostty-vt ABI type manifest",
    );
    // Every test binary the steps above run, built and installed side by
    // side without being run. A test binary is the only thing that can say
    // which `test` blocks it actually collected, so the reachability check
    // asks each one directly rather than inferring it from the import graph.
    //
    // Every `addTest` below must be registered here, and the check counts the
    // `addTest` calls in this file and refuses to report anything if the
    // count does not match the binaries it was handed. It cannot see the
    // other direction: a binary registered here but hung off no run step
    // would vouch for its files without ever running them.
    const test_binaries_step = b.step(
        "test-binaries",
        "Build every test binary without running it",
    );
    var test_binary_roots: std.ArrayList(u8) = .empty;
    const test_valgrind_step = b.step(
        "test-valgrind",
        "Run tests under valgrind",
    );
    const translations_step = b.step(
        "update-translations",
        "Update translation files",
    );

    // Ghostty resources like terminfo, shell integration, themes, etc.
    const resources = try buildpkg.GhosttyResources.init(b, &config, &deps);
    const i18n = if (config.i18n) try buildpkg.GhosttyI18n.init(b, &config) else null;

    // Ghostty executable, the actual runnable Ghostty program.
    const exe = try buildpkg.GhosttyExe.init(b, &config, &deps);

    // Ghostty docs
    const docs = try buildpkg.GhosttyDocs.init(b, &deps);
    if (config.emit_docs) {
        docs.install();
    } else if (config.target.result.os.tag.isDarwin()) {
        // If we aren't emitting docs we need to emit a placeholder so
        // our macOS xcodeproject builds since it expects the `share/man`
        // directory to exist to copy into the app bundle.
        docs.installDummy(b.getInstallStep());
    }

    // Ghostty webdata
    const webdata = try buildpkg.GhosttyWebdata.init(b, &deps);
    {
        // The website only needs these .mdx files, but the install step drags
        // in the whole app build to get them. This lets the docs be
        // regenerated on their own.
        const step = b.step("webdata", "Generate website reference data");
        for (webdata.steps) |s| step.dependOn(s);
    }
    if (config.emit_webdata) webdata.install();

    // Ghostty bench tools
    if (config.emit_bench) {
        const bench = try buildpkg.GhosttyBench.init(b, &deps);
        bench.install();
    }

    // Ghostty dist tarball
    const dist = try buildpkg.GhosttyDist.init(b, &config);
    {
        const step = b.step("dist", "Build the dist tarball");
        step.dependOn(dist.install_step);
        const check_step = b.step("distcheck", "Install and validate the dist tarball");
        check_step.dependOn(dist.check_step);
        check_step.dependOn(dist.install_step);
    }

    // libghostty-vt
    const native_freestanding = config.target.result.os.tag == .freestanding and
        !config.target.result.cpu.arch.isWasm();
    const libghostty_vt_shared: ?buildpkg.GhosttyLibVt = shared: {
        if (config.target.result.cpu.arch.isWasm()) {
            break :shared try buildpkg.GhosttyLibVt.initWasm(
                b,
                &mod,
            );
        }
        if (native_freestanding) break :shared null;

        break :shared try buildpkg.GhosttyLibVt.initShared(
            b,
            &mod,
        );
    };
    if (libghostty_vt_shared) |shared| {
        shared.install(b.getInstallStep());

        const type_schema_test = b.addSystemCommand(&.{"python3"});
        type_schema_test.addFileArg(b.path("src/terminal/c/types-schema-verify.py"));
        type_schema_test.addFileArg(b.path("src/terminal/c/types.schema.json"));
        type_schema_test.addFileArg(shared.output);
        test_lib_vt_schema_step.dependOn(&type_schema_test.step);
    } else {
        try test_lib_vt_schema_step.addError(
            "cannot execute the ABI manifest for a native freestanding target",
            .{},
        );
    }

    // libghostty-vt static lib
    const libghostty_vt_static = try buildpkg.GhosttyLibVt.initStatic(
        b,
        &mod,
    );
    if (config.is_dep) {
        // If we're a dependency, we need to install everything as-is
        // so that dep.artifact("ghostty-vt-static") works.
        libghostty_vt_static.install(b.getInstallStep());
    } else {
        // If we're not a dependency, we rename the static lib to
        // be idiomatic. On Windows, we use a distinct name to avoid
        // colliding with the DLL import library (ghostty-vt.lib).
        const static_lib_name = if (config.target.result.os.tag == .windows)
            "ghostty-vt-static.lib"
        else
            "libghostty-vt.a";
        b.getInstallStep().dependOn(&b.addInstallLibFile(
            libghostty_vt_static.output,
            static_lib_name,
        ).step);

        if (native_freestanding) {
            b.getInstallStep().dependOn(&b.addInstallDirectory(.{
                .source_dir = b.path("include/ghostty"),
                .install_dir = .header,
                .install_subdir = "ghostty",
                .include_extensions = &.{".h"},
            }).step);
        }
    }

    // libghostty-vt xcframework (Apple only, universal binary).
    // Only when building on macOS (not cross-compiling) since
    // xcodebuild is required.
    if (config.emit_lib_vt and
        config.emit_xcframework and
        builtin.os.tag.isDarwin() and
        config.target.result.os.tag.isDarwin())
    {
        const apple_libs = try buildpkg.GhosttyLibVt.initStaticAppleUniversal(
            b,
            &config,
            &deps,
            &mod,
        );
        const xcframework = buildpkg.GhosttyLibVt.xcframework(&apple_libs, b);
        b.getInstallStep().dependOn(xcframework.step);
    }

    // Helpgen
    if (config.emit_helpgen) deps.help_strings.install();

    // Runtime "none" is libghostty, anything else is an executable.
    if (config.app_runtime != .none) {
        if (config.emit_exe) {
            exe.install();
            resources.install();
            if (i18n) |v| v.install();
        }
    } else if (!config.emit_lib_vt) {
        // The macOS Ghostty Library
        //
        // This is NOT libghostty (even though its named that for historical
        // reasons). It is just the glue between Ghostty GUI on macOS and
        // the full Ghostty GUI core.
        const lib_shared = try buildpkg.GhosttyLib.initShared(b, &deps);
        const lib_static = try buildpkg.GhosttyLib.initStatic(b, &deps);

        // We shouldn't have this guard but we don't currently
        // build on macOS this way ironically so we need to fix that.
        if (!config.target.result.os.tag.isDarwin()) {
            lib_shared.installHeader(); // Only need one header
            if (config.target.result.os.tag == .windows) {
                lib_shared.install("ghostty.dll");
                if (lib_shared.implib) |implib| {
                    b.getInstallStep().dependOn(&b.addInstallLibFile(
                        implib,
                        "ghostty.lib",
                    ).step);
                }
                lib_static.install("ghostty-static.lib");
            } else {
                lib_shared.install("ghostty-internal.so");
                lib_static.install("ghostty-internal.a");
            }
        }
    }

    // macOS only artifacts. These will error if they're initialized for
    // other targets. In lib-vt mode emit_xcframework controls the lib-vt
    // xcframework above, not this one.
    if (!config.emit_lib_vt and config.target.result.os.tag.isDarwin() and
        (config.emit_xcframework or config.emit_macos_app))
    {
        // Ghostty xcframework
        const xcframework = try buildpkg.GhosttyXCFramework.init(
            b,
            &deps,
            config.xcframework_target,
        );
        if (config.emit_xcframework) {
            xcframework.install();

            // The xcframework build always installs resources because our
            // macOS xcode project contains references to them.
            resources.install();
            if (i18n) |v| v.install();
        }

        // Ghostty macOS app
        const macos_app = try buildpkg.GhosttyXcodebuild.init(
            b,
            &config,
            .{
                .xcframework = &xcframework,
                .docs = &docs,
                .i18n = if (i18n) |v| &v else null,
                .resources = &resources,
            },
        );
        if (config.emit_macos_app) {
            macos_app.install();
        }
    }

    // Run step
    run: {
        if (config.app_runtime != .none) {
            const run_cmd = b.addRunArtifact(exe.exe);
            if (b.args) |args| run_cmd.addArgs(args);

            // Set the proper resources dir so things like shell integration
            // work correctly. If we're running `zig build run` in Ghostty,
            // this also ensures it overwrites the release one with our debug
            // build.
            run_cmd.setEnvironmentVariable(
                "GHOSTTY_RESOURCES_DIR",
                b.getInstallPath(.prefix, "share/ghostty"),
            );

            run_step.dependOn(&run_cmd.step);
            break :run;
        }

        assert(config.app_runtime == .none);

        // On macOS we can run the macOS app. For "run" we always force
        // a native-only build so that we can run as quickly as possible.
        if (!config.emit_lib_vt and
            config.target.result.os.tag.isDarwin() and
            (config.emit_xcframework or config.emit_macos_app))
        {
            const xcframework_native = try buildpkg.GhosttyXCFramework.init(
                b,
                &deps,
                .native,
            );
            const macos_app_native_only = try buildpkg.GhosttyXcodebuild.init(
                b,
                &config,
                .{
                    .xcframework = &xcframework_native,
                    .docs = &docs,
                    .i18n = if (i18n) |v| &v else null,
                    .resources = &resources,
                },
            );

            // Run uses the native macOS app
            run_step.dependOn(&macos_app_native_only.open.step);

            // If we have no test filters, install the tests too
            if (test_filters.len == 0) {
                macos_app_native_only.addTestStepDependencies(test_step);
            }
        }
    }

    // Valgrind
    if (config.app_runtime != .none) {
        // We need to rebuild Ghostty with a baseline CPU target.
        const valgrind_exe = exe: {
            var valgrind_config = config;
            valgrind_config.target = valgrind_config.baselineTarget(b.graph.io);
            break :exe try buildpkg.GhosttyExe.init(
                b,
                &valgrind_config,
                &deps,
            );
        };

        const run_cmd = b.addSystemCommand(&.{
            "valgrind",
            "--leak-check=full",
            "--error-exitcode=1",
            "--num-callers=50",
            b.fmt("--suppressions={s}", .{b.pathFromRoot("valgrind.supp")}),
            "--gen-suppressions=all",
        });
        run_cmd.addArtifactArg(valgrind_exe.exe);
        if (b.args) |args| run_cmd.addArgs(args);
        run_valgrind_step.dependOn(&run_cmd.step);
    }

    // Zig module tests
    {
        const mod_vt_test = b.addTest(.{
            .name = "ghostty-vt-test",
            .root_module = mod.vt,
            .filters = test_filters,
        });
        const mod_vt_test_run = b.addRunArtifact(mod_vt_test);
        test_lib_vt_step.dependOn(&mod_vt_test_run.step);
        test_lib_vt_build_step.dependOn(&mod_vt_test.step);
        _ = installTestBinary(b, test_binaries_step, &test_binary_roots, mod_vt_test);

        const mod_vt_c_test = b.addTest(.{
            .name = "ghostty-vt-c-test",
            .root_module = mod.vt_c,
            .filters = test_filters,
        });
        const mod_vt_c_test_run = b.addRunArtifact(mod_vt_c_test);
        test_lib_vt_step.dependOn(&mod_vt_c_test_run.step);
        test_lib_vt_build_step.dependOn(&mod_vt_c_test.step);
        _ = installTestBinary(b, test_binaries_step, &test_binary_roots, mod_vt_c_test);
    }

    // Build-time code tests.
    //
    // src/build/ hangs off this file rather than off the src/main.zig test
    // root, and a test binary collects test blocks only from files its own
    // test and comptime blocks reach. src/build/test.zig says which files
    // those are and why they need a root of their own. Always the host, since
    // build-time code runs on the host whatever the build is targeting -- and
    // outside the emit_lib_vt guard below for the same reason.
    {
        const build_test = b.addTest(.{
            .name = "ghostty-build-test",
            .filters = test_filters,
            .root_module = b.createModule(.{
                .root_source_file = b.path("src/build/test.zig"),
                .target = b.graph.host,
                .optimize = .Debug,
            }),
        });
        test_step.dependOn(&b.addRunArtifact(build_test).step);
        _ = installTestBinary(b, test_binaries_step, &test_binary_roots, build_test);
    }

    // Tests (skip when building libghostty-vt)
    if (!config.emit_lib_vt) {
        // Full unit tests
        const test_exe = b.addTest(.{
            .name = "ghostty-test",
            .filters = test_filters,
            .root_module = b.createModule(.{
                .root_source_file = b.path("src/main.zig"),
                .target = config.baselineTarget(b.graph.io),
                .optimize = .Debug,
                .strip = false,
                .omit_frame_pointer = false,
                .unwind_tables = .sync,
            }),
            // Crash on x86_64 without this
            .use_llvm = true,
        });
        if (config.emit_test_exe) {
            const test_exe_install = b.addInstallArtifact(test_exe, .{});
            config.addPatchElf(test_exe, &test_exe_install.step);
            test_step.dependOn(&test_exe_install.step);
        }
        _ = try deps.add(test_exe);
        config.addPatchElf(
            test_exe,
            installTestBinary(b, test_binaries_step, &test_binary_roots, test_exe),
        );

        addGhosttyH(b, test_exe.root_module, config.baselineTarget(b.graph.io), .Debug);

        // Normal test running
        const test_run = b.addRunArtifact(test_exe);
        config.addPatchElf(test_exe, &test_run.step);
        test_step.dependOn(&test_run.step);

        // Normal tests always test our libghostty modules
        //test_step.dependOn(test_lib_vt_step);

        // Valgrind test running
        const valgrind_run = b.addSystemCommand(&.{
            "valgrind",
            "--leak-check=full",
            "--error-exitcode=1",
            "--num-callers=50",
            b.fmt("--suppressions={s}", .{b.pathFromRoot("valgrind.supp")}),
            "--gen-suppressions=all",
        });
        valgrind_run.addArtifactArg(test_exe);
        config.addPatchElf(test_exe, &valgrind_run.step);
        test_valgrind_step.dependOn(&valgrind_run.step);
    }

    // After the last installTestBinary above: this freezes the manifest.
    installTestBinaryRoots(b, test_binaries_step, &test_binary_roots);

    // update-translations does what it sounds like and updates the "pot"
    // files. These should be committed to the repo.
    if (i18n) |v| {
        translations_step.dependOn(v.update_step);
    } else {
        try translations_step.addError("cannot update translations when i18n is disabled", .{});
    }
}

/// Install a test binary into its own prefix subdirectory and record where
/// its root module is rooted. Returns the install step, for callers that need
/// to hang anything else off it.
///
/// The root path is what lets a qualified test name be turned back into a
/// file: names are relative to the module root, so `src/main.zig` and
/// `src/terminal/main.zig` both answer to `main` without it.
fn installTestBinary(
    b: *std.Build,
    step: *std.Build.Step,
    roots: *std.ArrayList(u8),
    compile: *std.Build.Step.Compile,
) *std.Build.Step {
    const dir: std.Build.Step.InstallArtifact.Options.Dir = .{
        .override = .{ .custom = test_binaries_dir },
    };
    const install = b.addInstallArtifact(compile, .{
        .dest_dir = dir,
        .pdb_dir = dir,
    });
    step.dependOn(&install.step);

    const root = compile.root_module.root_source_file orelse
        @panic("test binary has no root source file");
    const sub_path = switch (root) {
        .src_path => |src| src.sub_path,
        else => @panic("test binary is rooted outside the source tree"),
    };
    roots.appendSlice(
        b.allocator,
        b.fmt("{s}\t{s}\n", .{ compile.name, sub_path }),
    ) catch @panic("OOM");

    return &install.step;
}

/// Write the recorded module roots next to the binaries they describe.
fn installTestBinaryRoots(
    b: *std.Build,
    step: *std.Build.Step,
    roots: *const std.ArrayList(u8),
) void {
    const wf = b.addWriteFiles();
    step.dependOn(&b.addInstallFileWithDir(
        wf.add("roots.tsv", roots.items),
        .{ .custom = test_binaries_dir },
        "roots.tsv",
    ).step);
}

fn addGhosttyH(
    b: *std.Build,
    module: *std.Build.Module,
    target: std.Build.ResolvedTarget,
    optimize: std.builtin.OptimizeMode,
) void {
    const translate_c = b.lazyImport(@This(), "translate_c") orelse return;
    const translate_c_dep = b.lazyDependency("translate_c", .{}) orelse return;

    const translated: translate_c.Translator = .init(translate_c_dep, .{
        .c_source_file = b.addWriteFiles().add(
            "hb_c.h",
            \\#include <ghostty.h>
            ,
        ),
        .target = target,
        .optimize = optimize,
        .link_libc = true,
    });

    translated.addSystemIncludePath(b.path("include"));

    module.addImport("ghostty.h", translated.mod);
}
