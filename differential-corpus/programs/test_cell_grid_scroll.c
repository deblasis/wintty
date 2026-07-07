// test_cell_grid_scroll.c — Verify cell grid scrolls when cursor goes past the bottom
// When cursor_position.Y >= screen_buffer_size.Y, the grid should scroll up by one row.

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
    WCHAR read_buf[80];
    DWORD read;

    // Get current buffer size
    GetConsoleScreenBufferInfo(hOut, &sbi);
    int buf_height = sbi.dwSize.Y;
    int buf_width = sbi.dwSize.X;
    int last_row = buf_height - 1;

    // ===== Test 1: WriteConsoleW scrolls on LF at bottom =====
    {
        // Write unique content at bottom-2, bottom-1, bottom
        SetConsoleCursorPosition(hOut, (COORD){0, last_row - 2});
        WriteConsoleW(hOut, L"AAA", 3, NULL, NULL);
        SetConsoleCursorPosition(hOut, (COORD){0, last_row - 1});
        WriteConsoleW(hOut, L"BBB", 3, NULL, NULL);
        SetConsoleCursorPosition(hOut, (COORD){0, last_row});
        WriteConsoleW(hOut, L"CCC\n", 4, NULL, NULL);
        // After LF: grid scrolled up. row[bottom-2]="BBB", row[bottom-1]="CCC", row[bottom]=spaces

        // Read BEFORE any printf (printf would overwrite cells)
        WCHAR row_bm2[4], row_bm1[4], row_bot[4];
        ReadConsoleOutputCharacterW(hOut, row_bm2, 3, (COORD){0, last_row - 2}, &read);
        ReadConsoleOutputCharacterW(hOut, row_bm1, 3, (COORD){0, last_row - 1}, &read);
        ReadConsoleOutputCharacterW(hOut, row_bot, 3, (COORD){0, last_row}, &read);

        CHECK(row_bm2[0] == L'B' && row_bm2[1] == L'B' && row_bm2[2] == L'B',
              "Cell grid scroll: rows shift up after LF at bottom");
        CHECK(row_bm1[0] == L'C' && row_bm1[1] == L'C' && row_bm1[2] == L'C',
              "Cell grid scroll: bottom row moved up after scroll");
        CHECK(row_bot[0] == L' ',
              "Cell grid scroll: new bottom row is empty after scroll");
    }

    // ===== Test 2: WriteConsoleW wraps and scrolls at bottom =====
    {
        FillConsoleOutputCharacterW(hOut, L' ', buf_width, (COORD){0, last_row}, &read);
        SetConsoleCursorPosition(hOut, (COORD){buf_width - 2, last_row});
        WriteConsoleW(hOut, L"XXYY", 4, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.Y == last_row,
              "Cell grid scroll: cursor stays at bottom after wrap scroll");
    }

    // ===== Test 3: WriteFile scrolls at bottom =====
    {
        FillConsoleOutputCharacterW(hOut, L' ', buf_width * 2, (COORD){0, last_row - 1}, &read);
        SetConsoleCursorPosition(hOut, (COORD){0, last_row});
        char line[256];
        memset(line, 'X', buf_width);
        line[buf_width] = '\n';
        line[buf_width + 1] = 'Z';
        DWORD written;
        WriteFile(hOut, line, buf_width + 2, &written, NULL);

        ReadConsoleOutputCharacterW(hOut, read_buf, 1, (COORD){0, last_row}, &read);
        CHECK(read_buf[0] == L'Z',
              "Cell grid scroll: WriteFile LF at bottom scrolls grid");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
