//! Names that the shell completion generators special-case, each checked
//! at compile time against the type it names. Without the checks a rename
//! in the Config or in the CLI actions would make the special case
//! silently stop matching; with them it fails the build instead.

const std = @import("std");
const meta_compat = @import("../meta_compat.zig");

const Config = @import("../config/Config.zig");
const Action = @import("../cli.zig").ghostty.Action;

/// The prefix shared by the Config fields that complete from the font
/// list ("font-family" and its per-style variants).
pub const font_family = checkedConfigFieldPrefix("font-family");

/// The Config field that completes from the theme list.
pub const theme = checkedConfigField("theme");

/// The Config field that completes from directory names.
pub const working_directory = checkedConfigField("working-directory");

/// The action option that completes from the filesystem.
pub const config_file = checkedActionOption("config-file");

/// Actions handled by the shells through their top-level flags rather
/// than through their `+action` options, so their options are skipped.
pub const help_action = checkedAction("help");
pub const version_action = checkedAction("version");

/// The action the generated scripts run for the font list.
pub const list_fonts_action = checkedAction("list-fonts");

/// The action the generated scripts run for the theme list.
pub const list_themes_action = checkedAction("list-themes");

fn checkedConfigField(comptime name: []const u8) []const u8 {
    if (@hasField(Config, name)) return name;

    @compileError("Config has no field \"" ++ name ++ "\"; the shell " ++
        "completion generators special-case it by name and must be updated");
}

fn checkedConfigFieldPrefix(comptime prefix: []const u8) []const u8 {
    // The primary field itself must exist: a prefix that still matches
    // only its variants would otherwise pass while the generators
    // complete from a different field set.
    if (!@hasField(Config, prefix)) @compileError(
        "Config has no field \"" ++ prefix ++ "\"; the shell completion " ++
            "generators complete from it and its variants by prefix, and " ++
            "must be updated",
    );
    @setEvalBranchQuota(10000);
    for (meta_compat.structFields(Config)) |field| {
        if (comptime std.mem.startsWith(u8, field.name, prefix)) return prefix;
    }

    @compileError("Config has no field starting with \"" ++ prefix ++ "\"; " ++
        "the shell completion generators special-case it by name and must " ++
        "be updated");
}

fn checkedAction(comptime name: []const u8) []const u8 {
    if (@hasField(Action, name)) return name;

    @compileError("the CLI has no action \"" ++ name ++ "\"; the shell " ++
        "completion generators special-case it by name and must be updated");
}

fn checkedActionOption(comptime name: []const u8) []const u8 {
    @setEvalBranchQuota(10000);
    for (meta_compat.enumFields(Action)) |field| {
        if (@hasField(@field(Action, field.name).options(), name)) return name;
    }

    @compileError("no CLI action has an option \"" ++ name ++ "\"; the " ++
        "shell completion generators special-case it by name and must be " ++
        "updated");
}
