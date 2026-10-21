//! 0.17 compatibility helpers.
//!
//! `std.lang.Type` became struct-of-arrays in 0.17: `@typeInfo(T).@"struct".fields`
//! and `std.meta.fields`/`fieldInfo`/`fieldNames` are gone (the latter are hard
//! `@compileError`s). These helpers reconstruct the old array-of-fields shape at
//! comptime so the rest of the codebase keeps reading `.name` / `.type` /
//! `.value` / `.default_value_ptr`.

const std = @import("std");

pub fn StructField(comptime T: type) type {
    _ = T;
    return struct {
        name: [:0]const u8,
        type: type,
        default_value_ptr: ?*const anyopaque,
    };
}

pub fn structFields(comptime T: type) []const StructField(T) {
    const info = @typeInfo(T).@"struct";
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

pub const EnumField = struct {
    name: [:0]const u8,
    value: comptime_int,
};

pub fn enumFields(comptime T: type) []const EnumField {
    const info = @typeInfo(T).@"enum";
    comptime var buf: [info.field_names.len]EnumField = undefined;
    inline for (info.field_names, info.field_values, 0..) |name, value, i| {
        buf[i] = .{ .name = name, .value = value };
    }
    const result = buf;
    return &result;
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
