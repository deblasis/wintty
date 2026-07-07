// test_writeconsole_output_offset.c — Verify WriteConsoleOutputW at non-zero column offset
// Bug: \r\n only moves to column 0, not dst_left. Multi-row writes at non-zero
// columns had incorrect cursor tracking.

#include <windows.h>
#include <stdio.h>

static int tests_passed = 0;
static int tests_failed = 0;

#define CHECK(cond, msg) do { \
    if (cond) { printf("PASS: %s\n", msg); tests_passed++; } \
    else { printf("FAIL: %s\n", msg); tests_failed++; } \
} while(0)

int main(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO sbi;
    GetConsoleScreenBufferInfo(hOut, &sbi);
    // Use rows 3-6 to avoid printf output area
    COORD base = {0, 4};

    // ===== Test 1: WriteConsoleOutputW at column 5, 2 rows =====
    {
        // Create a 3x2 buffer with distinct characters
        CHAR_INFO cells[6];
        cells[0].Char.UnicodeChar = L'A'; cells[0].Attributes = 0x07;
        cells[1].Char.UnicodeChar = L'B'; cells[1].Attributes = 0x07;
        cells[2].Char.UnicodeChar = L'C'; cells[2].Attributes = 0x07;
        cells[3].Char.UnicodeChar = L'D'; cells[3].Attributes = 0x07;
        cells[4].Char.UnicodeChar = L'E'; cells[4].Attributes = 0x07;
        cells[5].Char.UnicodeChar = L'F'; cells[5].Attributes = 0x07;

        SMALL_RECT write_region = {5, base.Y, 7, base.Y + 1};
        BOOL ok = WriteConsoleOutputW(hOut, cells, (COORD){3, 2}, (COORD){0, 0}, &write_region);
        CHECK(ok, "WriteConsoleOutputW at column 5 succeeded");

        // Read back cells at (5, base.Y) through (7, base.Y+1)
        WCHAR buf[3];
        DWORD read;

        // Row 1: A B C at columns 5,6,7
        ReadConsoleOutputCharacterW(hOut, buf, 3, (COORD){5, base.Y}, &read);
        CHECK(buf[0] == L'A' && buf[1] == L'B' && buf[2] == L'C',
              "WriteConsoleOutputW offset row 1: ABC at columns 5-7");

        // Row 2: D E F at columns 5,6,7
        ReadConsoleOutputCharacterW(hOut, buf, 3, (COORD){5, base.Y + 1}, &read);
        CHECK(buf[0] == L'D' && buf[1] == L'E' && buf[2] == L'F',
              "WriteConsoleOutputW offset row 2: DEF at columns 5-7");

        // Verify column 0 was NOT written (should be spaces)
        ReadConsoleOutputCharacterW(hOut, buf, 1, (COORD){0, base.Y + 1}, &read);
        CHECK(buf[0] != L'D',
              "WriteConsoleOutputW offset: column 0 row 2 not overwritten");
    }

    // ===== Test 2: WriteConsoleOutputW with non-zero buffer coord =====
    {
        // Create a 4x3 buffer but only write the 2x2 sub-region starting at (1,1)
        CHAR_INFO cells[12];
        for (int i = 0; i < 12; i++) {
            cells[i].Char.UnicodeChar = L'?';
            cells[i].Attributes = 0x07;
        }
        // Sub-region at (1,1) in the 4x3 buffer
        cells[1 * 4 + 1].Char.UnicodeChar = L'X'; // row 1, col 1
        cells[1 * 4 + 2].Char.UnicodeChar = L'Y'; // row 1, col 2
        cells[2 * 4 + 1].Char.UnicodeChar = L'Z'; // row 2, col 1
        cells[2 * 4 + 2].Char.UnicodeChar = L'W'; // row 2, col 2

        SMALL_RECT write_region = {10, base.Y + 2, 11, base.Y + 3};
        BOOL ok = WriteConsoleOutputW(hOut, cells, (COORD){4, 3}, (COORD){1, 1}, &write_region);
        CHECK(ok, "WriteConsoleOutputW with buffer coord offset succeeded");

        // Read back: X Y on row base.Y+2, Z W on row base.Y+3, at columns 10-11
        WCHAR buf[2];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 2, (COORD){10, base.Y + 2}, &read);
        CHECK(buf[0] == L'X' && buf[1] == L'Y',
              "WriteConsoleOutputW buffer coord: XY at row 1");

        ReadConsoleOutputCharacterW(hOut, buf, 2, (COORD){10, base.Y + 3}, &read);
        CHECK(buf[0] == L'Z' && buf[1] == L'W',
              "WriteConsoleOutputW buffer coord: ZW at row 2");
    }

    // ===== Test 3: Cursor position preserved after offset WriteConsoleOutputW =====
    {
        SetConsoleCursorPosition(hOut, (COORD){0, base.Y});
        CHAR_INFO cells[2];
        cells[0].Char.UnicodeChar = L'P'; cells[0].Attributes = 0x07;
        cells[1].Char.UnicodeChar = L'Q'; cells[1].Attributes = 0x07;

        SMALL_RECT write_region = {20, base.Y, 21, base.Y};
        WriteConsoleOutputW(hOut, cells, (COORD){2, 1}, (COORD){0, 0}, &write_region);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 0 && sbi.dwCursorPosition.Y == base.Y,
              "Cursor preserved after WriteConsoleOutputW at offset");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
