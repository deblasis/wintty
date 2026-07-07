// test_writeconsole_w_ctrl.c — Verify WriteConsoleW handles control characters correctly
// Control chars (CR, LF, BS, TAB) should affect cursor but NOT store characters in cell grid.

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
    WCHAR read_buf[16];
    DWORD read;

    // ===== Test 1: WriteConsoleW with LF moves Y only =====
    {
        SetConsoleCursorPosition(hOut, (COORD){5, 2});
        WriteConsoleW(hOut, L"AB\nCD", 5, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        // LF only moves Y down, doesn't reset X
        // 'A' at (5,2), 'B' at (6,2), LF → (6,3), 'C' at (7,3), 'D' at (8,3)
        CHECK(sbi.dwCursorPosition.X == 9 && sbi.dwCursorPosition.Y == 3,
              "WriteConsoleW LF: cursor at (9,3)");

        ReadConsoleOutputCharacterW(hOut, read_buf, 2, (COORD){5, 2}, &read);
        CHECK(read_buf[0] == L'A' && read_buf[1] == L'B',
              "WriteConsoleW LF: 'AB' at row 2 cols 5-6");
        ReadConsoleOutputCharacterW(hOut, read_buf, 2, (COORD){7, 3}, &read);
        CHECK(read_buf[0] == L'C' && read_buf[1] == L'D',
              "WriteConsoleW LF: 'CD' at row 3 cols 7-8");
    }

    // ===== Test 2: WriteConsoleW with CR resets X =====
    {
        SetConsoleCursorPosition(hOut, (COORD){0, 5});
        WriteConsoleW(hOut, L"HELLO\rXX", 8, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        // 'HELLO' at (0-4,5), CR → (0,5), 'XX' at (0-1,5)
        CHECK(sbi.dwCursorPosition.X == 2 && sbi.dwCursorPosition.Y == 5,
              "WriteConsoleW CR: cursor at (2,5)");

        ReadConsoleOutputCharacterW(hOut, read_buf, 5, (COORD){0, 5}, &read);
        CHECK(read_buf[0] == L'X' && read_buf[1] == L'X' && read_buf[2] == L'L',
              "WriteConsoleW CR: 'XXLLO' at row 5 (CR overwrites)");
    }

    // ===== Test 3: WriteConsoleW with backspace =====
    {
        SetConsoleCursorPosition(hOut, (COORD){0, 7});
        WriteConsoleW(hOut, L"AB\bC", 4, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        // 'A' at (0,7), 'B' at (1,7), BS → (1,7), 'C' at (1,7) overwrites B
        CHECK(sbi.dwCursorPosition.X == 2 && sbi.dwCursorPosition.Y == 7,
              "WriteConsoleW BS: cursor at (2,7)");

        ReadConsoleOutputCharacterW(hOut, read_buf, 3, (COORD){0, 7}, &read);
        CHECK(read_buf[0] == L'A' && read_buf[1] == L'C',
              "WriteConsoleW BS: 'AC' at row 7 (C overwrites B)");
    }

    // ===== Test 4: WriteConsoleW with TAB =====
    {
        SetConsoleCursorPosition(hOut, (COORD){3, 9});
        WriteConsoleW(hOut, L"\tX", 2, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        // TAB from col 3 → col 8, 'X' at col 8, cursor → col 9
        CHECK(sbi.dwCursorPosition.X == 9 && sbi.dwCursorPosition.Y == 9,
              "WriteConsoleW TAB: cursor at (9,9) from (3,9)+TAB+X");

        ReadConsoleOutputCharacterW(hOut, read_buf, 1, (COORD){8, 9}, &read);
        CHECK(read_buf[0] == L'X',
              "WriteConsoleW TAB: 'X' at col 8");
    }

    // ===== Test 5: CRLF combination =====
    {
        SetConsoleCursorPosition(hOut, (COORD){10, 11});
        WriteConsoleW(hOut, L"Hi\r\nXY", 6, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        // 'H' at (10,11), 'i' at (11,11), CR → (0,11), LF → (0,12), 'X' at (0,12), 'Y' at (1,12)
        CHECK(sbi.dwCursorPosition.X == 2 && sbi.dwCursorPosition.Y == 12,
              "WriteConsoleW CRLF: cursor at (2,12)");

        ReadConsoleOutputCharacterW(hOut, read_buf, 2, (COORD){10, 11}, &read);
        CHECK(read_buf[0] == L'H' && read_buf[1] == L'i',
              "WriteConsoleW CRLF: 'Hi' at row 11 cols 10-11");
        ReadConsoleOutputCharacterW(hOut, read_buf, 2, (COORD){0, 12}, &read);
        CHECK(read_buf[0] == L'X' && read_buf[1] == L'Y',
              "WriteConsoleW CRLF: 'XY' at row 12 cols 0-1");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
