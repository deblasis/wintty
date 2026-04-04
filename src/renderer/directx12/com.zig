const windows_com = @import("../../os/windows_com.zig");

// Re-export shared COM primitives so all existing imports are unchanged.
pub const GUID = windows_com.GUID;
pub const HRESULT = windows_com.HRESULT;
pub const SUCCEEDED = windows_com.SUCCEEDED;
pub const FAILED = windows_com.FAILED;
pub const S_OK = windows_com.S_OK;
pub const E_NOINTERFACE = windows_com.E_NOINTERFACE;
pub const E_FAIL = windows_com.E_FAIL;
pub const IUnknown = windows_com.IUnknown;
pub const Reserved = windows_com.Reserved;

// DXGI error code used for device-lost / TDR recovery.
pub const DXGI_ERROR_DEVICE_REMOVED: HRESULT = @bitCast(@as(u32, 0x887A0005));
pub const DXGI_ERROR_DEVICE_HUNG: HRESULT = @bitCast(@as(u32, 0x887A0006));
pub const DXGI_ERROR_DEVICE_RESET: HRESULT = @bitCast(@as(u32, 0x887A0007));

test {
    _ = @import("com_test.zig");
    _ = @import("d3d12.zig");
}
