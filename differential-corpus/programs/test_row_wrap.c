// test_row_wrap.c — Verify cell grid row wrapping for Fill/Write/Read operations
// When coordinates exceed the buffer width, operations should wrap to the next row.

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
    int width = sbi.dwSize.X;
    int height = sbi.dwSize.Y;
    // Use rows 2-5 for testing (avoid rows 0-1 which may have test output)
    COORD base = {0, 3};

    // ===== Test 1: FillConsoleOutputCharacterW wraps rows =====
    {
        // Fill starting near end of row, spanning into next row
        COORD start = {width - 3, base.Y};
        FillConsoleOutputCharacterW(hOut, L'Q', 6, start, NULL);

        // Read back: should have QQQ at end of row 3 and QQQ at start of row 4
        WCHAR buf[6];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 3, (COORD){width - 3, base.Y}, &read);
        CHECK(buf[0] == L'Q' && buf[1] == L'Q' && buf[2] == L'Q',
              "FillConsoleOutputCharacterW: last 3 cells of row filled");

        ReadConsoleOutputCharacterW(hOut, buf, 3, (COORD){0, base.Y + 1}, &read);
        CHECK(buf[0] == L'Q' && buf[1] == L'Q' && buf[2] == L'Q',
              "FillConsoleOutputCharacterW: wraps to next row");
    }

    // ===== Test 2: WriteConsoleOutputCharacterW wraps rows =====
    {
        COORD start = {width - 4, base.Y + 2};
        WriteConsoleOutputCharacterW(hOut, L"ABCDEF", 6, start, NULL);

        // Read back: ABCD at end of row, EF at start of next row
        WCHAR buf[6];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 4, (COORD){width - 4, base.Y + 2}, &read);
        CHECK(buf[0] == L'A' && buf[1] == L'B' && buf[2] == L'C' && buf[3] == L'D',
              "WriteConsoleOutputCharacterW: last 4 cells of row written");

        ReadConsoleOutputCharacterW(hOut, buf, 2, (COORD){0, base.Y + 3}, &read);
        CHECK(buf[0] == L'E' && buf[1] == L'F',
              "WriteConsoleOutputCharacterW: wraps to next row");
    }

    // ===== Test 3: ReadConsoleOutputCharacterW wraps rows =====
    {
        // Write known data at end of row and start of next row
        COORD start = {width - 2, base.Y};
        WriteConsoleOutputCharacterW(hOut, L"MN", 2, start, NULL);
        WriteConsoleOutputCharacterW(hOut, L"PQ", 2, (COORD){0, base.Y + 1}, NULL);

        // Read spanning the row boundary
        WCHAR buf[4];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 4, start, &read);
        CHECK(buf[0] == L'M' && buf[1] == L'N' && buf[2] == L'P' && buf[3] == L'Q',
              "ReadConsoleOutputCharacterW: wraps to next row");
    }

    // ===== Test 4: FillConsoleOutputAttribute wraps rows =====
    {
        COORD start = {width - 2, base.Y + 2};
        // Fill with a distinctive attribute (bright cyan on black = 0x0B)
        FillConsoleOutputAttribute(hOut, 0x0B, 4, start, NULL);

        // Read attributes at end of row and start of next
        WORD attrs[4];
        DWORD read;
        ReadConsoleOutputAttribute(hOut, attrs, 2, start, &read);
        CHECK(attrs[0] == 0x0B && attrs[1] == 0x0B,
              "FillConsoleOutputAttribute: last 2 attrs of row set");

        ReadConsoleOutputAttribute(hOut, attrs, 2, (COORD){0, base.Y + 3}, &read);
        CHECK(attrs[0] == 0x0B && attrs[1] == 0x0B,
              "FillConsoleOutputAttribute: wraps to next row");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
