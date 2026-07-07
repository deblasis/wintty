#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_write_zero_bytes(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD written = 0xDEAD;
    BOOL ok = WriteConsoleW(hOut, L"hello", 0, &written, NULL);
    if (ok && written == 0) {
        PASS("WriteConsoleW 0 bytes returns TRUE, written=0");
    } else {
        FAIL("write_zero", "unexpected result");
    }
}

void test_write_null_buffer(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD written = 0xDEAD;
    // WriteConsoleW with NULL buffer and 0 length should succeed
    BOOL ok = WriteConsoleW(hOut, NULL, 0, &written, NULL);
    if (ok && written == 0) {
        PASS("WriteConsoleW NULL+0 returns TRUE");
    } else if (!ok) {
        // Also acceptable — returning FALSE for NULL buffer
        PASS("WriteConsoleW NULL+0 returns FALSE (acceptable)");
    } else {
        FAIL("write_null", "unexpected result");
    }
}

void test_fill_zero_count(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD written = 0xDEAD;
    COORD pos = {0, 0};
    BOOL ok = FillConsoleOutputCharacterW(hOut, 'X', 0, pos, &written);
    if (ok) {
        PASS("FillConsoleOutputCharacterW 0 count returns TRUE");
    } else {
        FAIL("fill_zero", "returned FALSE");
    }
}

void test_read_zero_chars(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    WCHAR buf[1] = {0xFFFF};
    DWORD read = 0xDEAD;
    COORD pos = {0, 0};
    BOOL ok = ReadConsoleOutputCharacterW(hOut, buf, 0, pos, &read);
    if (ok) {
        PASS("ReadConsoleOutputCharacterW 0 count returns TRUE");
    } else {
        FAIL("read_zero", "returned FALSE");
    }
}

void test_set_cursor_origin(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 0};
    BOOL ok = SetConsoleCursorPosition(hOut, pos);
    if (ok) {
        // Verify cursor is at (0,0)
        CONSOLE_SCREEN_BUFFER_INFO info;
        if (GetConsoleScreenBufferInfo(hOut, &info)) {
            if (info.dwCursorPosition.X == 0 && info.dwCursorPosition.Y == 0) {
                PASS("SetConsoleCursorPosition (0,0) works and verified");
            } else {
                FAIL("cursor_origin", "cursor not at (0,0)");
            }
        } else {
            PASS("SetConsoleCursorPosition (0,0) returns TRUE");
        }
    } else {
        FAIL("cursor_origin", "returned FALSE");
    }
}

void test_get_cursor_info(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_CURSOR_INFO info;
    BOOL ok = GetConsoleCursorInfo(hOut, &info);
    if (ok && info.dwSize >= 1 && info.dwSize <= 100) {
        PASS("GetConsoleCursorInfo returns valid size");
    } else if (ok) {
        PASS("GetConsoleCursorInfo returns TRUE");
    } else {
        FAIL("cursor_info", "returned FALSE");
    }
}

void test_get_console_window(void) {
    HWND hwnd = GetConsoleWindow();
    if (hwnd != NULL) {
        PASS("GetConsoleWindow returns non-NULL");
    } else {
        // Our implementation returns a sentinel HWND
        FAIL("console_window", "returned NULL");
    }
}

void test_get_largest_window_size(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD size = GetLargestConsoleWindowSize(hOut);
    if (size.X > 0 && size.Y > 0) {
        PASS("GetLargestConsoleWindowSize returns positive values");
    } else {
        FAIL("largest_size", "returned zero size");
    }
}

void test_scroll_zero_region(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    SMALL_RECT scrollRect = {0, 0, 0, 0};
    COORD dest = {0, 0};
    CHAR_INFO fill = {0};
    fill.Char.UnicodeChar = ' ';
    fill.Attributes = 7;
    // Scroll of a 1x1 region to itself — should succeed (no-op)
    BOOL ok = ScrollConsoleScreenBufferW(hOut, &scrollRect, NULL, dest, &fill);
    if (ok) {
        PASS("ScrollConsoleScreenBufferW 1x1 to same position returns TRUE");
    } else {
        FAIL("scroll_zero", "returned FALSE");
    }
}

void test_write_output_then_read(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Write a known character at a known position
    COORD write_pos = {0, 0};
    DWORD written;
    // Move cursor and write
    SetConsoleCursorPosition(hOut, write_pos);
    WriteConsoleW(hOut, L"EDGE", 4, &written, NULL);
    
    // Read back
    WCHAR buf[5] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 4, write_pos, &read);
    if (wcsncmp(buf, L"EDGE", 4) == 0) {
        PASS("Write→Read round-trip: EDGE matches");
    } else {
        FAIL("write_read", "mismatch");
    }
}

void test_fill_then_read_attr(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Fill attributes
    COORD pos = {0, 0};
    DWORD written;
    FillConsoleOutputAttribute(hOut, 0x0C, 4, pos, &written); // red on black
    
    // Read back attributes
    WORD attrs[4] = {0};
    DWORD read;
    ReadConsoleOutputAttribute(hOut, attrs, 4, pos, &read);
    if (attrs[0] == 0x0C && attrs[1] == 0x0C) {
        PASS("Fill→Read attribute round-trip: 0x0C matches");
    } else {
        FAIL("fill_attr", "attribute mismatch");
    }
}

void test_set_text_attr_roundtrip(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Set text attribute to bright green on blue (0x0A + 0x10 = 0x1A)
    WORD test_attr = 0x2E; // bright yellow on green
    SetConsoleTextAttribute(hOut, test_attr);
    
    // Verify via GetConsoleScreenBufferInfo
    CONSOLE_SCREEN_BUFFER_INFO info;
    if (GetConsoleScreenBufferInfo(hOut, &info)) {
        if (info.wAttributes == test_attr) {
            PASS("Set→Get attribute round-trip: 0x2E matches");
        } else {
            FAIL("text_attr", "attribute mismatch");
        }
    } else {
        FAIL("text_attr", "GetConsoleScreenBufferInfo failed");
    }
}

void test_set_cursor_far(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Get screen buffer size and try to set cursor at last valid position
    CONSOLE_SCREEN_BUFFER_INFO info;
    if (GetConsoleScreenBufferInfo(hOut, &info)) {
        COORD pos = {
            .X = info.dwSize.X - 1,
            .Y = info.dwSize.Y - 1
        };
        BOOL ok = SetConsoleCursorPosition(hOut, pos);
        if (ok) {
            // Verify position
            GetConsoleScreenBufferInfo(hOut, &info);
            if (info.dwCursorPosition.X == pos.X && info.dwCursorPosition.Y == pos.Y) {
                PASS("SetConsoleCursorPosition at (max-1,max-1) works");
            } else {
                FAIL("cursor_far", "position mismatch after set");
            }
        } else {
            FAIL("cursor_far", "returned FALSE");
        }
    } else {
        FAIL("cursor_far", "GetConsoleScreenBufferInfo failed");
    }
}

void test_get_number_of_input_events(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    DWORD count = 0;
    BOOL ok = GetNumberOfConsoleInputEvents(hIn, &count);
    if (ok) {
        PASS("GetNumberOfConsoleInputEvents returns TRUE");
    } else {
        FAIL("input_events", "returned FALSE");
    }
}

int main(void) {
    printf("=== Edge Case Tests ===\n\n"); fflush(stdout);
    
    test_write_zero_bytes();
    test_write_null_buffer();
    test_fill_zero_count();
    test_read_zero_chars();
    test_set_cursor_origin();
    test_get_cursor_info();
    test_get_console_window();
    test_get_largest_window_size();
    test_scroll_zero_region();
    test_write_output_then_read();
    test_fill_then_read_attr();
    test_set_text_attr_roundtrip();
    test_set_cursor_far();
    test_get_number_of_input_events();
    
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
