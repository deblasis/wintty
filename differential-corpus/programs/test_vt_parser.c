// test_vt_parser.c — Verify VT500 parser via WriteFile VT sequence handling
// Tests that the VT parser in our DLL correctly handles various VT sequences
// when programs write them via WriteFile to a console handle.

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

    // ===== Test 1: CSI SGR (color) sequence =====
    {
        COORD pos = {0, base_y};
        SetConsoleCursorPosition(hOut, pos);
        // Write: A, then CSI red SGR, B, then CSI reset, C
        const char *data = "A\x1b[31mB\x1b[0mC";
        DWORD written;
        WriteFile(hOut, data, 13, &written, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 3,
              "VT parser: cursor at X=3 after A+SGR_RED+B+SGR_RESET+C");

        // Read back: A, B, C should be at positions 0, 1, 2
        WCHAR buf[4];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 3, (COORD){0, base_y}, &read);
        CHECK(buf[0] == L'A',
              "VT parser: 'A' at position 0");
        CHECK(buf[1] == L'B',
              "VT parser: 'B' at position 1 (SGR skipped in cell grid)");
        CHECK(buf[2] == L'C',
              "VT parser: 'C' at position 2 (SGR reset skipped in cell grid)");
    }

    // ===== Test 2: CSI cursor movement (now tracked via interpretCSI) =====
    // CSI 1D (CUB = cursor back 1) moves internal cursor from X=2 to X=1.
    {
        COORD pos = {0, base_y + 1};
        SetConsoleCursorPosition(hOut, pos);
        const char *data = "AB\x1b[1D"; // A, B, cursor left 1
        DWORD written;
        WriteFile(hOut, data, 6, &written, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        // CSI 1D now moves internal cursor back from X=2 to X=1
        CHECK(sbi.dwCursorPosition.X == 1,
              "VT parser: cursor at X=1 after AB + CSI 1D (cursor back tracked)");
    }

    // ===== Test 3: OSC title sequence =====
    {
        COORD pos = {0, base_y + 2};
        SetConsoleCursorPosition(hOut, pos);
        // OSC 0;title BEL
        const char *data = "X\x1b]0;test-title\x07Y";
        DWORD written;
        WriteFile(hOut, data, 20, &written, NULL);

        // X and Y should be in the cell grid (OSC data skipped)
        WCHAR buf[3];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 2, (COORD){0, base_y + 2}, &read);
        CHECK(buf[0] == L'X',
              "VT parser: 'X' before OSC");
        CHECK(buf[1] == L'Y',
              "VT parser: 'Y' after OSC");
    }

    // ===== Test 4: ESC single-char sequences (DECSC/DECRC) =====
    // ESC 7/8 are dispatched but cursor save/restore is not tracked in cell grid.
    // Verify that Z is written at the expected position after ESC sequence processing.
    {
        COORD pos = {5, base_y + 3};
        SetConsoleCursorPosition(hOut, pos);
        // ESC 7 = DECSC (save cursor), ESC 8 = DECRC (restore cursor)
        const char *data = "\x1b" "7" "\x1b" "[2;2H" "\x1b" "8Z";
        DWORD written;
        WriteFile(hOut, data, 11, &written, NULL);

        // Z is written at (5, base_y+3) by our cell grid (internal cursor
        // stays at the position set by SetConsoleCursorPosition since
        // ESC/CSI sequences don't update internal cursor tracking).
        WCHAR ch;
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){5, base_y + 3}, &read);
        CHECK(ch == L'Z',
              "VT parser: Z written at expected position after ESC 7/ESC 8");
    }

    // ===== Test 5: CSI with intermediate byte (DECSCUSR) =====
    {
        COORD pos = {0, base_y + 4};
        SetConsoleCursorPosition(hOut, pos);
        // CSI 1 SP q = DECSCUSR (set cursor style) — has intermediate byte
        const char *data = "AB\x1b[1 qCD";
        DWORD written;
        WriteFile(hOut, data, 11, &written, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 4,
              "VT parser: cursor at X=4 after AB + CSI 1 SP q + CD");

        WCHAR buf[5];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 4, (COORD){0, base_y + 4}, &read);
        CHECK(buf[0] == L'A' && buf[1] == L'B' && buf[2] == L'C' && buf[3] == L'D',
              "VT parser: ABCD correct after CSI with intermediate");
    }

    // ===== Test 6: Multiple CSI sequences in sequence =====
    {
        COORD pos = {0, base_y + 5};
        SetConsoleCursorPosition(hOut, pos);
        // SGR red, SGR green, SGR reset — all should be skipped in cell grid
        const char *data = "\x1b[31m\x1b[32m\x1b[0mX";
        DWORD written;
        WriteFile(hOut, data, 15, &written, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 1,
              "VT parser: cursor at X=1 after 3 SGR sequences + X");

        WCHAR ch;
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y + 5}, &read);
        CHECK(ch == L'X',
              "VT parser: X after multiple SGR sequences");
    }

    // ===== Test 7: Mixed printable and control =====
    {
        COORD pos = {0, base_y + 6};
        SetConsoleCursorPosition(hOut, pos);
        // Tab is a control character (0x09)
        const char *data = "A\tB";
        DWORD written;
        WriteFile(hOut, data, 3, &written, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        // Tab advances to next 8-column boundary: A at 0, tab to 8, B at 8
        CHECK(sbi.dwCursorPosition.X == 9,
              "VT parser: cursor at X=9 after A + TAB + B");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
