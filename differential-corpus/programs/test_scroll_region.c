// test_scroll_region.c — Verify ScrollConsoleScreenBufferW with partial-height scroll regions
// Bug: VT scroll sequences scroll the ENTIRE visible area. For partial-height scrolls,
// DECSTBM sets scroll margins to limit the scroll region.

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
    SHORT width = sbi.dwSize.X;
    SHORT height = sbi.dwSize.Y;
    SHORT base_y = 4;

    // ===== Test 1: Full-height scroll up (baseline — should work) =====
    {
        // Fill all rows with unique markers
        for (SHORT row = 0; row < height; row++) {
            WCHAR ch = L'0' + (row % 10);
            FillConsoleOutputCharacterW(hOut, ch, 3, (COORD){0, row}, NULL);
        }

        // Scroll entire screen up by 1: source rows 1 to height-1, dest row 0
        SMALL_RECT scroll_rect = {0, 1, width - 1, height - 1};
        CHAR_INFO fill = {.Char = {.UnicodeChar = L'*'}, .Attributes = 0x07};
        ScrollConsoleScreenBufferW(hOut, &scroll_rect, NULL, (COORD){0, 0}, &fill);

        // Row 0 should have old row 1 content
        WCHAR ch;
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, 0}, &read);
        CHECK(ch == L'1', "Full-height scroll up: row 0 has content from old row 1");

        // Last row should be fill
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, height - 1}, &read);
        CHECK(ch == L'*', "Full-height scroll up: last row has fill character");
    }

    // ===== Test 2: Partial-height scroll with clip rectangle =====
    {
        // Write markers in rows base_y through base_y+5
        for (SHORT row = base_y; row <= base_y + 5; row++) {
            WCHAR ch = L'A' + (row - base_y);
            FillConsoleOutputCharacterW(hOut, ch, 3, (COORD){0, row}, NULL);
        }
        // Write marker in row above and below
        FillConsoleOutputCharacterW(hOut, L'^', 1, (COORD){0, base_y - 1}, NULL);
        FillConsoleOutputCharacterW(hOut, L'v', 1, (COORD){0, base_y + 6}, NULL);

        // Scroll rows base_y to base_y+4 up by 1, CLIPPED to rows base_y to base_y+4
        SMALL_RECT scroll_rect = {0, base_y + 1, width - 1, base_y + 4};
        SMALL_RECT clip_rect = {0, base_y, width - 1, base_y + 4};
        CHAR_INFO fill = {.Char = {.UnicodeChar = L'-'}, .Attributes = 0x07};
        // Note: clip_rect is ignored in our implementation, but the cell grid
        // is updated correctly regardless since we copy source→dest + fill vacated.
        // The key test is that the cell grid is correct.
        ScrollConsoleScreenBufferW(hOut, &scroll_rect, &clip_rect, (COORD){0, base_y}, &fill);

        // Row above should be unchanged
        WCHAR ch;
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y - 1}, &read);
        CHECK(ch == L'^', "Partial scroll: row above unchanged");

        // First row of region should have B (from old row base_y+1)
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y}, &read);
        CHECK(ch == L'B', "Partial scroll: row 0 has content from old row 1");

        // Last row of region should be fill
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y + 4}, &read);
        CHECK(ch == L'-', "Partial scroll: last row has fill character");

        // Row below should be unchanged
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y + 6}, &read);
        CHECK(ch == L'v', "Partial scroll: row below unchanged");
    }

    // ===== Test 3: Scroll down with partial region =====
    {
        // Write markers
        for (SHORT row = base_y; row <= base_y + 5; row++) {
            WCHAR ch = L'M' + (row - base_y);
            FillConsoleOutputCharacterW(hOut, ch, 3, (COORD){0, row}, NULL);
        }
        FillConsoleOutputCharacterW(hOut, L'^', 1, (COORD){0, base_y - 1}, NULL);
        FillConsoleOutputCharacterW(hOut, L'v', 1, (COORD){0, base_y + 6}, NULL);

        // Scroll rows base_y to base_y+3 down by 1 (dest is base_y+1)
        SMALL_RECT scroll_rect = {0, base_y, width - 1, base_y + 3};
        SMALL_RECT clip_rect = {0, base_y, width - 1, base_y + 4};
        CHAR_INFO fill = {.Char = {.UnicodeChar = L'+'}, .Attributes = 0x07};
        ScrollConsoleScreenBufferW(hOut, &scroll_rect, &clip_rect, (COORD){0, base_y + 1}, &fill);

        WCHAR ch;
        DWORD read;
        // Row above unchanged
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y - 1}, &read);
        CHECK(ch == L'^', "Partial scroll down: row above unchanged");

        // First row should be fill (vacated)
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y}, &read);
        CHECK(ch == L'+', "Partial scroll down: first row has fill");

        // Row base_y+1 should have M (from old row base_y)
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y + 1}, &read);
        CHECK(ch == L'M', "Partial scroll down: row 1 has content from old row 0");

        // Row below unchanged
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y + 6}, &read);
        CHECK(ch == L'v', "Partial scroll down: row below unchanged");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
