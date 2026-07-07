// test_surrogate_pairs.c — Verify WriteConsoleW handles UTF-16 surrogate pairs
// Bug: Each WCHAR of a surrogate pair was independently transcoded to UTF-8.
// Surrogates are not valid Unicode code points, so utf8Encode returned an error
// and the character was silently dropped. Emoji and other supplementary characters
// (code points > U+FFFF) were invisible.

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

    // ===== Test 1: Basic emoji via surrogate pair =====
    {
        // 😀 = U+1F600, encoded as surrogate pair: 0xD83D 0xDE00
        WCHAR emoji[] = { 0xD83D, 0xDE00, 0 }; // 😀
        COORD pos = {0, base_y};
        SetConsoleCursorPosition(hOut, pos);
        DWORD written;
        WriteConsoleW(hOut, emoji, 2, &written, NULL);
        CHECK(written == 2, "Surrogate pair: WriteConsoleW returns 2");

        // Cell grid should have the high surrogate stored
        WCHAR ch;
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y}, &read);
        CHECK(ch == 0xD83D,
              "Surrogate pair: high surrogate stored in cell grid");
    }

    // ===== Test 2: Mixed ASCII and emoji =====
    {
        // "A" + 😀 + "B" = 1 + 2 + 1 = 4 cells
        WCHAR mixed[] = { L'A', 0xD83D, 0xDE00, L'B', 0 };
        COORD pos = {0, base_y + 1};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, mixed, 4, NULL, NULL);

        WCHAR buf[5];
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, buf, 4, (COORD){0, base_y + 1}, &read);
        CHECK(buf[0] == L'A',
              "Mixed ASCII+emoji: A before emoji");
        // Emoji takes 2 cells (buf[1] and buf[2] both contain high surrogate)
        CHECK(buf[1] == 0xD83D,
              "Mixed ASCII+emoji: emoji cell 1 has high surrogate");
        CHECK(buf[3] == L'B',
              "Mixed ASCII+emoji: B after emoji at cell 3");
    }

    // ===== Test 3: Lone high surrogate (no low surrogate) =====
    {
        WCHAR lone[] = { 0xD83D, L'X', 0 }; // High surrogate without low
        COORD pos = {0, base_y + 2};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, lone, 2, NULL, NULL);

        // Lone surrogate should be skipped, X should be at position 0
        WCHAR ch;
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y + 2}, &read);
        CHECK(ch == L'X',
              "Lone high surrogate: skipped, X at position 0");
    }

    // ===== Test 4: Lone low surrogate =====
    {
        WCHAR lone_low[] = { 0xDE00, L'Y', 0 }; // Low surrogate without high
        COORD pos = {0, base_y + 3};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, lone_low, 2, NULL, NULL);

        // Lone low surrogate should be skipped, Y should be at position 0
        WCHAR ch;
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y + 3}, &read);
        CHECK(ch == L'Y',
              "Lone low surrogate: skipped, Y at position 0");
    }

    // ===== Test 5: Multiple surrogate pairs =====
    {
        // 😀😃 = two emoji (4 WCHARs)
        WCHAR two_emoji[] = { 0xD83D, 0xDE00, 0xD83D, 0xDE03, 0 };
        COORD pos = {0, base_y + 4};
        SetConsoleCursorPosition(hOut, pos);
        WriteConsoleW(hOut, two_emoji, 4, NULL, NULL);

        // First cell should have first high surrogate
        WCHAR ch;
        DWORD read;
        ReadConsoleOutputCharacterW(hOut, &ch, 1, (COORD){0, base_y + 4}, &read);
        CHECK(ch == 0xD83D,
              "Multiple surrogate pairs: first emoji in cell grid");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
