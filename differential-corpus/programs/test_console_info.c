#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_console_window(void) {
    HWND hwnd = GetConsoleWindow();
    if (hwnd != NULL) {
        PASS("GetConsoleWindow returns non-NULL HWND");
    } else {
        FAIL("console_window", "returned NULL");
    }
}

void test_process_list(void) {
    DWORD pids[16] = {0};
    DWORD count = GetConsoleProcessList(pids, 16);
    if (count >= 1) {
        PASS("GetConsoleProcessList returns >= 1 process");
    } else {
        FAIL("process_list", "returned 0");
    }
}

void test_largest_window_size(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD size = GetLargestConsoleWindowSize(hOut);
    if (size.X > 0 && size.Y > 0) {
        PASS("GetLargestConsoleWindowSize returns valid size");
    } else {
        FAIL("largest_size", "returned (0,0)");
    }
}

void test_font_info(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_FONT_INFOEX font = {0};
    font.cbSize = sizeof(font);
    BOOL ok = GetCurrentConsoleFontEx(hOut, FALSE, &font);
    if (ok) {
        PASS("GetCurrentConsoleFontEx succeeds");
    } else {
        FAIL("font_info", "returned FALSE");
    }
}

void test_screen_buffer_info_ex(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFOEX info = {0};
    info.cbSize = sizeof(info);
    BOOL ok = GetConsoleScreenBufferInfoEx(hOut, &info);
    if (ok && info.cbSize == sizeof(info)) {
        PASS("GetConsoleScreenBufferInfoEx succeeds with correct cbSize");
    } else {
        FAIL("sb_info_ex", "failed");
    }
}

void test_display_mode(void) {
    DWORD mode = 0;
    BOOL ok = GetConsoleDisplayMode(&mode);
    if (ok) {
        PASS("GetConsoleDisplayMode succeeds");
    } else {
        FAIL("display_mode", "returned FALSE");
    }
}

void test_history_info(void) {
    CONSOLE_HISTORY_INFO info = {0};
    info.cbSize = sizeof(info);
    BOOL ok = GetConsoleHistoryInfo(&info);
    if (ok && info.cbSize == sizeof(info)) {
        PASS("GetConsoleHistoryInfo succeeds");
    } else {
        FAIL("history_info", "failed");
    }
}

int main(void) {
    printf("=== Console Info API Test ===\n\n"); fflush(stdout);
    test_console_window();
    test_process_list();
    test_largest_window_size();
    test_font_info();
    test_screen_buffer_info_ex();
    test_display_mode();
    test_history_info();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
