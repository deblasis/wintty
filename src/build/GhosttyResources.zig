const GhosttyResources = @This();

const std = @import("std");
const assert = std.debug.assert;
const Config = @import("Config.zig");
const samePath = @import("install_paths.zig").samePath;
const terminfo_paths = @import("../terminfo/paths.zig");
const RunStep = std.Build.Step.Run;
const SharedDeps = @import("SharedDeps.zig");

steps: []*std.Build.Step,

/// The resources a Windows build installs, as individual steps.
///
/// Windows builds a library rather than an executable, so `install()` (which
/// only the executable build depends on) never runs there and build.zig has
/// to depend on the shippable steps one by one. What belongs here is every
/// resource that is just files: the terminfo source, the shell-integration
/// scripts and the themes. What does not is everything produced by shelling
/// out to `tic`, `infotocap`, `mkdir -p` or `cp -R`, none of which a Windows
/// build host has.
///
/// The three are not independent. The terminfo source is what
/// `os/resourcesdir.zig` detects the tree by, so it gates the other two:
/// without it the scripts and the themes are installed into a tree nothing
/// ever looks in. `init` refuses a Windows target that would drop it rather
/// than leave that to be noticed at runtime.
///
/// Only ever populated for a Windows target. The same steps exist for other
/// targets and install elsewhere (FreeBSD puts terminfo under
/// `site-terminfo`), so collecting them under this name would be a trap for
/// whoever reads it next.
windows: []*std.Build.Step,

pub fn init(b: *std.Build, cfg: *const Config, deps: *const SharedDeps) !GhosttyResources {
    var steps: std.ArrayList(*std.Build.Step) = .empty;
    errdefer steps.deinit(b.allocator);
    var windows_steps: std.ArrayList(*std.Build.Step) = .empty;
    errdefer windows_steps.deinit(b.allocator);

    // This is the exe used to generate some build data.
    const build_data_exe = b.addExecutable(.{
        .name = "ghostty-build-data",
        .root_module = b.createModule(.{
            .root_source_file = b.path("src/main_build_data.zig"),
            .target = b.graph.host,
            .strip = false,
            .omit_frame_pointer = false,
            .unwind_tables = .sync,
            .link_libc = true,
        }),
    });

    deps.help_strings.addImport(build_data_exe);

    const os_tag = cfg.target.result.os.tag;

    // Terminfo
    terminfo: {
        const terminfo_share_dir = if (os_tag == .freebsd)
            terminfo_paths.freebsd_db_dir
        else
            terminfo_paths.db_dir;

        // Encode our terminfo
        const run = b.addRunArtifact(build_data_exe);
        run.addArg("+terminfo");
        const wf = b.addWriteFiles();
        const source = wf.addCopyFile(run.captureStdOut(.{}), "ghostty.terminfo");

        // On Windows this file is not just one more resource: it is the only
        // thing `os/resourcesdir.zig` detects the tree by, so without it the
        // shell-integration scripts and the themes install into a tree
        // libghostty never finds. That is the exact bug this tree was shipped
        // to fix, and it would come back with no signal but a per-spawn log
        // line. `-Demit-terminfo=false` is user settable and only defaults
        // true here, so refuse it rather than let it re-land the bug quietly.
        if (os_tag == .windows and !cfg.emit_terminfo) {
            std.log.err(
                "-Demit-terminfo=false cannot be used for a Windows target: " ++
                    "share/terminfo/ghostty.terminfo is how the resources " ++
                    "directory is found, and without it the shell-integration " ++
                    "scripts and themes are installed but never loaded.",
                .{},
            );
            return error.WindowsRequiresTerminfo;
        }

        if (cfg.emit_terminfo) {
            const source_install = b.addInstallFile(
                source,
                if (os_tag == .freebsd)
                    "share/" ++ terminfo_paths.freebsd_db_dir ++ "/" ++ terminfo_paths.source_file
                else
                    "share/" ++ terminfo_paths.db_dir ++ "/" ++ terminfo_paths.source_file,
            );

            try steps.append(b.allocator, &source_install.step);
            // Only the Windows target's copy of this step belongs in the
            // Windows set; on other targets the same step installs elsewhere
            // (FreeBSD puts it under site-terminfo) and nothing would read it.
            if (os_tag == .windows) {
                try windows_steps.append(b.allocator, &source_install.step);
            }
        }

        // Windows doesn't have the binaries below.
        if (os_tag == .windows) break :terminfo;

        // Convert to termcap source format if thats helpful to people and
        // install it. The resulting value here is the termcap source in case
        // that is used for other commands.
        if (cfg.emit_termcap) {
            const run_step = RunStep.create(b, "infotocap");
            run_step.addArg("infotocap");
            run_step.addFileArg(source);
            const out_source = run_step.captureStdOut(.{});
            _ = run_step.captureStdErr(.{}); // so we don't see stderr

            const cap_install = b.addInstallFile(
                out_source,
                if (os_tag == .freebsd)
                    "share/" ++ terminfo_paths.freebsd_db_dir ++ "/" ++ terminfo_paths.preferred_name ++ ".termcap"
                else
                    "share/" ++ terminfo_paths.db_dir ++ "/" ++ terminfo_paths.preferred_name ++ ".termcap",
            );

            try steps.append(b.allocator, &cap_install.step);
        }

        // Compile the terminfo source into a terminfo database
        {
            const run_step = RunStep.create(b, "tic");
            run_step.addArgs(&.{ "tic", "-x", "-o" });
            const path = run_step.addOutputFileArg(terminfo_share_dir);

            run_step.addFileArg(source);
            _ = run_step.captureStdErr(.{}); // so we don't see stderr

            const install_step = b.addInstallDirectory(.{
                .source_dir = path,
                .install_dir = .{ .custom = "share" },
                .install_subdir = terminfo_share_dir,
            });
            try steps.append(b.allocator, &install_step.step);
        }
    }

    // Shell-integration
    {
        const install_step = b.addInstallDirectory(.{
            .source_dir = b.path("src/shell-integration"),
            .install_dir = .{ .custom = "share" },
            .install_subdir = b.pathJoin(&.{ "ghostty", "shell-integration" }),
            .exclude_extensions = &.{".md"},
        });
        try steps.append(b.allocator, &install_step.step);
        if (os_tag == .windows) {
            try windows_steps.append(b.allocator, &install_step.step);
        }
    }

    // Themes
    if (cfg.emit_themes) {
        if (b.lazyDependency("iterm2_themes", .{})) |upstream| {
            const install_step = b.addInstallDirectory(.{
                .source_dir = upstream.path(""),
                .install_dir = .{ .custom = "share" },
                .install_subdir = b.pathJoin(&.{ "ghostty", "themes" }),
                .exclude_extensions = &.{".md"},
            });
            try steps.append(b.allocator, &install_step.step);
            if (os_tag == .windows) {
                try windows_steps.append(b.allocator, &install_step.step);
            }
        }
    }

    // Fish shell completions
    {
        const run = b.addRunArtifact(build_data_exe);
        run.addArg("+fish");
        const wf = b.addWriteFiles();
        _ = wf.addCopyFile(run.captureStdOut(.{}), "ghostty.fish");

        const install_step = b.addInstallDirectory(.{
            .source_dir = wf.getDirectory(),
            .install_dir = .prefix,
            .install_subdir = "share/fish/vendor_completions.d",
        });
        try steps.append(b.allocator, &install_step.step);
    }

    // zsh shell completions
    {
        const run = b.addRunArtifact(build_data_exe);
        run.addArg("+zsh");
        const wf = b.addWriteFiles();
        _ = wf.addCopyFile(run.captureStdOut(.{}), "_ghostty");

        const install_step = b.addInstallDirectory(.{
            .source_dir = wf.getDirectory(),
            .install_dir = .prefix,
            .install_subdir = "share/zsh/site-functions",
        });
        try steps.append(b.allocator, &install_step.step);
    }

    // bash shell completions
    {
        const run = b.addRunArtifact(build_data_exe);
        run.addArg("+bash");
        const wf = b.addWriteFiles();
        _ = wf.addCopyFile(run.captureStdOut(.{}), "ghostty.bash");

        const install_step = b.addInstallDirectory(.{
            .source_dir = wf.getDirectory(),
            .install_dir = .prefix,
            .install_subdir = "share/bash-completion/completions",
        });
        try steps.append(b.allocator, &install_step.step);
    }

    // Vim and Neovim plugin
    {
        const wf = b.addWriteFiles();

        {
            const run = b.addRunArtifact(build_data_exe);
            run.addArg("+vim-syntax");
            _ = wf.addCopyFile(run.captureStdOut(.{}), "syntax/ghostty.vim");
        }
        {
            const run = b.addRunArtifact(build_data_exe);
            run.addArg("+vim-ftdetect");
            _ = wf.addCopyFile(run.captureStdOut(.{}), "ftdetect/ghostty.vim");
        }
        {
            const run = b.addRunArtifact(build_data_exe);
            run.addArg("+vim-ftplugin");
            _ = wf.addCopyFile(run.captureStdOut(.{}), "ftplugin/ghostty.vim");
        }
        {
            const run = b.addRunArtifact(build_data_exe);
            run.addArg("+vim-compiler");
            _ = wf.addCopyFile(run.captureStdOut(.{}), "compiler/ghostty.vim");
        }

        const vim_step = b.addInstallDirectory(.{
            .source_dir = wf.getDirectory(),
            .install_dir = .prefix,
            .install_subdir = "share/vim/vimfiles",
        });
        try steps.append(b.allocator, &vim_step.step);

        const neovim_step = b.addInstallDirectory(.{
            .source_dir = wf.getDirectory(),
            .install_dir = .prefix,
            .install_subdir = "share/nvim/site",
        });
        try steps.append(b.allocator, &neovim_step.step);
    }

    // Sublime syntax highlighting for bat cli tool
    // NOTE: The current implementation requires symlinking the generated
    // 'ghostty.sublime-syntax' file from zig-out to the '~.config/bat/syntaxes'
    // directory. The syntax then needs to be mapped to the correct language in
    // the config file within the '~.config/bat' directory
    // (ex: --map-syntax "/Users/user/.config/ghostty/config.ghostty:Ghostty Config").
    {
        const run = b.addRunArtifact(build_data_exe);
        run.addArg("+sublime");
        const wf = b.addWriteFiles();
        _ = wf.addCopyFile(run.captureStdOut(.{}), "ghostty.sublime-syntax");

        const install_step = b.addInstallDirectory(.{
            .source_dir = wf.getDirectory(),
            .install_dir = .prefix,
            .install_subdir = "share/bat/syntaxes",
        });
        try steps.append(b.allocator, &install_step.step);
    }

    // App (Linux)
    if (cfg.target.result.os.tag == .linux) try addLinuxAppResources(
        b,
        cfg,
        &steps,
    );

    return .{ .steps = steps.items, .windows = windows_steps.items };
}

/// Add the resource files needed to make Ghostty a proper
/// Linux desktop application (for various desktop environments).
fn addLinuxAppResources(
    b: *std.Build,
    cfg: *const Config,
    steps: *std.ArrayList(*std.Build.Step),
) !void {
    assert(cfg.target.result.os.tag == .linux);

    // Background:
    // https://developer.gnome.org/documentation/guidelines/maintainer/integrating.html

    const name = b.fmt("Ghostty{s}", .{
        switch (cfg.optimize) {
            .debug, .safe => " (Debug)",
            .fast, .small => "",
        },
    });

    const app_id = b.fmt("com.mitchellh.ghostty{s}", .{
        switch (cfg.optimize) {
            .debug, .safe => "-debug",
            .fast, .small => "",
        },
    });

    // How the files below launch Ghostty. See -Dinstalled-exe-path.
    const exe = cfg.installed_exe_path orelse "ghostty";

    // Variables used by the templates.
    // Fields are nullable since unused fields trigger a build failure.
    const Variables = struct {
        NAME: ?[]const u8 = null,
        APPID: ?[]const u8 = null,
        GHOSTTY: ?[]const u8 = null,
    };

    // The templates that we will process. The templates are in
    // cmake format and will be processed and saved to the
    // second element of the tuple.
    const Template = struct { std.Build.LazyPath, []const u8, Variables };
    const templates: []const Template = templates: {
        var ts: std.ArrayList(Template) = .empty;
        defer ts.deinit(b.allocator);

        // Desktop file so that we have an icon and other metadata
        try ts.append(b.allocator, .{
            b.path("dist/linux/app.desktop.in"),
            b.fmt("share/applications/{s}.desktop", .{app_id}),
            .{ .NAME = name, .APPID = app_id, .GHOSTTY = exe },
        });

        // Service for DBus activation.
        try ts.append(b.allocator, .{
            if (cfg.flatpak)
                b.path("dist/linux/dbus.service.flatpak.in")
            else
                b.path("dist/linux/dbus.service.in"),
            b.fmt("share/dbus-1/services/{s}.service", .{app_id}),
            .{ .APPID = app_id, .GHOSTTY = exe },
        });

        // `systemd` user service. This is kind of nasty but `systemd` looks for
        // user services in different paths depending on if we are installed
        // as a system package or not (lib vs. share) so we have to handle that
        // here. We might be able to get away with always installing to both
        // because it only ever searches in one... but I don't want to do that
        // hack until we have to.
        //
        // The XDG Freedesktop Portal has a major undocumented requirement
        // for programs that are launched/controlled by `systemd` to interact
        // with the Portal. The unit must be named `app-<appid>.service`. The
        // Portal uses the `systemd` unit name figure out what the program's
        // application ID is and it will only look at unit names that begin with
        // `app-`. I can find no place that this is documented other than by
        // inspecting the code or the issue and PR that introduced this feature.
        // See the following code:
        //
        // https://github.com/flatpak/xdg-desktop-portal/blob/7d4d48cf079147c8887da17ec6c3954acd5a285c/src/xdp-utils.c#L152-L220
        if (!cfg.flatpak) try ts.append(b.allocator, .{
            b.path("dist/linux/systemd.service.in"),
            b.fmt(
                "{s}/systemd/user/app-{s}.service",
                .{
                    if (b.graph.system_package_mode) "lib" else "share",
                    app_id,
                },
            ),
            .{ .NAME = name, .APPID = app_id, .GHOSTTY = exe },
        });

        // AppStream metainfo so that application has rich metadata
        // within app stores
        try ts.append(b.allocator, .{
            b.path("dist/linux/com.mitchellh.ghostty.metainfo.xml.in"),
            b.fmt("share/metainfo/{s}.metainfo.xml", .{app_id}),
            .{ .NAME = name, .APPID = app_id },
        });

        break :templates try ts.toOwnedSlice(b.allocator);
    };

    // Process all our templates
    for (templates) |template| {
        const tpl = b.addConfigHeader(
            .{ .style = .{ .cmake = template[0] } },
            .{},
        );
        inline for (@typeInfo(Variables).@"struct".field_names) |field| {
            if (@field(template[2], field)) |v| {
                tpl.addValue(field, @FieldType(Variables, field), v);
            }
        }

        // Template output has a single header line we want to remove.
        // We use `tail` to do it since its part of the POSIX standard.
        const tail = b.addSystemCommand(&.{ "tail", "-n", "+2" });
        tail.setStdIn(.{ .lazy_path = tpl.getOutputFile() });

        const copy = b.addInstallFile(
            tail.captureStdOut(.{}),
            template[1],
        );

        try steps.append(b.allocator, &copy.step);
    }

    // Right click menu action for Plasma desktop
    try steps.append(b.allocator, &b.addInstallFile(
        b.path("dist/linux/ghostty_dolphin.desktop"),
        "share/kio/servicemenus/com.mitchellh.ghostty.desktop",
    ).step);

    // Right click menu action for Nautilus. Note that this _must_ be named
    // `ghostty.py`. Using the full app id causes problems (see #5468).
    try steps.append(b.allocator, &b.addInstallFile(
        b.path("dist/linux/ghostty_nautilus.py"),
        "share/nautilus-python/extensions/ghostty.py",
    ).step);

    // Various icons that our application can use, including the icon
    // that will be used for the desktop.
    try steps.append(b.allocator, &b.addInstallFile(
        b.path("images/gnome/16.png"),
        "share/icons/hicolor/16x16/apps/com.mitchellh.ghostty.png",
    ).step);
    try steps.append(b.allocator, &b.addInstallFile(
        b.path("images/gnome/32.png"),
        "share/icons/hicolor/32x32/apps/com.mitchellh.ghostty.png",
    ).step);
    try steps.append(b.allocator, &b.addInstallFile(
        b.path("images/gnome/128.png"),
        "share/icons/hicolor/128x128/apps/com.mitchellh.ghostty.png",
    ).step);
    try steps.append(b.allocator, &b.addInstallFile(
        b.path("images/gnome/256.png"),
        "share/icons/hicolor/256x256/apps/com.mitchellh.ghostty.png",
    ).step);
    try steps.append(b.allocator, &b.addInstallFile(
        b.path("images/gnome/512.png"),
        "share/icons/hicolor/512x512/apps/com.mitchellh.ghostty.png",
    ).step);
    // Flatpaks only support icons up to 512x512.
    if (!cfg.flatpak) {
        try steps.append(b.allocator, &b.addInstallFile(
            b.path("images/gnome/1024.png"),
            "share/icons/hicolor/1024x1024/apps/com.mitchellh.ghostty.png",
        ).step);
    }

    try steps.append(b.allocator, &b.addInstallFile(
        b.path("images/gnome/32.png"),
        "share/icons/hicolor/16x16@2/apps/com.mitchellh.ghostty.png",
    ).step);
    try steps.append(b.allocator, &b.addInstallFile(
        b.path("images/gnome/64.png"),
        "share/icons/hicolor/32x32@2/apps/com.mitchellh.ghostty.png",
    ).step);
    try steps.append(b.allocator, &b.addInstallFile(
        b.path("images/gnome/256.png"),
        "share/icons/hicolor/128x128@2/apps/com.mitchellh.ghostty.png",
    ).step);
    try steps.append(b.allocator, &b.addInstallFile(
        b.path("images/gnome/512.png"),
        "share/icons/hicolor/256x256@2/apps/com.mitchellh.ghostty.png",
    ).step);
}

/// The install paths a Windows build has to produce, each one the whole path
/// under the install prefix and not the tail of one.
///
/// `share/terminfo/ghostty.terminfo` is the sentinel `os/resourcesdir.zig`
/// finds the tree by, and `share/ghostty/shell-integration` is what
/// `termio/shell_integration.zig` opens per shell. The themes are deliberately
/// not required: `-Demit-themes=false` is a supported build and costs only the
/// bundled colour schemes.
///
/// Writing the whole path is the correction to a guard that could not see the
/// half it mattered most for. A step's own idea of where it goes is split in
/// two (`install_dir` plus a path relative to it), and the shell-integration
/// step carries `share` in the first half. Checking only the second half meant
/// moving the scripts out of `share` left this list matching, so the build
/// stayed green while shipping an app with a findable tree and no scripts in
/// it. `installPath` now composes both halves, and these are composed the same
/// way before comparison, so the two sides cannot drift apart again.
///
/// The terminfo entry is `share/` plus the sentinel `os/resourcesdir.zig`
/// detects the tree by, spelled in `terminfo/paths.zig` from the terminfo
/// table's own names. The comptime block below refuses a literal here that
/// stops matching it.
const windows_required = [_][]const u8{
    "share/terminfo/ghostty.terminfo",
    "share/ghostty/shell-integration",
};

comptime {
    if (!std.mem.eql(
        u8,
        windows_required[0],
        "share/" ++ terminfo_paths.windows_sentinel,
    )) {
        @compileError("windows_required[0] must be share/ plus terminfo/paths.zig's " ++
            "windows_sentinel: it is the install path of the file " ++
            "os/resourcesdir.zig detects the Windows resources tree by");
    }
}

/// Fail the configure step unless every resource a Windows build has to ship
/// is both produced and attached to the install step.
///
/// This exists because neither half was covered by anything. The install
/// wiring could be deleted from build.zig, or a member dropped from
/// `windows`, and the whole suite stayed green while the shipped app went
/// back to having no resources directory: shell integration off, TERM
/// generic, and nothing but a per-spawn log line to say so. A unit test
/// cannot reach this, since it needs a real `std.Build`, so the check runs at
/// configure time on every Windows build instead, where it cannot be skipped.
pub fn assertWindowsInstall(self: *const GhosttyResources, b: *std.Build) !void {
    const install_step = b.getInstallStep();

    for (windows_required) |required| {
        // Composed the same way the steps are, so the comparison is between
        // two whole prefix-relative locations rather than between two path
        // fragments whose bases might differ.
        const want = installPath(b, .prefix, required);

        var wired = false;
        for (self.windows) |step| {
            const path = stepInstallPath(b, step) orelse continue;
            if (!samePath(path, want)) continue;

            // Present in the set is not enough: it has to be depended on, or
            // the step is built and never run.
            for (install_step.dependencies.items) |dep| {
                if (dep == step) {
                    wired = true;
                    break;
                }
            }
            break;
        }

        if (!wired) {
            std.log.err(
                "a Windows build must install {s}, and it is either not " ++
                    "produced (see GhosttyResources.init) or not attached to " ++
                    "the install step (see the Windows branch of build.zig). " ++
                    "Without it the app ships a resources tree it cannot find.",
                .{required},
            );
            return error.WindowsResourcesNotInstalled;
        }
    }
}

/// Where a step installs to, relative to the install prefix, for the two
/// step kinds the Windows set is made of. Null for anything else.
///
/// Both kinds hold their destination in two parts: an `InstallDir` naming a
/// base (`.prefix`, `.lib`, `.bin`, `.header` or a `.custom` subdirectory of
/// the prefix) and a path relative to that base. Reading only the second part
/// is what made the guard blind: the shell-integration step keeps `share` in
/// its base, so `ghostty/shell-integration` matched no matter which base it
/// was under.
fn stepInstallPath(b: *std.Build, step: *std.Build.Step) ?[]const u8 {
    if (step.cast(std.Build.Step.InstallFile)) |s|
        return installPath(b, s.dir, s.dest_rel_path);
    if (step.cast(std.Build.Step.InstallDir)) |s|
        return installPath(b, s.options.install_dir, s.options.install_subdir);
    return null;
}

/// An install location as a path under the prefix. Zig 0.17 resolves the
/// prefix itself only at make time, so the configure-time guard compares
/// prefix-relative paths; both sides go through here, so the bases are
/// spelled out the same way. The lib, bin and include names are the build
/// system's defaults, which the Windows build does not override.
fn installPath(b: *std.Build, dir: std.Build.InstallDir, sub_path: []const u8) []const u8 {
    const base: []const u8 = switch (dir) {
        .prefix => "",
        .lib => "lib",
        .bin => "bin",
        .header => "include",
        .custom => |c| c,
    };
    if (base.len == 0) return sub_path;
    if (sub_path.len == 0) return base;
    return b.pathJoin(&.{ base, sub_path });
}

pub fn install(self: *const GhosttyResources) void {
    const b = self.steps[0].owner;
    self.addStepDependencies(b.getInstallStep());
}

pub fn addStepDependencies(
    self: *const GhosttyResources,
    other_step: *std.Build.Step,
) void {
    for (self.steps) |step| other_step.dependOn(step);
}
