#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_get_cursor_info(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_CURSOR_INFO info;
    BOOL ok = GetConsoleCursorInfo(hOut, &info);
    if (ok && info.dwSize >= 1 && info.dwSize <= 100 && info.bVisible == TRUE) {
        PASS("GetConsoleCursorInfo: default size and visible");
    } else {
        FAIL("get_cursor_info", "unexpected values");
    }
}

void test_set_cursor_size(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_CURSOR_INFO info;
    info.dwSize = 50;
    info.bVisible = TRUE;
    BOOL ok = SetConsoleCursorInfo(hOut, &info);
    if (!ok) { FAIL("set_size", "SetConsoleCursorInfo returned FALSE"); return; }

    CONSOLE_CURSOR_INFO read_info;
    GetConsoleCursorInfo(hOut, &read_info);
    if (read_info.dwSize == 50 && read_info.bVisible == TRUE) {
        PASS("SetConsoleCursorInfo: size=50, visible=TRUE round-trip");
    } else {
        FAIL("set_size_read", "round-trip mismatch");
    }
}

void test_hide_show_cursor(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Hide cursor
    CONSOLE_CURSOR_INFO info;
    info.dwSize = 25;
    info.bVisible = FALSE;
    SetConsoleCursorInfo(hOut, &info);
    
    CONSOLE_CURSOR_INFO read_info;
    GetConsoleCursorInfo(hOut, &read_info);
    if (read_info.bVisible == FALSE) {
        PASS("SetConsoleCursorInfo: cursor hidden (bVisible=FALSE)");
    } else {
        FAIL("hide_cursor", "cursor still visible");
    }
    
    // Show cursor again
    info.bVisible = TRUE;
    SetConsoleCursorInfo(hOut, &info);
    GetConsoleCursorInfo(hOut, &read_info);
    if (read_info.bVisible == TRUE) {
        PASS("SetConsoleCursorInfo: cursor shown again (bVisible=TRUE)");
    } else {
        FAIL("show_cursor", "cursor not shown");
    }
}

void test_cursor_info_invalid_handle(void) {
    CONSOLE_CURSOR_INFO info;
    BOOL ok = GetConsoleCursorInfo((HANDLE)0x999, &info);
    if (ok == FALSE) {
        PASS("GetConsoleCursorInfo rejects invalid handle");
    } else {
        FAIL("invalid_handle", "should have returned FALSE");
    }
}

int main(void) {
    printf("=== Console Cursor Info Test ===\n\n"); fflush(stdout);
    test_get_cursor_info();
    test_set_cursor_size();
    test_hide_show_cursor();
    test_cursor_info_invalid_handle();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
