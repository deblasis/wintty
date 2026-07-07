// test_writeconsole_output_attr_pos.c — Verify WriteConsoleOutputAttribute positions cursor correctly
// Bug: WriteConsoleOutputAttribute didn't position the cursor before writing SGR+character.
// Attributes were applied at wherever the cursor happened to be, not at dwWriteCoord.

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
    SHORT base_y = 4;

    // ===== Test 1: WriteConsoleOutputAttribute at non-zero position =====
    {
        // First, write some text at column 10, row base_y
        WriteConsoleOutputCharacterW(hOut, L"HELLO", 5, (COORD){10, base_y}, NULL);

        // Now set attributes for those cells
        WORD attrs[5] = {
            FOREGROUND_RED | FOREGROUND_INTENSITY,    // H: bright red
            FOREGROUND_RED | FOREGROUND_INTENSITY,    // E: bright red
            FOREGROUND_GREEN | FOREGROUND_INTENSITY,  // L: bright green
            FOREGROUND_GREEN | FOREGROUND_INTENSITY,  // L: bright green
            FOREGROUND_BLUE | FOREGROUND_INTENSITY,   // O: bright blue
        };
        DWORD written;
        BOOL ok = WriteConsoleOutputAttribute(hOut, attrs, 5, (COORD){10, base_y}, &written);
        CHECK(ok, "WriteConsoleOutputAttribute at column 10 succeeded");

        // Read back attributes and verify they match
        WORD read_attrs[5];
        DWORD read;
        ReadConsoleOutputAttribute(hOut, read_attrs, 5, (COORD){10, base_y}, &read);
        CHECK(read_attrs[0] == attrs[0] && read_attrs[1] == attrs[1],
              "WriteConsoleOutputAttribute: attrs at column 10 match");
        CHECK(read_attrs[2] == attrs[2] && read_attrs[4] == attrs[4],
              "WriteConsoleOutputAttribute: attrs L and O match");
    }

    // ===== Test 2: Attributes don't affect cells at column 0 =====
    {
        // Write different text at column 0 of the same row
        WriteConsoleOutputCharacterW(hOut, L"XXXXX", 5, (COORD){0, base_y}, NULL);
        WORD default_attr = sbi.wAttributes;
        // Set attributes at column 10 again (different from column 0)
        WORD red_attr = FOREGROUND_RED | FOREGROUND_INTENSITY;
        DWORD written;
        WriteConsoleOutputAttribute(hOut, &red_attr, 1, (COORD){10, base_y}, &written);

        // Column 0 should still have its original text character
        WCHAR ch;
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y}, &read);
        CHECK(ch == L'X',
              "WriteConsoleOutputAttribute: column 0 character not affected");
    }

    // ===== Test 3: Attribute write spanning row boundary =====
    {
        SHORT row2 = base_y + 1;
        // Write characters near end of row
        GetConsoleScreenBufferInfo(hOut, &sbi);
        SHORT width = sbi.dwSize.X;
        SHORT start_x = width - 3;

        WriteConsoleOutputCharacterW(hOut, L"ABCD", 4, (COORD){start_x, row2}, NULL);

        // Set attributes spanning the row boundary
        WORD green_attrs[4];
        for (int i = 0; i < 4; i++) green_attrs[i] = FOREGROUND_GREEN | FOREGROUND_INTENSITY;
        DWORD written;
        WriteConsoleOutputAttribute(hOut, green_attrs, 4, (COORD){start_x, row2}, &written);

        // Read back: 2 attrs at end of row2, 2 at start of row3
        WORD read_attrs[4];
        DWORD read;
        ReadConsoleOutputAttribute(hOut, read_attrs, 4, (COORD){start_x, row2}, &read);
        CHECK(read_attrs[0] == (FOREGROUND_GREEN | FOREGROUND_INTENSITY),
              "WriteConsoleOutputAttribute row wrap: attr at end of row correct");
        CHECK(read_attrs[2] == (FOREGROUND_GREEN | FOREGROUND_INTENSITY),
              "WriteConsoleOutputAttribute row wrap: attr at start of next row correct");
    }

    // ===== Test 4: Cursor position preserved =====
    {
        SetConsoleCursorPosition(hOut, (COORD){5, base_y});
        WORD attr = FOREGROUND_BLUE;
        DWORD written;
        WriteConsoleOutputAttribute(hOut, &attr, 1, (COORD){20, base_y + 2}, &written);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 5 && sbi.dwCursorPosition.Y == base_y,
              "WriteConsoleOutputAttribute: cursor preserved at (5, base_y)");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
