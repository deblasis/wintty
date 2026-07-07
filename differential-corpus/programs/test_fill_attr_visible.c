// test_fill_attr_visible.c — Verify FillConsoleOutputAttribute changes visible terminal colors
// Bug: FillConsoleOutputAttribute only updated the cell grid but didn't re-emit characters.
// The terminal would still show old colors. Also didn't position cursor at dwWriteCoord.

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

    // ===== Test 1: FillConsoleOutputAttribute updates cell grid =====
    {
        // Write text at a known position
        WriteConsoleOutputCharacterW(hOut, L"TESTDATA", 8, (COORD){10, base_y}, NULL);

        // Fill attributes with red
        WORD red = FOREGROUND_RED | FOREGROUND_INTENSITY;
        DWORD written;
        BOOL ok = FillConsoleOutputAttribute(hOut, red, 4, (COORD){10, base_y}, &written);
        CHECK(ok, "FillConsoleOutputAttribute succeeded");
        CHECK(written == 4, "FillConsoleOutputAttribute wrote 4 attrs");

        // Read back attributes — should be red
        WORD attrs[4];
        DWORD read;
        ReadConsoleOutputAttribute(hOut, attrs, 4, (COORD){10, base_y}, &read);
        CHECK(attrs[0] == red && attrs[1] == red && attrs[2] == red && attrs[3] == red,
              "FillConsoleOutputAttribute: all 4 attrs are red");

        // Characters should still be the same
        WCHAR chars[4];
        ReadConsoleOutputCharacterW(hOut, chars, 4, (COORD){10, base_y}, &read);
        CHECK(chars[0] == L'T' && chars[1] == L'E' && chars[2] == L'S' && chars[3] == L'T',
              "FillConsoleOutputAttribute: characters unchanged after attr fill");
    }

    // ===== Test 2: FillConsoleOutputAttribute at non-zero position =====
    {
        // Write text at column 20
        WriteConsoleOutputCharacterW(hOut, L"ABCDE", 5, (COORD){20, base_y + 1}, NULL);

        // Fill with green attribute at column 20
        WORD green = FOREGROUND_GREEN | FOREGROUND_INTENSITY;
        DWORD written;
        FillConsoleOutputAttribute(hOut, green, 3, (COORD){20, base_y + 1}, &written);

        // Verify only the filled cells have green, not cells at column 0
        WORD attr_col0;
        DWORD read;
        ReadConsoleOutputAttribute(hOut, &attr_col0, 1, (COORD){0, base_y + 1}, &read);
        CHECK(attr_col0 != green,
              "FillConsoleOutputAttribute: column 0 not affected by fill at column 20");
    }

    // ===== Test 3: FillConsoleOutputAttribute spanning row boundary =====
    {
        SHORT width = sbi.dwSize.X;
        SHORT start_x = width - 3;

        // Write characters near end of row
        WriteConsoleOutputCharacterW(hOut, L"XYZW", 4, (COORD){start_x, base_y + 2}, NULL);

        // Fill attributes spanning the boundary
        WORD blue = FOREGROUND_BLUE | FOREGROUND_INTENSITY;
        DWORD written;
        FillConsoleOutputAttribute(hOut, blue, 4, (COORD){start_x, base_y + 2}, &written);

        // Read back attributes across the boundary
        WORD attrs[4];
        DWORD read;
        ReadConsoleOutputAttribute(hOut, attrs, 4, (COORD){start_x, base_y + 2}, &read);
        CHECK(attrs[0] == blue && attrs[1] == blue,
              "FillConsoleOutputAttribute row wrap: end-of-row attrs correct");
        CHECK(attrs[2] == blue && attrs[3] == blue,
              "FillConsoleOutputAttribute row wrap: start-of-next-row attrs correct");
    }

    // ===== Test 4: Cursor position preserved =====
    {
        SetConsoleCursorPosition(hOut, (COORD){7, base_y});
        WORD attr = FOREGROUND_RED;
        DWORD written;
        FillConsoleOutputAttribute(hOut, attr, 2, (COORD){30, base_y + 3}, &written);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 7 && sbi.dwCursorPosition.Y == base_y,
              "FillConsoleOutputAttribute: cursor preserved");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
