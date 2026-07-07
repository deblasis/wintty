// test_unicode_width_extended.c — Verify comprehensive Unicode width coverage
// Tests that the new Unicode 16.0 East Asian Width lookup covers characters
// that the old hand-rolled charWidth() missed.

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

    // ===== Test 1: Emoji are width 2 (0x1F600 range) =====
    {
        // 😀 U+1F600 via surrogate pair
        WCHAR emoji[] = { 0xD83D, 0xDE00, 0 };
        COORD pos = {0, base_y};
        SetConsoleCursorPosition(hOut, pos);
        DWORD written;
        WriteConsoleW(hOut, emoji, 2, &written, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        // Emoji is width 2, so cursor should be at X=2
        CHECK(sbi.dwCursorPosition.X == 2,
              "Emoji width 2: cursor at X=2 after U+1F600 (surrogate pair)");
    }

    // ===== Test 2: More emoji from extended range =====
    {
        // 🎉 U+1F389 (party popper) via surrogate pair
        WCHAR emoji[] = { 0xD83C, 0xDF89, 0 }; // 🎉
        COORD pos = {0, base_y + 1};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, emoji, 2, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 2,
              "Emoji width 2: cursor at X=2 after U+1F389 (party popper)");
    }

    // ===== Test 3: Chess symbol (Ambiguous width = 1, NOT Wide) =====
    // U+2656 (♖) is East Asian Ambiguous, not Wide — treated as width 1
    {
        WCHAR ch = 0x2656; // ♖
        COORD pos = {0, base_y + 2};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, &ch, 1, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 1,
              "Chess symbol (Ambiguous) width 1: cursor at X=1 after U+2656");
    }

    // ===== Test 4: CJK Extension B (supplementary plane, U+20000+) =====
    {
        // 𠀀 U+20000 via surrogate pair: D840 DC00
        WCHAR extB[] = { 0xD840, 0xDC00, 0 };
        COORD pos = {0, base_y + 3};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, extB, 2, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 2,
              "CJK Extension B width 2: cursor at X=2 after U+20000");
    }

    // ===== Test 5: Fullwidth Latin (U+FF21 = Ａ) =====
    {
        WCHAR ch = 0xFF21; // Ａ Fullwidth Latin capital A
        COORD pos = {0, base_y + 4};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, &ch, 1, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 2,
              "Fullwidth Latin width 2: cursor at X=2 after U+FF21");
    }

    // ===== Test 6: Combining accent (width 0) does not advance cursor =====
    // Note: in our implementation, combining marks ARE stored in cell grid
    // but advance cursor by 0. So A+combining = cursor at X=1, but 2 cells used.
    // The cursor only advances by charWidth(A)=1 + charWidth(0x0301)=0 = 1.
    {
        // A + combining acute accent (U+0301)
        WCHAR text[] = { L'A', 0x0301, 0 };
        COORD pos = {0, base_y + 5};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, text, 2, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        // A is width 1, combining accent is width 0 → cursor at X=1
        CHECK(sbi.dwCursorPosition.X == 1,
              "Combining accent width 0: cursor at X=1 after A + U+0301");
    }

    // ===== Test 7: Variation selector (width 0) does not advance cursor =====
    {
        // Heart U+2764 (width 1, Ambiguous) + VS16 U+FE0F (width 0)
        // Total cursor advance = 1
        WCHAR text[] = { 0x2764, 0xFE0F, 0 };
        COORD pos = {0, base_y + 6};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, text, 2, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        // Heart is width 1 (Ambiguous), VS16 is width 0 → cursor at X=1
        CHECK(sbi.dwCursorPosition.X == 1,
              "Variation selector width 0: cursor at X=1 after U+2764 + U+FE0F");
    }

    // ===== Test 8: Mixed emoji and ASCII =====
    {
        // "A" + 😀 + "B" = 1 + 2 + 1 = 4 cells
        WCHAR text[] = { L'A', 0xD83D, 0xDE00, L'B', 0 };
        COORD pos = {0, base_y + 7};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, text, 4, NULL, NULL);

        GetConsoleScreenBufferInfo(hOut, &sbi);
        CHECK(sbi.dwCursorPosition.X == 4,
              "Mixed ASCII+emoji: cursor at X=4 after A+emoji+B");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
