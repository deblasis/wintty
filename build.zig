const std = @import("std");
const assert = std.debug.assert;
const builtin = @import("builtin");
const buildpkg = @import("src/build/main.zig");

const c_deps = @import("src/build/c_deps.zig");

/// App version from build.zig.zon.
const app_zon_version = @import("build.zig.zon").version;

/// The libghostty-vt version, released separately from the app with
/// `vt-X.Y.Z` tags. See `Config.libVersion` for how builds use it.
///
/// CMakeLists.txt and CI read this line with a regular expression, so
/// keep it on one line in this form.
const lib_version = "1.0.2-dev";

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

    // Source tarballs have no git history, so they carry their version
    // in a VERSION (app) or LIB_VERSION (libghostty-vt) file.
    const config = try buildpkg.Config.init(b, .{
        .app = app_zon_version,
        .app_file = readVersionFile(b, "VERSION"),
        .lib = lib_version,
        .lib_file = readVersionFile(b, "LIB_VERSION"),
    });
    const test_filters = b.option(
        [][]const u8,
        "test-filter",
        "Filter for test. Only applies to Zig tests.",
    ) orelse &[0][]const u8{};

    // Declared for every target so passing it to a build that never uses it
    // (not Windows, or libghostty-vt only) is not an invalid-option error.
    const dxc_dll = b.option(
        []const u8,
        "dxc-dll",
        "dxcompiler.dll to place beside the Windows test binary",
    );
    // How many processes run each large test binary at once. Zig's test
    // runner runs tests one after another on a single thread, and the
    // Debug suite takes many minutes that way. With N shards the build
    // starts N copies of the binary and each copy runs every N-th test
    // (see src/build/test_runner.zig), so the suite finishes in about
    // 1/N of the time on a machine with N free cores.
    //
    // The default is the CPU count, up to 64. Use 1 to run every test in
    // a single process, in order, like Zig's default test runner:
    //
    //     zig build test -Dtest-shards=1
    const test_shards: u32 = b.option(
        u32,
        "test-shards",
        "Processes to split each test binary across (default: CPU count, at most 64)",
    ) orelse @intCast(@min(std.Thread.getCpuCount() catch 1, 64));
    if (test_shards == 0) {
        std.debug.print("-Dtest-shards must be at least 1\n", .{});
        std.process.exit(1);
    }
    const test_runner: std.Build.Step.Compile.TestRunner = .{
        .path = b.path("src/build/test_runner.zig"),
        .mode = .server,
    };

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
    const test_lib_vt_abi_step = b.step(
        "test-lib-vt-abi",
        "Fail if libghostty-vt changed its ABI in a way that is not an addition",
    );
    const update_lib_vt_abi_step = b.step(
        "update-lib-vt-abi",
        "Record the libghostty-vt ABI of the current target in src/terminal/c/abi",
    );
    const test_valgrind_step = b.step(
        "test-valgrind",
        "Run tests under valgrind",
    );
    const translations_step = b.step(
        "update-translations",
        "Update translation files",
    );

    // Ghostty resources like terminfo, shell integration, themes, etc.
    // Only the app uses them, so a libghostty-vt build skips them, which
    // also keeps it from downloading the themes package. This is null only
    // when emit_lib_vt is set without emit_exe, and every use below is in
    // an app-only branch.
    const resources: ?buildpkg.GhosttyResources = if (config.emit_lib_vt and
        !config.emit_exe)
        null
    else
        try buildpkg.GhosttyResources.init(b, &config, &deps);
    const i18n = if (config.i18n) try buildpkg.GhosttyI18n.init(b, &config) else null;

    // Ghostty executable, the actual runnable Ghostty program.
    const exe = try buildpkg.GhosttyExe.init(b, &config, &deps);

    // Ghostty docs
    const docs = try buildpkg.GhosttyDocs.init(b, &deps);
    if (config.emit_docs) {
        docs.install();
    } else if (config.target.result.os.tag.isDarwin() and
        !config.emit_lib_vt)
    {
        // If we aren't emitting docs we need to emit a placeholder so
        // our macOS xcodeproject builds since it expects the `share/man`
        // directory to exist to copy into the app bundle. A libghostty-vt
        // build has no app bundle, so it doesn't need one.
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
        // Only libghostty-vt builds (-Demit-lib-vt) have this step.
        if (dist.deps_install_step) |deps_install| {
            const deps_step = b.step(
                "dist-deps",
                "Build the libghostty-vt dependency tarball for offline builds (needs network)",
            );
            deps_step.dependOn(deps_install);
        }
    }

    // libghostty-vt
    //
    // The libraries are always defined so that steps like test-lib-vt can
    // use them, but they are only installed by a libghostty-vt build or
    // when Ghostty is a dependency. A dependency must install them so
    // that dep.artifact("ghostty-vt") and friends can find them. App
    // builds install none of it, so they don't ship a stray C library.
    const install_lib_vt = config.emit_lib_vt or config.is_dep;
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
            config.is_dep,
        );
    };
    if (libghostty_vt_shared) |shared| {
        if (install_lib_vt) shared.install(b.getInstallStep());

        const type_schema_test = b.addSystemCommand(&.{"python3"});
        type_schema_test.addFileArg(b.path("src/terminal/c/types-schema-verify.py"));
        type_schema_test.addFileArg(b.path("src/terminal/c/types.schema.json"));
        type_schema_test.addFileArg(shared.output);
        test_lib_vt_schema_step.dependOn(&type_schema_test.step);

        shared.addAbiCheck(b, config.target.result, test_lib_vt_abi_step, update_lib_vt_abi_step);
        // A test binary over the ABI-check root, registered for the
        // reachability check: addAbiCheck runs the same blocks under
        // test-lib-vt-abi, which the default ladder does not run.
        const abi_check_test = b.addTest(.{
            .name = "ghostty-vt-abi-check-test",
            .root_module = b.createModule(.{
                .root_source_file = b.path("src/terminal/c/abi/check.zig"),
                .target = b.graph.host,
            }),
            .filters = test_filters,
        });
        installTestBinaryAt(
            b,
            test_binaries_step,
            &test_binary_roots,
            abi_check_test,
            "src/terminal/c/abi/check.zig",
        );
    } else {
        _ = b.addFail("cannot execute the ABI manifest for a native freestanding target");
    }

    // The packages libghostty-vt uses, for gen-lib-vt-deps.sh. Defined
    // only for library builds so app builds skip reading every package's
    // build.zig.zon.
    if (config.emit_lib_vt) _ = try buildpkg.LibVtPackages.init(b, mod.vt_c);

    // libghostty-vt static lib
    const libghostty_vt_static = try buildpkg.GhosttyLibVt.initStatic(
        b,
        &mod,
    );
    if (config.is_dep) {
        // If we're a dependency, we need to install everything as-is
        // so that dep.artifact("ghostty-vt-static") works.
        libghostty_vt_static.install(b.getInstallStep());
    } else if (install_lib_vt) {
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
        builtin.target.os.tag.isDarwin() and
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
            if (resources) |v| v.install();
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
                // The Windows app ships its resources beside its executable.
                // The library build never runs `install()`, which only the
                // executable build depends on, so the shippable steps are
                // depended on individually (see GhosttyResources.windows).
                for (resources.?.windows) |step| b.getInstallStep().dependOn(step);

                // And then prove it. Deleting the line above used to be
                // invisible: the whole suite stayed green while the shipped
                // app went back to finding no resources directory at all.
                try resources.?.assertWindowsInstall(b);

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
            resources.?.install();
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
                .resources = &resources.?,
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
            run_cmd.addPassthruArgs();

            // Set the proper resources dir so things like shell integration
            // work correctly. If we're running `zig build run` in Ghostty,
            // this also ensures it overwrites the release one with our debug
            // build.
            // FIXME: This is not possible after Zig 0.17.
            // run_cmd.setEnvironmentVariable(
            //     "GHOSTTY_RESOURCES_DIR",
            //     b.getInstallPath(.prefix, "share/ghostty"),
            // );

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
                    .resources = &resources.?,
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
            "--gen-suppressions=all",
        });
        run_cmd.addFileArg2(b.path("valgrind.supp"), .{ .prefix = "--suppressions=" });
        run_cmd.addArtifactArg(valgrind_exe.exe);
        run_cmd.addPassthruArgs();
        run_valgrind_step.dependOn(&run_cmd.step);
    }

    // Zig module tests
    {
        const mod_vt_test = b.addTest(.{
            .name = "ghostty-vt-test",
            .root_module = mod.vt,
            .filters = test_filters,
            .test_runner = test_runner,
        });
        try c_deps.add(b, .@"ghostty-vt-h", mod_vt_test.root_module, &config, .{});
        _ = addShardedTestRuns(b, &config, mod_vt_test, test_lib_vt_step, test_shards);
        _ = installTestBinary(b, test_binaries_step, &test_binary_roots, mod_vt_test);

        const mod_vt_c_test = b.addTest(.{
            .name = "ghostty-vt-c-test",
            .root_module = try buildpkg.GhosttyZig.initVtCTest(b, &config, &deps),
            .filters = test_filters,
            .test_runner = test_runner,
        });
        _ = addShardedTestRuns(b, &config, mod_vt_c_test, test_lib_vt_step, test_shards);
        _ = installTestBinary(b, test_binaries_step, &test_binary_roots, mod_vt_c_test);

        // The committed version.h, the symbol version nodes in
        // ghostty-vt.map and the version in the nix package must all match
        // lib_version, so a version bump has to update them together. The
        // test embeds these files, so they are all copied into one
        // directory.
        const version_test = version_test: {
            const wf = b.addWriteFiles();
            const root = wf.addCopyFile(
                b.path("src/terminal/c/version_test.zig"),
                "version_test.zig",
            );
            _ = wf.addCopyFile(
                b.path("include/ghostty/vt/version.h"),
                "version.h",
            );
            _ = wf.addCopyFile(
                b.path("src/terminal/c/ghostty-vt.map"),
                "ghostty-vt.map",
            );
            _ = wf.addCopyFile(
                b.path("nix/libghostty-vt.nix"),
                "libghostty-vt.nix",
            );
            const opts = b.addOptions();
            opts.addOption(std.SemanticVersion, "lib_version", config.lib_version);
            const test_mod = b.createModule(.{
                .root_source_file = root,
                .target = b.graph.host,
                .optimize = .debug,
            });
            test_mod.addOptions("lib_version", opts);
            break :version_test b.addTest(.{
                .name = "ghostty-vt-version-test",
                .root_module = test_mod,
                .filters = test_filters,
            });
        };
        test_lib_vt_step.dependOn(&b.addRunArtifact(version_test).step);
        // Registered for the reachability check: this binary carries the
        // version_test.zig test blocks. Its root is generated by
        // addWriteFiles, so the root is named explicitly.
        installTestBinaryAt(
            b,
            test_binaries_step,
            &test_binary_roots,
            version_test,
            "src/terminal/c/version_test.zig",
        );

        // The public headers must compile without warnings in every
        // language standard we support, so compile a file that includes
        // vt.h once per standard with strict warnings as errors.
        {
            const wf = b.addWriteFiles();
            const c_src = wf.add("vt_h_test.c", "#include <ghostty/vt.h>\n");
            const cpp_src = wf.add("vt_h_test.cpp", "#include <ghostty/vt.h>\n");
            const warnings: []const []const u8 = &.{
                "-Wall",
                "-Wextra",
                "-Wpedantic",
                "-Werror",
            };
            const Case = struct { std: []const u8, src: std.Build.LazyPath };
            const cases: []const Case = &.{
                .{ .std = "c11", .src = c_src },
                .{ .std = "c23", .src = c_src },
                .{ .std = "c++17", .src = cpp_src },
            };
            for (cases) |case| {
                const flags = std.mem.concat(b.allocator, []const u8, &.{
                    &.{b.fmt("-std={s}", .{case.std})},
                    warnings,
                }) catch @panic("OOM");
                const obj_mod = b.createModule(.{
                    .target = b.graph.host,
                    .optimize = .debug,
                    .link_libc = true,
                    .link_libcpp = std.mem.startsWith(u8, case.std, "c++"),
                });
                obj_mod.addIncludePath(b.path("include"));
                obj_mod.addCSourceFile(.{ .file = case.src, .flags = flags });
                const obj = b.addObject(.{
                    .name = b.fmt("vt-h-{s}", .{case.std}),
                    .root_module = obj_mod,
                });
                test_lib_vt_step.dependOn(&obj.step);
            }
        }

        if (config.emit_test_exe) {
            // With -Demit-test-exe, test-lib-vt-build builds only the C API
            // test binary and installs it to zig-out/bin, so it can run on
            // another machine. CI uses this to run it in a FreeBSD VM. The
            // C API binary already runs every test in the Zig one, and
            // compiling both at once can run a CI runner out of memory.
            const install = b.addInstallArtifact(mod_vt_c_test, .{});
            test_lib_vt_build_step.dependOn(&install.step);
        } else {
            test_lib_vt_build_step.dependOn(&mod_vt_test.step);
            test_lib_vt_build_step.dependOn(&mod_vt_c_test.step);
        }
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
        // Full unit tests, built twice.
        //
        // ghostty-test is the normal test binary. It is built with Zig's
        // self-hosted backend, which compiles it minutes faster than LLVM.
        // In the Nix dev shell, its run steps start it through the
        // shell's dynamic linker rather than patching it (see
        // Config.addLoaderArgs).
        //
        // ghostty-test-valgrind holds the same tests built with LLVM, for
        // `zig build test-valgrind`. Valgrind can't start a program
        // through the dynamic linker, so this binary is patched with
        // patchelf instead, and patchelf only accepts LLVM's ELF output.
        var test_exes: [2]*std.Build.Step.Compile = undefined;
        for (&test_exes, [_]bool{ false, true }) |*test_exe_ptr, valgrind| {
            test_exe_ptr.* = b.addTest(.{
                .name = if (valgrind) "ghostty-test-valgrind" else "ghostty-test",
                .filters = test_filters,
                .root_module = b.createModule(.{
                    .root_source_file = b.path("src/main.zig"),
                    .target = config.baselineTarget(b.graph.io),
                    .optimize = .debug,
                    .strip = false,
                    .omit_frame_pointer = false,
                    .unwind_tables = .sync,
                }),
                .use_llvm = if (valgrind) true else null,
                .test_runner = test_runner,
            });
            _ = try deps.add(test_exe_ptr.*, .Debug);
            try c_deps.add(b, .@"ghostty-h", test_exe_ptr.*.root_module, &config, .{
                .target = config.baselineTarget(b.graph.io),
                .optimize = .Debug,
            });
            try c_deps.add(b, .@"ghostty-vt-h", test_exe_ptr.*.root_module, &config, .{
                .target = config.baselineTarget(b.graph.io),
                .optimize = .Debug,
            });
        }
        const test_exe, const valgrind_exe = test_exes;

        // The installed binary is not patched for the Nix dev shell,
        // because patchelf can't rewrite the self-hosted backend's output.
        // To run it there, start it through the shell's dynamic linker
        // the same way the test run steps do (see Config.addLoaderArgs).
        var test_exe_bin_install: ?*std.Build.Step = null;
        if (config.emit_test_exe) {
            const test_exe_install = b.addInstallArtifact(test_exe, .{});
            test_step.dependOn(&test_exe_install.step);
            test_exe_bin_install = &test_exe_install.step;
        }
        // The test binary pins Debug above, so its dependencies stay Debug
        // too; -Doptimize and -Dvt-safe must not split the two apart.
        const test_exe_install_step = installTestBinary(b, test_binaries_step, &test_binary_roots, test_exe);
        _ = config.addPatchElf(test_exe, test_exe_install_step);
        _ = installTestBinary(b, test_binaries_step, &test_binary_roots, valgrind_exe);

        // Normal test running
        const test_runs = addShardedTestRuns(b, &config, test_exe, test_step, test_shards);

        // The DX12 renderer loads dxcompiler.dll only from the running
        // executable's own directory (d3d12.zig, DxcLibrary.load), and the
        // cached test binary has no such neighbour, so the custom-shader
        // tests report compiler_unavailable. Step.Run executes the installed
        // copy once an install has set installed_path, which is std's own
        // answer to DLLs beside an exe on Windows: install the DLL next to
        // every copy the run might pick, and order the run after them.
        // --fuzz reruns the cached binary instead, so shader tests still
        // report compiler_unavailable there.
        const test_target = config.baselineTarget(b.graph.io).result;
        if (test_target.os.tag == .windows) {
            if (dxcTestDll(b, test_target.cpu.arch, dxc_dll)) |dll| {
                // An input of the run, so a new DXC pin reruns the tests
                // instead of replaying a result cached against the old DLL.
                for (test_runs) |test_run| {
                    test_run.addFileInput(dll);
                    test_run.step.dependOn(test_exe_install_step);
                    test_run.step.dependOn(&b.addInstallFileWithDir(
                        dll,
                        .{ .custom = test_binaries_dir },
                        "dxcompiler.dll",
                    ).step);
                    if (test_exe_bin_install) |install| {
                        test_run.step.dependOn(install);
                        test_run.step.dependOn(&b.addInstallFileWithDir(
                            dll,
                            .bin,
                            "dxcompiler.dll",
                        ).step);
                    }
                }
            }
        }

        // Normal tests always test our libghostty modules
        //test_step.dependOn(test_lib_vt_step);

        // Valgrind test running. These are test runner steps like the
        // normal runs above, with valgrind in front of the binary instead
        // of the dynamic linker. A plain command step whose stdio goes to
        // the terminal holds a global lock while it runs, so the build
        // runner would run the shards one after another and sharding
        // would gain nothing. As test runner steps the shards run at the
        // same time, a failing test is reported by name, and valgrind's
        // report on stderr is printed when each shard finishes rather
        // than live. Valgrind's own errors exit with code 1 and fail the
        // shard.
        //
        // patchelf rewrites the binary in place, so it runs once, before
        // any shard starts. One patchelf step per shard would rewrite the
        // file under the shards already running, and they die with
        // SIGBUS: valgrind maps the file itself, so nothing stops the
        // write.
        const valgrind_patchelf = config.addPatchElf(valgrind_exe, test_valgrind_step);
        for (0..test_shards) |shard| {
            const valgrind_run = b.addSystemCommand(&.{
                "valgrind",
                "--leak-check=full",
                "--error-exitcode=1",
                "--num-callers=50",
                "--gen-suppressions=all",
            });
            valgrind_run.producer = valgrind_exe;
            valgrind_run.addFileArg2(b.path("valgrind.supp"), .{ .prefix = "--suppressions=" });
            valgrind_run.addArtifactArg(valgrind_exe);
            valgrind_run.enableTestRunnerMode();
            if (test_shards > 1) {
                valgrind_run.addArg(b.fmt("--shard={d}/{d}", .{ shard, test_shards }));
            }
            if (valgrind_patchelf) |patchelf| valgrind_run.step.dependOn(&patchelf.step);
            test_valgrind_step.dependOn(&valgrind_run.step);
        }
    }

    // After the last installTestBinary above: this freezes the manifest.
    installTestBinaryRoots(b, test_binaries_step, &test_binary_roots);

    // update-translations does what it sounds like and updates the "pot"
    // files. These should be committed to the repo.
    if (i18n) |v| {
        translations_step.dependOn(v.update_step);
    } else {
        _ = b.addFail("cannot update translations when i18n is disabled");
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

/// installTestBinary for a binary whose root is generated (addWriteFiles)
/// or otherwise not a plain source-tree path: the root is named explicitly
/// so the reachability check can still qualify the binary's test names.
fn installTestBinaryAt(
    b: *std.Build,
    step: *std.Build.Step,
    roots: *std.ArrayList(u8),
    compile: *std.Build.Step.Compile,
    comptime root: []const u8,
) void {
    const dir: std.Build.Step.InstallArtifact.Options.Dir = .{
        .override = .{ .custom = test_binaries_dir },
    };
    const install = b.addInstallArtifact(compile, .{
        .dest_dir = dir,
        .pdb_dir = dir,
    });
    step.dependOn(&install.step);
    roots.appendSlice(
        b.allocator,
        b.fmt("{s}\t{s}\n", .{ compile.name, root }),
    ) catch @panic("OOM");
}

/// The dxcompiler.dll the Windows test run places beside its binary:
/// -Ddxc-dll when given, otherwise the copy the C# restore left in the
/// NuGet packages folder at the version windows/Ghostty/Ghostty.csproj
/// pins, read from there so the app and the tests cannot drift apart.
/// Null when neither exists, and the shader tests then fail with
/// compiler_unavailable exactly as they would without this.
fn dxcTestDll(
    b: *std.Build,
    arch: std.Target.Cpu.Arch,
    override: ?[]const u8,
) ?std.Build.LazyPath {
    if (override) |path| return .{ .cwd_relative = path };

    const arch_dir = switch (arch) {
        .x86_64 => "x64",
        .aarch64 => "arm64",
        .x86 => "x86",
        else => return null,
    };

    const csproj = b.root.root_dir.handle.readFileAlloc(
        b.graph.io,
        "windows/Ghostty/Ghostty.csproj",
        b.allocator,
        .limited(1 << 20),
    ) catch return null;
    const at = std.mem.indexOf(u8, csproj, "Include=\"Microsoft.Direct3D.DXC\"") orelse
        return null;
    const key = "Version=\"";
    const start = (std.mem.indexOfPos(u8, csproj, at, key) orelse return null) + key.len;
    const end = std.mem.indexOfScalarPos(u8, csproj, start, '"') orelse return null;
    // The Version must belong to this PackageReference, not a later element.
    const close = std.mem.indexOfScalarPos(u8, csproj, at, '>') orelse return null;
    if (end > close) return null;

    const env = &b.graph.environ_map;
    const packages = env.get("NUGET_PACKAGES") orelse
        b.pathJoin(&.{ env.get("USERPROFILE") orelse return null, ".nuget", "packages" });
    const dll = b.pathJoin(&.{
        packages, "microsoft.direct3d.dxc", csproj[start..end],
        "build",  "native",                 "bin",
        arch_dir, "dxcompiler.dll",
    });
    std.Io.Dir.accessAbsolute(b.graph.io, dll, .{}) catch return null;
    return .{ .cwd_relative = dll };
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

/// Makes `step` run the tests in `test_exe` as `shards` processes at
/// once. Each process gets a `--shard=I/N` argument and runs only its
/// share of the tests (see src/build/test_runner.zig). Nothing orders the
/// run steps, so the build runner starts them in parallel, and it reports
/// each failing test by name as usual.
///
/// For example, with `shards` set to 4 the build runs:
///
///     ghostty-test --shard=0/4
///     ghostty-test --shard=1/4
///     ghostty-test --shard=2/4
///     ghostty-test --shard=3/4
///
/// With `shards` set to 1 there is no `--shard` argument, so the single
/// process behaves exactly like Zig's default test runner. In the Nix dev
/// shell each command starts with the shell's dynamic linker (see
/// Config.addLoaderArgs).
fn addShardedTestRuns(
    b: *std.Build,
    config: *const buildpkg.Config,
    test_exe: *std.Build.Step.Compile,
    step: *std.Build.Step,
    shards: u32,
) []*std.Build.Step.Run {
    const runs = b.allocator.alloc(*std.Build.Step.Run, shards) catch @panic("OOM");
    for (0..shards) |shard| {
        // Like b.addRunArtifact, with the loader in front of the binary.
        const run = std.Build.Step.Run.create(b, b.fmt("run test {s}", .{test_exe.name}));
        run.producer = test_exe;
        config.addLoaderArgs(run);
        run.addArtifactArg(test_exe);
        run.enableTestRunnerMode();
        if (shards > 1) {
            run.addArg(b.fmt("--shard={d}/{d}", .{ shard, shards }));
        }
        step.dependOn(&run.step);
        runs[shard] = run;
    }
    return runs;
}

/// Returns the trimmed contents of `name` in the build root, or null if
/// it doesn't exist.
fn readVersionFile(b: *std.Build, name: []const u8) ?[]const u8 {
    const content = b.root.root_dir.handle.readFileAlloc(
        b.graph.io,
        name,
        b.allocator,
        .limited(128),
    ) catch return null;
    return std.mem.trim(u8, content, &std.ascii.whitespace);
}
