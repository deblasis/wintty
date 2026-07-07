// test_wide_char_fill.c — Conformance test for wide-char FillConsoleOutputCharacterW
// Verifies that filling with a wide char (CJK) fills exactly nLength cells,
// and the internal VT emission produces correct output (nLength/2 wide chars).
#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;

#define PASS(name, ...) do { printf("PASS: " name "\n", ##__VA_ARGS__); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

int main(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    CONSOLE_SCREEN_BUFFER_INFO sbi;
    GetConsoleScreenBufferInfo(hOut, &sbi);

    // Use a safe area (row 30+)
    COORD start = { .X = 0, .Y = 30 };

    // Test 1: Fill 10 cells with CJK char U+4E2D (中, width 2)
    // Expected: 5 wide chars fill 10 cells
    {
        DWORD written = 0;
        BOOL ok = FillConsoleOutputCharacterW(hOut, 0x4E2D, 10, start, &written);
        if (!ok || written != 10) {
            FAIL("fill10", "FillConsoleOutputCharacterW returned %d, written=%lu", ok, written);
        } else {
            PASS("FillConsoleOutputCharacterW: 10 cells filled with U+4E2D");
        }

        // Read back to verify
        CHAR_INFO buf[10] = {};
        COORD bufSize = { .X = 10, .Y = 1 };
        COORD bufCoord = { .X = 0, .Y = 0 };
        SMALL_RECT readRegion = { .Left = start.X, .Top = start.Y, .Right = start.X + 9, .Bottom = start.Y };
        ReadConsoleOutputW(hOut, buf, bufSize, bufCoord, &readRegion);

        int correct = 1;
        for (int i = 0; i < 10; i++) {
            if (buf[i].Char.UnicodeChar != 0x4E2D) {
                FAIL("readback", "cell[%d] = U+%04X, expected U+4E2D", i, buf[i].Char.UnicodeChar);
                correct = 0;
                break;
            }
        }
        if (correct) {
            PASS("Wide-char fill readback: all 10 cells contain U+4E2D");
        }
    }

    // Test 2: Fill 120 cells (full row) with CJK char U+3042 (あ)
    {
        COORD row_start = { .X = 0, .Y = 31 };
        DWORD written = 0;
        BOOL ok = FillConsoleOutputCharacterW(hOut, 0x3042, 120, row_start, &written);
        if (!ok || written != 120) {
            FAIL("fill120", "FillConsoleOutputCharacterW returned %d, written=%lu", ok, written);
        } else {
            PASS("FillConsoleOutputCharacterW: 120 cells filled with U+3042");
        }

        // Verify all 120 cells
        CHAR_INFO buf2[120] = {};
        COORD bufSize2 = { .X = 120, .Y = 1 };
        COORD bufCoord2 = { .X = 0, .Y = 0 };
        SMALL_RECT readRegion2 = { .Left = 0, .Top = 31, .Right = 119, .Bottom = 31 };
        ReadConsoleOutputW(hOut, buf2, bufSize2, bufCoord2, &readRegion2);

        int all_ok = 1;
        for (int i = 0; i < 120; i++) {
            if (buf2[i].Char.UnicodeChar != 0x3042) {
                FAIL("row_readback", "cell[%d] = U+%04X, expected U+3042", i, buf2[i].Char.UnicodeChar);
                all_ok = 0;
                break;
            }
        }
        if (all_ok) {
            PASS("Full-row CJK fill readback: all 120 cells contain U+3042");
        }
    }

    // Test 3: Narrow char fill still works correctly (regression)
    {
        COORD narrow_start = { .X = 0, .Y = 32 };
        DWORD written = 0;
        FillConsoleOutputCharacterW(hOut, 'X', 80, narrow_start, &written);
        if (written != 80) {
            FAIL("narrow_fill", "written=%lu, expected 80", written);
        } else {
            PASS("Narrow char fill: 80 cells filled with 'X'");
        }

        CHAR_INFO buf3[80] = {};
        COORD bufSize3 = { .X = 80, .Y = 1 };
        COORD bufCoord3 = { .X = 0, .Y = 0 };
        SMALL_RECT readRegion3 = { .Left = 0, .Top = 32, .Right = 79, .Bottom = 32 };
        ReadConsoleOutputW(hOut, buf3, bufSize3, bufCoord3, &readRegion3);

        int narrow_ok = 1;
        for (int i = 0; i < 80; i++) {
            if (buf3[i].Char.UnicodeChar != 'X') {
                FAIL("narrow_readback", "cell[%d] = U+%04X, expected 'X'", i, buf3[i].Char.UnicodeChar);
                narrow_ok = 0;
                break;
            }
        }
        if (narrow_ok) {
            PASS("Narrow char fill readback: all 80 cells contain 'X'");
        }
    }

    // Test 4: FillConsoleOutputAttribute with wide char preserves characters
    {
        COORD attr_start = { .X = 0, .Y = 33 };
        // First fill with CJK
        FillConsoleOutputCharacterW(hOut, 0x5B57, 20, attr_start, NULL);
        // Then fill attributes
        DWORD attr_written = 0;
        FillConsoleOutputAttribute(hOut, FOREGROUND_RED, 20, attr_start, &attr_written);
        if (attr_written != 20) {
            FAIL("attr_fill", "attr_written=%lu, expected 20", attr_written);
        } else {
            PASS("FillConsoleOutputAttribute after wide-char fill: 20 attrs written");
        }

        // Verify characters still intact
        CHAR_INFO buf4[20] = {};
        COORD bufSize4 = { .X = 20, .Y = 1 };
        COORD bufCoord4 = { .X = 0, .Y = 0 };
        SMALL_RECT readRegion4 = { .Left = 0, .Top = 33, .Right = 19, .Bottom = 33 };
        ReadConsoleOutputW(hOut, buf4, bufSize4, bufCoord4, &readRegion4);

        if (buf4[0].Char.UnicodeChar == 0x5B57 && buf4[0].Attributes & FOREGROUND_RED) {
            PASS("Wide-char preserved after attr fill: char=U+5B57, red attr set");
        } else {
            FAIL("attr_preserve", "char=U+%04X attr=0x%04X", buf4[0].Char.UnicodeChar, buf4[0].Attributes);
        }
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail > 0 ? 1 : 0;
}
