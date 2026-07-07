// test_vt_csi_intermediate.c — Verify VT CSI sequences with intermediate bytes are fully skipped
// Bug: CSI parsing skipped intermediates (0x20-0x2F) before parameters (0x30-0x3F).
// Real VT order is: params first, then intermediates, then final.
// Sequences like CSI 1 SP q (DECSCUSR) had intermediates not skipped.

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

    // ===== Test 1: CSI with intermediate byte (DECSCUSR) via WriteFile =====
    {
        // Write "AB" then DECSCUSR sequence (\e[1 q = set cursor to blinking block) then "CD"
        // The cell grid should have "AB" at columns 0-1 and "CD" at columns 2-3
        // Without the fix, ' ' and 'q' from the sequence would appear in the cell grid
        COORD pos = {0, base_y};
        SetConsoleCursorPosition(hOut, pos);

        const char *data = "AB\x1b[1 qCD";
        DWORD written;
        WriteFile(hOut, data, 10, &written, NULL);

        // Read back cell grid
        WCHAR buf[6];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 4, (COORD){0, base_y}, &read);
        CHECK(buf[0] == L'A' && buf[1] == L'B' && buf[2] == L'C' && buf[3] == L'D',
              "WriteFile CSI with intermediate: ABCD without garbage from escape");
    }

    // ===== Test 2: CSI with multiple intermediates via WriteConsoleA =====
    {
        COORD pos = {0, base_y + 1};
        SetConsoleCursorPosition(hOut, pos);

        // WriteConsoleA with a CSI sequence containing intermediate byte
        const char *data = "XY\x1b[?25hZW";  // DECTCEM (show cursor) — ? is parameter
        DWORD written;
        WriteConsoleA(hOut, data, 10, &written, NULL);

        WCHAR buf[6];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 4, (COORD){0, base_y + 1}, &read);
        CHECK(buf[0] == L'X' && buf[1] == L'Y' && buf[2] == L'Z' && buf[3] == L'W',
              "WriteConsoleA CSI with ? param: XYZW without garbage");
    }

    // ===== Test 3: OSC sequence doesn't corrupt cell grid =====
    {
        COORD pos = {0, base_y + 2};
        SetConsoleCursorPosition(hOut, pos);

        // OSC sequence: \e]0;title\a then text
        const char *data = "MN\x1b]0;test\aPQ";
        DWORD written;
        WriteFile(hOut, data, 14, &written, NULL);

        WCHAR buf[6];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 4, (COORD){0, base_y + 2}, &read);
        CHECK(buf[0] == L'M' && buf[1] == L'N' && buf[2] == L'P' && buf[3] == L'Q',
              "WriteFile OSC sequence: MNPQ without garbage");
    }

    // ===== Test 4: ESC followed by single char doesn't corrupt cell grid =====
    {
        COORD pos = {0, base_y + 3};
        SetConsoleCursorPosition(hOut, pos);

        // ESC 7 (DECSC - save cursor) and ESC 8 (DECRC - restore cursor)
        const char *data = "EF" "\x1b" "7" "\x1b" "8" "GH";
        DWORD written;
        WriteFile(hOut, data, 8, &written, NULL);

        WCHAR buf[6];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 4, (COORD){0, base_y + 3}, &read);
        CHECK(buf[0] == L'E' && buf[1] == L'F',
              "WriteFile ESC+char: EF preserved before escape");
        CHECK(buf[2] == L'G' && buf[3] == L'H',
              "WriteFile ESC+char: GH after escape");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
