#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_initial_size(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    printf("Initial size: (%d,%d)\n", info.dwSize.X, info.dwSize.Y);
    // Should be the real terminal size (120x52 or similar), not 80x25
    if (info.dwSize.X > 80 && info.dwSize.Y > 25) {
        PASS("Initial terminal size is dynamic (>80x25)");
    } else if (info.dwSize.X == 120 && info.dwSize.Y == 52) {
        PASS("Initial terminal size is 120x52");
    } else {
        FAIL("initial_size", "expected >80x25");
    }
}

void test_size_consistency(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO info1, info2;
    GetConsoleScreenBufferInfo(hOut, &info1);
    GetConsoleScreenBufferInfo(hOut, &info2);
    if (info1.dwSize.X == info2.dwSize.X && info1.dwSize.Y == info2.dwSize.Y) {
        PASS("Terminal size consistent across calls");
    } else {
        FAIL("consistency", "size changed between calls");
    }
}

void test_size_vs_conout(void) {
    // Compare our size with CONOUT$ directly
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO our_info;
    GetConsoleScreenBufferInfo(hOut, &our_info);
    
    HANDLE hConout = CreateFileW(L"CONOUT$", GENERIC_READ | GENERIC_WRITE,
                                  FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (hConout == INVALID_HANDLE_VALUE) {
        FAIL("conout", "can't open CONOUT$");
        return;
    }
    
    CONSOLE_SCREEN_BUFFER_INFO real_info;
    GetConsoleScreenBufferInfo(hConout, &real_info);
    CloseHandle(hConout);
    
    // Our size should match the real console window size
    int real_w = real_info.srWindow.Right - real_info.srWindow.Left + 1;
    int real_h = real_info.srWindow.Bottom - real_info.srWindow.Top + 1;
    
    if (our_info.dwSize.X == real_w && our_info.dwSize.Y == real_h) {
        PASS("Our size matches real console window size");
    } else {
        FAIL("size_match", "mismatch");
    }
}

void test_cursor_within_bounds(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    
    // Cursor should be within bounds
    if (info.dwCursorPosition.X >= 0 && info.dwCursorPosition.X < info.dwSize.X &&
        info.dwCursorPosition.Y >= 0 && info.dwCursorPosition.Y < info.dwSize.Y) {
        PASS("Cursor within screen buffer bounds");
    } else {
        FAIL("cursor_bounds", "cursor out of bounds");
    }
}

int main(void) {
    printf("=== Dynamic Resize Test ===\n\n"); fflush(stdout);
    test_initial_size();
    test_size_consistency();
    test_size_vs_conout();
    test_cursor_within_bounds();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
