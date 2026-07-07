#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;

#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)
#define CHECK(cond, name) do { if (cond) { PASS(name); } else { FAIL(name, #cond); } } while(0)

int main(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO sbi;
    SHORT base_y = 20; // Use a row far enough down
    DWORD written;

    printf("=== Cursor Movement VT Sequence Test ===\n\n"); fflush(stdout);

    // ===== Test 1: CSI A (CUU - Cursor Up) =====
    {
        COORD pos = {5, base_y};
        SetConsoleCursorPosition(hOut, pos);
        // AB + CSI 2A = cursor up 2. Bytes: 41 42 1B 5B 32 41 = 6 bytes
        const char *data = "AB\x1b[2A";
        WriteFile(hOut, data, 6, &written, NULL);
        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 7 && sbi.dwCursorPosition.Y == base_y - 2,
              "CSI 2A: cursor up 2 rows");
    }

    // ===== Test 2: CSI B (CUD - Cursor Down) =====
    {
        COORD pos = {3, base_y};
        SetConsoleCursorPosition(hOut, pos);
        // X + CSI 3B = cursor down 3. Bytes: 58 1B 5B 33 42 = 5 bytes
        const char *data = "X\x1b[3B";
        WriteFile(hOut, data, 5, &written, NULL);
        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 4 && sbi.dwCursorPosition.Y == base_y + 3,
              "CSI 3B: cursor down 3 rows");
    }

    // ===== Test 3: CSI C (CUF - Cursor Forward) =====
    {
        COORD pos = {0, base_y + 1};
        SetConsoleCursorPosition(hOut, pos);
        // AB + CSI 5C = cursor forward 5. Bytes: 41 42 1B 5B 35 43 = 6 bytes
        const char *data = "AB\x1b[5C";
        WriteFile(hOut, data, 6, &written, NULL);
        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 7 && sbi.dwCursorPosition.Y == base_y + 1,
              "CSI 5C: cursor forward 5 columns");
    }

    // ===== Test 4: CSI D (CUB - Cursor Back) =====
    {
        COORD pos = {0, base_y + 2};
        SetConsoleCursorPosition(hOut, pos);
        // ABCDE + CSI 3D = cursor back 3. Bytes: 9 bytes
        const char *data = "ABCDE\x1b[3D";
        WriteFile(hOut, data, 9, &written, NULL);
        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 2 && sbi.dwCursorPosition.Y == base_y + 2,
              "CSI 3D: cursor back 3 columns");
    }

    // ===== Test 5: CSI H (CUP - Cursor Position) =====
    {
        COORD pos = {0, base_y};
        SetConsoleCursorPosition(hOut, pos);
        // Hello + CSI 3;5H + X = 12 bytes
        const char *data = "Hello\x1b[3;5HX";
        WriteFile(hOut, data, 12, &written, NULL);
        GetConsoleScreenBufferInfo(hOut, &sbi);
        // CUP 3;5H → 0-indexed (4, 2), then X → cursor at (5, 2)
        CHECK(sbi.dwCursorPosition.X == 5 && sbi.dwCursorPosition.Y == 2,
              "CSI 3;5H + X: cursor at (5, 2) after CUP to (4,2) then X");
    }

    // ===== Test 6: CSI G (CHA - Cursor Horizontal Absolute) =====
    {
        COORD pos = {0, base_y + 3};
        SetConsoleCursorPosition(hOut, pos);
        // ABCD + CSI 10G = 9 bytes
        const char *data = "ABCD\x1b[10G";
        WriteFile(hOut, data, 9, &written, NULL);
        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 9 && sbi.dwCursorPosition.Y == base_y + 3,
              "CSI 10G: cursor at column 9 (0-indexed)");
    }

    // ===== Test 7: CSI d (VPA - Vertical Position Absolute) =====
    {
        COORD pos = {5, base_y};
        SetConsoleCursorPosition(hOut, pos);
        // X + CSI 15d = 6 bytes
        const char *data = "X\x1b[15d";
        WriteFile(hOut, data, 6, &written, NULL);
        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 6 && sbi.dwCursorPosition.Y == 14,
              "CSI 15d: cursor at row 14, X advanced to 6 (0-indexed)");
    }

    // ===== Test 8: ESC 7 / ESC 8 (DECSC/DECRC) =====
    {
        COORD pos = {7, base_y + 4};
        SetConsoleCursorPosition(hOut, pos);
        // ESC 7 (save), CSI 1;1H (move to 0,0), ESC 8 (restore), Z
        const char *data = "\x1b" "7" "\x1b" "[1;1H" "\x1b" "8" "Z";
        WriteFile(hOut, data, 11, &written, NULL);
        GetConsoleScreenBufferInfo(hOut, &sbi);
        // Cursor restored to (7, base_y+4), then Z → (8, base_y+4)
        CHECK(sbi.dwCursorPosition.X == 8 && sbi.dwCursorPosition.Y == base_y + 4,
              "ESC 7/8: cursor restored to saved position after CUP detour");
    }

    // ===== Test 9: CSI K (EL - Erase in Line) =====
    {
        COORD pos = {0, base_y + 5};
        SetConsoleCursorPosition(hOut, pos);
        WriteFile(hOut, "ABCDE", 5, &written, NULL);
        COORD erase_pos = {2, base_y + 5};
        SetConsoleCursorPosition(hOut, erase_pos);
        WriteFile(hOut, "\x1b[K", 3, &written, NULL);
        WCHAR buf[5];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 5, pos, &read);
        CHECK(buf[0] == L'A' && buf[1] == L'B' && buf[2] == L' ' && buf[3] == L' ' && buf[4] == L' ',
              "CSI K: EL(0) erases from cursor to end of line");
    }

    // ===== Test 10: CSI J (ED - Erase in Display) =====
    {
        COORD pos = {0, base_y + 6};
        SetConsoleCursorPosition(hOut, pos);
        WriteFile(hOut, "ABCDEF", 6, &written, NULL);
        COORD pos2 = {3, base_y + 6};
        SetConsoleCursorPosition(hOut, pos2);
        WriteFile(hOut, "\x1b[0J", 4, &written, NULL);
        WCHAR buf[6];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 6, (COORD){0, base_y + 6}, &read);
        CHECK(buf[0] == L'A' && buf[1] == L'B' && buf[2] == L'C' && buf[3] == L' ' && buf[4] == L' ' && buf[5] == L' ',
              "CSI 0J: ED(0) erases from cursor to end of display");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
