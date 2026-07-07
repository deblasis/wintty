#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_full_width_scroll(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Write content on rows 5-7
    COORD pos;
    DWORD written;
    pos = (COORD){0, 5}; SetConsoleCursorPosition(hOut, pos);
    WriteConsoleW(hOut, L"LINE1_AAAA", 10, &written, NULL);
    pos = (COORD){0, 6}; SetConsoleCursorPosition(hOut, pos);
    WriteConsoleW(hOut, L"LINE2_BBBB", 10, &written, NULL);
    pos = (COORD){0, 7}; SetConsoleCursorPosition(hOut, pos);
    WriteConsoleW(hOut, L"LINE3_CCCC", 10, &written, NULL);
    
    // Full-width scroll up by 1 (rows 5-7 → rows 4-6)
    SMALL_RECT scrollRect = {0, 5, 79, 7};
    COORD dest = {0, 4};
    CHAR_INFO fill = {0};
    fill.Char.UnicodeChar = ' ';
    fill.Attributes = 7;
    
    BOOL ok = ScrollConsoleScreenBufferW(hOut, &scrollRect, NULL, dest, &fill);
    if (!ok) { FAIL("full_scroll", "returned FALSE"); return; }
    
    // Verify row 4 now has LINE1_AAAA
    WCHAR buf[11] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 10, (COORD){0, 4}, &read);
    if (wcsncmp(buf, L"LINE1_AAAA", 10) == 0) {
        PASS("Full-width scroll up: LINE1 moved to row 4");
    } else {
        FAIL("full_scroll_read", "row 4 mismatch");
    }
}

void test_partial_width_scroll(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Write content in columns 0-9 on rows 10-12
    COORD pos;
    DWORD written;
    pos = (COORD){0, 10}; SetConsoleCursorPosition(hOut, pos);
    WriteConsoleW(hOut, L"AAA", 3, &written, NULL);
    pos = (COORD){0, 11}; SetConsoleCursorPosition(hOut, pos);
    WriteConsoleW(hOut, L"BBB", 3, &written, NULL);
    pos = (COORD){0, 12}; SetConsoleCursorPosition(hOut, pos);
    WriteConsoleW(hOut, L"CCC", 3, &written, NULL);
    
    // Partial-width scroll: scroll columns 0-9, rows 10-12 up by 1
    SMALL_RECT scrollRect = {0, 10, 9, 12};
    COORD dest = {0, 9};
    CHAR_INFO fill = {0};
    fill.Char.UnicodeChar = '.';
    fill.Attributes = 7;
    
    BOOL ok = ScrollConsoleScreenBufferW(hOut, &scrollRect, NULL, dest, &fill);
    if (!ok) { FAIL("partial_scroll", "returned FALSE"); return; }
    
    // Verify row 9 columns 0-2 now have AAA
    WCHAR buf[11] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 3, (COORD){0, 9}, &read);
    if (wcsncmp(buf, L"AAA", 3) == 0) {
        PASS("Partial-width scroll: AAA moved from row 10 to row 9");
    } else {
        FAIL("partial_scroll_read", "row 9 mismatch");
    }
    
    // Verify row 10 columns 0-2 now have BBB
    ReadConsoleOutputCharacterW(hOut, buf, 3, (COORD){0, 10}, &read);
    if (wcsncmp(buf, L"BBB", 3) == 0) {
        PASS("Partial-width scroll: BBB moved from row 11 to row 10");
    } else {
        FAIL("partial_scroll_read2", "row 10 mismatch");
    }
}

void test_diagonal_scroll(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Write content
    COORD pos;
    DWORD written;
    pos = (COORD){0, 15}; SetConsoleCursorPosition(hOut, pos);
    WriteConsoleW(hOut, L"DIAG", 4, &written, NULL);
    
    // Diagonal scroll: move columns 0-4, rows 15-16 to (5, 14)
    SMALL_RECT scrollRect = {0, 15, 4, 16};
    COORD dest = {5, 14};
    CHAR_INFO fill = {0};
    fill.Char.UnicodeChar = ' ';
    fill.Attributes = 7;
    
    BOOL ok = ScrollConsoleScreenBufferW(hOut, &scrollRect, NULL, dest, &fill);
    if (!ok) { FAIL("diag_scroll", "returned FALSE"); return; }
    
    // Verify row 14 columns 5-8 now have DIAG
    WCHAR buf[5] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 4, (COORD){5, 14}, &read);
    if (wcsncmp(buf, L"DIAG", 4) == 0) {
        PASS("Diagonal scroll: DIAG moved from (0,15) to (5,14)");
    } else {
        FAIL("diag_scroll_read", "mismatch at destination");
    }
}

int main(void) {
    printf("=== Partial Scroll Test ===\n\n"); fflush(stdout);
    test_full_width_scroll();
    test_partial_width_scroll();
    test_diagonal_scroll();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
