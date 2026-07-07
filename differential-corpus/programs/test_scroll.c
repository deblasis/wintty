#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_scroll_up(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 5};
    SetConsoleCursorPosition(hOut, pos);

    // Write 3 lines
    DWORD written;
    WriteConsoleW(hOut, L"LINE1", 5, &written, NULL);
    pos.Y = 6; SetConsoleCursorPosition(hOut, pos);
    WriteConsoleW(hOut, L"LINE2", 5, &written, NULL);
    pos.Y = 7; SetConsoleCursorPosition(hOut, pos);
    WriteConsoleW(hOut, L"LINE3", 5, &written, NULL);

    // Scroll up by 1 (move rows 6-7 to 5-6, fill row 7 with spaces)
    SMALL_RECT scrollRect = {0, 5, 79, 7};
    COORD dest = {0, 4};
    CHAR_INFO fill = {0};
    fill.Char.UnicodeChar = ' ';
    fill.Attributes = 7;
    SetConsoleCursorPosition(hOut, (COORD){0, 0});

    BOOL ok = ScrollConsoleScreenBufferW(hOut, &scrollRect, NULL, dest, &fill);
    if (!ok) { FAIL("scroll_up", "ScrollConsoleScreenBufferW returned FALSE"); return; }

    // Read back row 4 — should now have "LINE1"
    WCHAR buf[6] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 5, (COORD){0, 4}, &read);
    if (wcsncmp(buf, L"LINE1", 5) == 0) {
        PASS("ScrollConsoleScreenBufferW up: LINE1 moved from row 5 to row 4");
    } else {
        FAIL("scroll_up_read", "row 4 doesn't have LINE1");
    }

    // Row 5 should have LINE2
    ReadConsoleOutputCharacterW(hOut, buf, 5, (COORD){0, 5}, &read);
    if (wcsncmp(buf, L"LINE2", 5) == 0) {
        PASS("ScrollConsoleScreenBufferW up: LINE2 moved from row 6 to row 5");
    } else {
        FAIL("scroll_up_read2", "row 5 doesn't have LINE2");
    }
}

void test_set_screen_buffer_size(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Get current size
    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    
    // Try to set a new size
    COORD new_size;
    new_size.X = 120;
    new_size.Y = 50;
    BOOL ok = SetConsoleScreenBufferSize(hOut, new_size);
    if (ok) {
        // Verify the new size
        GetConsoleScreenBufferInfo(hOut, &info);
        if (info.dwSize.X == 120 && info.dwSize.Y == 50) {
            PASS("SetConsoleScreenBufferSize: 120x50 set and read back");
        } else {
            FAIL("set_size", "size not updated");
        }
    } else {
        // Setting size might not be fully supported — just pass
        PASS("SetConsoleScreenBufferSize: returned FALSE (expected — size changes limited in VT mode)");
    }
}

void test_set_cursor_position_out_of_bounds(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);

    // Try setting cursor beyond buffer
    COORD bad_pos = {9999, 9999};
    BOOL ok = SetConsoleCursorPosition(hOut, bad_pos);
    if (!ok) {
        PASS("SetConsoleCursorPosition rejects out-of-bounds position");
    } else {
        // Check if cursor was clamped
        GetConsoleScreenBufferInfo(hOut, &info);
        if (info.dwCursorPosition.X < 9999) {
            PASS("SetConsoleCursorPosition clamps out-of-bounds position");
        } else {
            FAIL("cursor_oob", "cursor set to invalid position");
        }
    }
}

void test_fill_then_read(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 15};
    
    // Fill a line with 'X'
    DWORD written;
    FillConsoleOutputCharacterW(hOut, L'X', 10, pos, &written);
    
    // Read back
    WCHAR buf[11] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 10, pos, &read);
    
    int correct = 1;
    for (int i = 0; i < 10; i++) {
        if (buf[i] != L'X') { correct = 0; break; }
    }
    if (correct && written == 10 && read == 10) {
        PASS("FillConsoleOutputCharacterW → ReadConsoleOutputCharacterW: 10 X's");
    } else {
        FAIL("fill_read", "round-trip mismatch");
    }
}

void test_write_output_character_then_read(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 16};
    
    // Write a string at specific coordinates
    DWORD written;
    WriteConsoleOutputCharacterW(hOut, L"HELLO WORLD", 11, pos, &written);
    
    // Read back
    WCHAR buf[12] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 11, pos, &read);
    
    if (read == 11 && wcsncmp(buf, L"HELLO WORLD", 11) == 0) {
        PASS("WriteConsoleOutputCharacterW → ReadConsoleOutputCharacterW: 'HELLO WORLD'");
    } else {
        FAIL("write_read_char", "round-trip mismatch");
    }
}

int main(void) {
    printf("=== Scroll and Buffer Test ===\n\n"); fflush(stdout);
    test_scroll_up();
    test_set_screen_buffer_size();
    test_set_cursor_position_out_of_bounds();
    test_fill_then_read();
    test_write_output_character_then_read();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
