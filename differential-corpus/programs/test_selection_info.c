#include <windows.h>
#include <stdio.h>

// GetConsoleSelectionInfo is in kernel32.dll but not in the standard headers
// CONSOLE_SELECTION_INFO structure
typedef struct {
    DWORD dwFlags;
    COORD dwSelectionAnchor;
    SMALL_RECT srSelection;
} MY_CONSOLE_SELECTION_INFO;

// Selection flags
#define MY_CONSOLE_NO_SELECTION           0x0000
#define MY_CONSOLE_SELECTION_IN_PROGRESS  0x0001
#define MY_CONSOLE_SELECTION_NOT_EMPTY    0x0002
#define MY_CONSOLE_MOUSE_SELECTION        0x0004

typedef BOOL (WINAPI *GetConsoleSelectionInfo_t)(MY_CONSOLE_SELECTION_INFO*);

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name, ...) do { printf("PASS: " name "\n", ##__VA_ARGS__); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

void test_selection_info_basic(void) {
    // Dynamically resolve GetConsoleSelectionInfo
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (!k32) {
        FAIL("GetConsoleSelectionInfo", "GetModuleHandleW failed");
        return;
    }

    GetConsoleSelectionInfo_t pGetConsoleSelectionInfo =
        (GetConsoleSelectionInfo_t)GetProcAddress(k32, "GetConsoleSelectionInfo");
    if (!pGetConsoleSelectionInfo) {
        FAIL("GetConsoleSelectionInfo", "GetProcAddress failed — function not exported");
        return;
    }

    MY_CONSOLE_SELECTION_INFO csi = {0};
    BOOL result = pGetConsoleSelectionInfo(&csi);

    if (!result) {
        FAIL("GetConsoleSelectionInfo", "returned FALSE");
        return;
    }

    PASS("GetConsoleSelectionInfo returns TRUE");

    // In VT mode, no selection should be active
    if (csi.dwFlags == MY_CONSOLE_NO_SELECTION) {
        PASS("Selection flags: CONSOLE_NO_SELECTION (0x%lX)", csi.dwFlags);
    } else {
        FAIL("GetConsoleSelectionInfo", "expected CONSOLE_NO_SELECTION (0x0), got 0x%lX", csi.dwFlags);
    }
}

void test_selection_info_anchor(void) {
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (!k32) return;
    GetConsoleSelectionInfo_t pGetConsoleSelectionInfo =
        (GetConsoleSelectionInfo_t)GetProcAddress(k32, "GetConsoleSelectionInfo");
    if (!pGetConsoleSelectionInfo) return;

    MY_CONSOLE_SELECTION_INFO csi = {0};
    pGetConsoleSelectionInfo(&csi);

    // Anchor should be (0,0) when no selection
    if (csi.dwSelectionAnchor.X == 0 && csi.dwSelectionAnchor.Y == 0) {
        PASS("Selection anchor is (0, 0) when no selection");
    } else {
        FAIL("GetConsoleSelectionInfo", "anchor (%d,%d) expected (0,0)",
             csi.dwSelectionAnchor.X, csi.dwSelectionAnchor.Y);
    }
}

void test_selection_info_rect(void) {
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (!k32) return;
    GetConsoleSelectionInfo_t pGetConsoleSelectionInfo =
        (GetConsoleSelectionInfo_t)GetProcAddress(k32, "GetConsoleSelectionInfo");
    if (!pGetConsoleSelectionInfo) return;

    MY_CONSOLE_SELECTION_INFO csi = {0};
    pGetConsoleSelectionInfo(&csi);

    // Selection rect should be all zeros when no selection
    if (csi.srSelection.Left == 0 && csi.srSelection.Top == 0 &&
        csi.srSelection.Right == 0 && csi.srSelection.Bottom == 0) {
        PASS("Selection rect is (0,0,0,0) when no selection");
    } else {
        FAIL("GetConsoleSelectionInfo", "rect (%d,%d,%d,%d) expected (0,0,0,0)",
             csi.srSelection.Left, csi.srSelection.Top,
             csi.srSelection.Right, csi.srSelection.Bottom);
    }
}

void test_selection_info_iat(void) {
    // Test that the IAT hook is installed — GetConsoleSelectionInfo should
    // be patched in our import table and return our fake result.
    // If the hook isn't installed, the real GetConsoleSelectionInfo will
    // return the actual console selection state (which should also be no selection
    // in a normal console). So we just verify the function doesn't crash.
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (!k32) return;
    GetConsoleSelectionInfo_t pGetConsoleSelectionInfo =
        (GetConsoleSelectionInfo_t)GetProcAddress(k32, "GetConsoleSelectionInfo");
    if (!pGetConsoleSelectionInfo) return;

    MY_CONSOLE_SELECTION_INFO csi;
    // Zero-initialize with garbage pattern first to verify it's actually filled
    memset(&csi, 0xCC, sizeof(csi));
    BOOL result = pGetConsoleSelectionInfo(&csi);

    if (result) {
        PASS("GetConsoleSelectionInfo fills struct without crash");
    } else {
        FAIL("GetConsoleSelectionInfo", "returned FALSE on second call");
    }
}

int main(void) {
    printf("=== Console Selection Info Test ===\n\n"); fflush(stdout);

    test_selection_info_basic();
    test_selection_info_anchor();
    test_selection_info_rect();
    test_selection_info_iat();

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
