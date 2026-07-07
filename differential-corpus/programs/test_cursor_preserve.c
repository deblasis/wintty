// test_cursor_preserve.c — Verify cursor position is preserved after Fill/Output operations
// In real Windows console, FillConsoleOutputCharacterW, FillConsoleOutputAttribute,
// WriteConsoleOutputW, and WriteConsoleOutputAttribute do NOT move the cursor.

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

    // Set a known cursor position
    COORD initial_pos = {20, 5};
    SetConsoleCursorPosition(hOut, initial_pos);

    // ===== Test 1: FillConsoleOutputCharacterW preserves cursor =====
    {
        COORD fill_pos = {0, 0};
        DWORD written;
        FillConsoleOutputCharacterW(hOut, L'X', 10, fill_pos, &written);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == initial_pos.X &&
              sbi.dwCursorPosition.Y == initial_pos.Y,
              "FillConsoleOutputCharacterW preserves cursor position");
    }

    // ===== Test 2: FillConsoleOutputAttribute preserves cursor =====
    {
        COORD fill_pos = {0, 1};
        DWORD written;
        FillConsoleOutputAttribute(hOut, 0x0C, 10, fill_pos, &written);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == initial_pos.X &&
              sbi.dwCursorPosition.Y == initial_pos.Y,
              "FillConsoleOutputAttribute preserves cursor position");
    }

    // ===== Test 3: WriteConsoleOutputW preserves cursor =====
    {
        // Prepare buffer
        CHAR_INFO ci[5];
        for (int i = 0; i < 5; i++) {
            ci[i].Char.UnicodeChar = L'A' + i;
            ci[i].Attributes = 0x07;
        }
        SMALL_RECT write_region = {0, 2, 4, 2};
        COORD buf_size = {5, 1};
        COORD buf_coord = {0, 0};
        WriteConsoleOutputW(hOut, ci, buf_size, buf_coord, &write_region);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == initial_pos.X &&
              sbi.dwCursorPosition.Y == initial_pos.Y,
              "WriteConsoleOutputW preserves cursor position");
    }

    // ===== Test 4: WriteConsoleOutputAttribute preserves cursor =====
    {
        WORD attrs[] = {0x0C, 0x0A, 0x09, 0x0E, 0x0D};
        DWORD written;
        WriteConsoleOutputAttribute(hOut, attrs, 5, (COORD){0, 3}, &written);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == initial_pos.X &&
              sbi.dwCursorPosition.Y == initial_pos.Y,
              "WriteConsoleOutputAttribute preserves cursor position");
    }

    // ===== Test 5: Multiple Fill operations preserve cursor =====
    {
        DWORD written;
        FillConsoleOutputCharacterW(hOut, L'Y', 30, (COORD){0, 6}, &written);
        FillConsoleOutputAttribute(hOut, 0x0A, 30, (COORD){0, 6}, &written);
        FillConsoleOutputCharacterW(hOut, L'Z', 15, (COORD){10, 7}, &written);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == initial_pos.X &&
              sbi.dwCursorPosition.Y == initial_pos.Y,
              "Multiple Fill operations preserve cursor position");
    }

    // ===== Test 6: ScrollConsoleScreenBufferW preserves cursor =====
    {
        // Write some data first
        DWORD written;
        FillConsoleOutputCharacterW(hOut, L'S', 20, (COORD){0, 8}, &written);
        FillConsoleOutputAttribute(hOut, 0x07, 20, (COORD){0, 8}, &written);

        SMALL_RECT scroll_rect = {0, 8, 19, 9};
        CHAR_INFO fill = {.Char.UnicodeChar = L' ', .Attributes = 0x07};
        ScrollConsoleScreenBufferW(hOut, &scroll_rect, NULL, (COORD){0, 10}, &fill);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == initial_pos.X &&
              sbi.dwCursorPosition.Y == initial_pos.Y,
              "ScrollConsoleScreenBufferW preserves cursor position");
    }

    // ===== Test 7: Full-width scroll preserves cursor =====
    {
        DWORD written;
        FillConsoleOutputCharacterW(hOut, L'T', 80, (COORD){0, 12}, &written);
        FillConsoleOutputAttribute(hOut, 0x07, 80, (COORD){0, 12}, &written);

        SMALL_RECT scroll_rect = {0, 12, 79, 13};
        CHAR_INFO fill = {.Char.UnicodeChar = L' ', .Attributes = 0x07};
        ScrollConsoleScreenBufferW(hOut, &scroll_rect, NULL, (COORD){0, 14}, &fill);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == initial_pos.X &&
              sbi.dwCursorPosition.Y == initial_pos.Y,
              "Full-width scroll preserves cursor position");
    }

    // ===== Test 8: WriteConsoleOutputCharacterW preserves cursor =====
    {
        DWORD written;
        WriteConsoleOutputCharacterW(hOut, L"HELLO", 5, (COORD){0, 15}, &written);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == initial_pos.X &&
              sbi.dwCursorPosition.Y == initial_pos.Y,
              "WriteConsoleOutputCharacterW preserves cursor position");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
