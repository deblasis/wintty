const std = @import("std");
const com = @import("com.zig");
const dxgi = @import("dxgi.zig");
const GUID = com.GUID;
const HRESULT = com.HRESULT;
const IUnknown = com.IUnknown;
const Reserved = com.Reserved;
const HWND = dxgi.HWND;

pub const IDCompositionDevice = extern struct {
    vtable: *const VTable,

    pub const IID = GUID{
        .data1 = 0xc37ea93a,
        .data2 = 0xe7aa,
        .data3 = 0x450d,
        .data4 = .{ 0xb1, 0x6f, 0x97, 0x46, 0xcb, 0x04, 0x07, 0xf3 },
    };

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: *const fn (*IDCompositionDevice, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDCompositionDevice) callconv(.winapi) u32,
        Release: *const fn (*IDCompositionDevice) callconv(.winapi) u32,
        Commit: *const fn (*IDCompositionDevice) callconv(.winapi) HRESULT,
        WaitForCommitCompletion: Reserved,
        GetFrameStatistics: Reserved,
        CreateTargetForHwnd: *const fn (*IDCompositionDevice, hwnd: HWND, topmost: i32, target: *?*IDCompositionTarget) callconv(.winapi) HRESULT,
        CreateVisual: *const fn (*IDCompositionDevice, visual: *?*IDCompositionVisual) callconv(.winapi) HRESULT,
    };

    pub inline fn Commit(self: *IDCompositionDevice) HRESULT {
        return self.vtable.Commit(self);
    }

    pub inline fn CreateTargetForHwnd(self: *IDCompositionDevice, hwnd: HWND, topmost: i32, target: *?*IDCompositionTarget) HRESULT {
        return self.vtable.CreateTargetForHwnd(self, hwnd, topmost, target);
    }

    pub inline fn CreateVisual(self: *IDCompositionDevice, visual: *?*IDCompositionVisual) HRESULT {
        return self.vtable.CreateVisual(self, visual);
    }

    pub inline fn Release(self: *IDCompositionDevice) u32 {
        return self.vtable.Release(self);
    }
};

pub const IDCompositionTarget = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: *const fn (*IDCompositionTarget, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDCompositionTarget) callconv(.winapi) u32,
        Release: *const fn (*IDCompositionTarget) callconv(.winapi) u32,
        SetRoot: *const fn (*IDCompositionTarget, visual: ?*IDCompositionVisual) callconv(.winapi) HRESULT,
    };

    pub inline fn SetRoot(self: *IDCompositionTarget, visual: ?*IDCompositionVisual) HRESULT {
        return self.vtable.SetRoot(self, visual);
    }

    pub inline fn Release(self: *IDCompositionTarget) u32 {
        return self.vtable.Release(self);
    }
};

pub const IDCompositionVisual = extern struct {
    vtable: *const VTable,

    pub const VTable = extern struct {
        // IUnknown
        QueryInterface: *const fn (*IDCompositionVisual, *const GUID, *?*anyopaque) callconv(.winapi) HRESULT,
        AddRef: *const fn (*IDCompositionVisual) callconv(.winapi) u32,
        Release: *const fn (*IDCompositionVisual) callconv(.winapi) u32,
        // Reserved slots before SetContent.
        SetOffsetX_float: Reserved,
        SetOffsetX_animation: Reserved,
        SetOffsetY_float: Reserved,
        SetOffsetY_animation: Reserved,
        SetTransform_matrix: Reserved,
        SetTransform_transform: Reserved,
        SetTransformParent: Reserved,
        SetEffect: Reserved,
        SetBitmapInterpolationMode: Reserved,
        SetBorderMode: Reserved,
        SetClip_rect: Reserved,
        SetClip_clip: Reserved,
        SetContent: *const fn (*IDCompositionVisual, content: ?*IUnknown) callconv(.winapi) HRESULT,
        // Reserved slots after SetContent.
        AddVisual: Reserved,
        RemoveVisual: Reserved,
        RemoveAllVisuals: Reserved,
    };

    pub inline fn SetContent(self: *IDCompositionVisual, content: ?*IUnknown) HRESULT {
        return self.vtable.SetContent(self, content);
    }

    pub inline fn Release(self: *IDCompositionVisual) u32 {
        return self.vtable.Release(self);
    }
};

pub extern "dcomp" fn DCompositionCreateDevice(
    dxgiDevice: ?*IUnknown,
    iid: *const GUID,
    dcompositionDevice: *?*anyopaque,
) callconv(.winapi) HRESULT;
