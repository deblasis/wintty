//! 0.17 compatibility helpers.
//!
//! `std.lang.Type` became struct-of-arrays in 0.17: `@typeInfo(T).@"struct".fields`
//! and `std.meta.fields`/`fieldInfo`/`fieldNames` are gone (the latter are hard
//! `@compileError`s). These helpers reconstruct the old array-of-fields shape at
//! comptime so the fork's own code keeps reading `.name` / `.type` /
//! `.value` / `.default_value_ptr`. Upstream code uses the struct-of-arrays
//! form directly; this is only for fork-only files.

const std = @import("std");

/// A field descriptor with the pre-0.17 `std.meta.FieldInfo` shape.
pub const Field = struct {
    name: [:0]const u8,
    type: type,
    default_value_ptr: ?*const anyopaque = null,
    alignment: ?usize = null,
    is_comptime: bool = false,
};

pub fn StructField(comptime T: type) type {
    _ = T;
    return Field;
}

pub fn structFields(comptime T: type) []const StructField(T) {
    @setEvalBranchQuota(200_000);
    const info = @typeInfo(T).@"struct";
    comptime var buf: [info.field_names.len]StructField(T) = undefined;
    inline for (info.field_names, info.field_types, info.field_attrs, 0..) |name, FieldTy, attrs, i| {
        buf[i] = .{
            .name = name,
            .type = FieldTy,
            // Carried over, not defaulted: a caller asking whether a field
            // has a default (tests that pin "no default, so every
            // construction site must name it") would otherwise pass for
            // every field.
            .default_value_ptr = attrs.default_value_ptr,
            .alignment = attrs.@"align",
            .is_comptime = attrs.@"comptime",
        };
    }
    const result = buf;
    return &result;
}

pub const EnumField = struct {
    name: [:0]const u8,
    value: comptime_int,
};

pub fn enumFields(comptime T: type) []const EnumField {
    @setEvalBranchQuota(200_000);
    const info = @typeInfo(T).@"enum";
    comptime var buf: [info.field_names.len]EnumField = undefined;
    inline for (info.field_names, info.field_values, 0..) |name, value, i| {
        buf[i] = .{ .name = name, .value = value };
    }
    const result = buf;
    return &result;
}

/// Fields of either a struct or a union, for generics that accept both.
pub fn aggregateFields(comptime T: type) []const StructField(T) {
    return switch (@typeInfo(T)) {
        .@"struct" => structFields(T),
        .@"union" => unionFields(T),
        else => @compileError("aggregateFields: " ++ @typeName(T) ++ " is not an aggregate"),
    };
}

/// `std.meta.fieldInfo(T, name).type` replacement for a comptime string name.
pub fn fieldType(comptime T: type, comptime name: []const u8) type {
    return @FieldType(T, name);
}

/// `"abc" ** n` replacement for multi-byte strings (removed in 0.17).
pub fn repeat(comptime s: []const u8, comptime n: usize) [s.len * n]u8 {
    var out: [s.len * n]u8 = undefined;
    for (0..n) |i| @memcpy(out[i * s.len ..][0..s.len], s);
    return out;
}

/// Same array-of-fields shape for unions (0.17 made `Type.Union` SoA too).
pub fn unionFields(comptime T: type) []const StructField(T) {
    @setEvalBranchQuota(200_000);
    if (@typeInfo(T) != .@"union")
        @compileError("unionFields called on non-union " ++ @typeName(T));
    const info = @typeInfo(T).@"union";
    comptime var buf: [info.field_names.len]StructField(T) = undefined;
    inline for (info.field_names, info.field_types, 0..) |name, FieldTy, i| {
        buf[i] = .{
            .name = name,
            .type = FieldTy,
            .default_value_ptr = null,
        };
    }
    const result = buf;
    return &result;
}
