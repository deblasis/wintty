// test_writeconsole_output_clear.c — Verify WriteConsoleOutputW clears old content with spaces
// Bug: WriteConsoleOutputW skipped space cells in VT output. If the terminal had existing
// content at those positions, the old content remained visible ("ghost characters").
// Fix: only skip spaces if the cell grid already has a space there.

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
    SHORT base_y = 4;

    // ===== Test 1: WriteConsoleOutputW overwrites text with spaces =====
    {
        // First, write "HELLO" at (0, base_y)
        WriteConsoleOutputCharacterW(hOut, L"HELLO", 5, (COORD){0, base_y}, NULL);

        // Verify "HELLO" is in the cell grid
        WCHAR buf[5];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 5, (COORD){0, base_y}, &read);
        CHECK(buf[0] == L'H' && buf[4] == L'O',
              "Clear test setup: HELLO written to cell grid");

        // Now write a 5x1 block of spaces (default attr) over "HELLO"
        CHAR_INFO cells[5];
        for (int i = 0; i < 5; i++) {
            cells[i].Char.UnicodeChar = L' ';
            cells[i].Attributes = 0x07; // default white on black
        }
        SMALL_RECT region = {0, base_y, 4, base_y};
        BOOL ok = WriteConsoleOutputW(hOut, cells, (COORD){5, 1}, (COORD){0, 0}, &region);
        CHECK(ok, "WriteConsoleOutputW with spaces succeeded");

        // Read back — should be spaces
        ReadConsoleOutputCharacterW(hOut, buf, 5, (COORD){0, base_y}, &read);
        CHECK(buf[0] == L' ' && buf[1] == L' ' && buf[2] == L' ' && buf[3] == L' ' && buf[4] == L' ',
              "WriteConsoleOutputW: spaces overwrite HELLO in cell grid");
    }

    // ===== Test 2: Partial overwrite — mix of content and spaces =====
    {
        // Write "ABCDE" at (0, base_y + 1)
        WriteConsoleOutputCharacterW(hOut, L"ABCDE", 5, (COORD){0, base_y + 1}, NULL);

        // Write a block with "X" at col 0, spaces at cols 1-3, "Y" at col 4
        CHAR_INFO cells[5];
        cells[0].Char.UnicodeChar = L'X'; cells[0].Attributes = 0x07;
        cells[1].Char.UnicodeChar = L' '; cells[1].Attributes = 0x07;
        cells[2].Char.UnicodeChar = L' '; cells[2].Attributes = 0x07;
        cells[3].Char.UnicodeChar = L' '; cells[3].Attributes = 0x07;
        cells[4].Char.UnicodeChar = L'Y'; cells[4].Attributes = 0x07;

        SMALL_RECT region = {0, base_y + 1, 4, base_y + 1};
        WriteConsoleOutputW(hOut, cells, (COORD){5, 1}, (COORD){0, 0}, &region);

        // Read back: should be "X   Y" (X, 3 spaces, Y)
        WCHAR buf[5];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 5, (COORD){0, base_y + 1}, &read);
        CHECK(buf[0] == L'X',
              "Partial overwrite: X at col 0");
        CHECK(buf[1] == L' ' && buf[2] == L' ' && buf[3] == L' ',
              "Partial overwrite: spaces at cols 1-3 (old BCD cleared)");
        CHECK(buf[4] == L'Y',
              "Partial overwrite: Y at col 4");
    }

    // ===== Test 3: Spaces over spaces — should be skipped (optimization) =====
    {
        // Write spaces at (0, base_y + 2)
        FillConsoleOutputCharacterW(hOut, L' ', 5, (COORD){0, base_y + 2}, NULL);

        // Write spaces again via WriteConsoleOutputW
        CHAR_INFO cells[5];
        for (int i = 0; i < 5; i++) {
            cells[i].Char.UnicodeChar = L' ';
            cells[i].Attributes = 0x07;
        }
        SMALL_RECT region = {0, base_y + 2, 4, base_y + 2};
        BOOL ok = WriteConsoleOutputW(hOut, cells, (COORD){5, 1}, (COORD){0, 0}, &region);
        CHECK(ok, "WriteConsoleOutputW: spaces over spaces succeeds");

        WCHAR buf[5];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 5, (COORD){0, base_y + 2}, &read);
        CHECK(buf[0] == L' ' && buf[4] == L' ',
              "WriteConsoleOutputW: spaces over spaces is still spaces");
    }

    // ===== Test 4: Colored space overwrites white text =====
    {
        // Write white text "TEST" at (0, base_y + 3)
        SetConsoleTextAttribute(hOut, 0x07);
        WriteConsoleOutputCharacterW(hOut, L"TEST", 4, (COORD){0, base_y + 3}, NULL);

        // Write a colored space over it (red on black = 0x0C)
        CHAR_INFO cells[1];
        cells[0].Char.UnicodeChar = L' ';
        cells[0].Attributes = 0x0C; // red fg

        SMALL_RECT region = {0, base_y + 3, 0, base_y + 3};
        WriteConsoleOutputW(hOut, cells, (COORD){1, 1}, (COORD){0, 0}, &region);

        // Read back: should be a space with red attribute
        WCHAR ch;
        WORD attr;
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y + 3}, &read);
        ReadConsoleOutputAttribute(hOut, &attr, 1, (COORD){0, base_y + 3}, &read);
        CHECK(ch == L' ', "Colored space overwrites text: character is space");
        CHECK(attr == 0x0C, "Colored space overwrites text: attribute is red");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
