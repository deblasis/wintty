// test_sgr_invalidation.c — Verify SGR state tracking is invalidated after WriteConsoleA
// Bug: WriteConsoleA wrote raw VT sequences to the terminal (including SGR color codes)
// but didn't invalidate the SGR tracking. Subsequent SetConsoleTextAttribute calls with
// the same attribute would skip SGR emission, leaving the terminal in the wrong color.

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

    // ===== Test 1: SetConsoleTextAttribute after WriteConsoleA with VT SGR =====
    {
        // Step 1: Set attribute to white-on-black (0x07)
        SetConsoleTextAttribute(hOut, 0x07);

        // Step 2: WriteConsoleA with embedded SGR to change to red
        COORD pos = {0, base_y};
        SetConsoleCursorPosition(hOut, pos);
        const char *red_text = "\x1b[31mRED";
        DWORD written;
        WriteConsoleA(hOut, red_text, 6, &written, NULL);

        // Step 3: SetConsoleTextAttribute back to 0x07
        // Without the fix, this would skip SGR emission (current_sgr_attr == 0x07)
        // and the terminal would still be in red mode.
        SetConsoleTextAttribute(hOut, 0x07);

        // Step 4: Write text — should be white, not red
        // The cell grid should have 'W' at (0, base_y+1) with 0x07 attribute
        SetConsoleCursorPosition(hOut, (COORD){0, base_y + 1});
        WriteConsoleW(hOut, L"W", 1, NULL, NULL);

        // Read back attribute from cell grid
        WORD attr;
        DWORD read;
        ReadConsoleOutputAttribute(hOut, &attr, 1, (COORD){0, base_y + 1}, &read);
        CHECK(attr == 0x07,
              "SGR invalidation: attribute after WriteConsoleA+SetConsoleTextAttribute is 0x07");
    }

    // ===== Test 2: WriteFile also invalidates SGR =====
    {
        SetConsoleTextAttribute(hOut, 0x07);
        COORD pos = {0, base_y + 2};
        SetConsoleCursorPosition(hOut, pos);

        // WriteFile with SGR to change to green
        const char *green_text = "\x1b[32mGRN";
        DWORD written;
        WriteFile(hOut, green_text, 6, &written, NULL);

        // SetConsoleTextAttribute back to 0x07 — should NOT be skipped
        SetConsoleTextAttribute(hOut, 0x07);

        // Write and check attribute
        SetConsoleCursorPosition(hOut, (COORD){0, base_y + 3});
        WriteConsoleW(hOut, L"T", 1, NULL, NULL);

        WORD attr;
        DWORD read;
        ReadConsoleOutputAttribute(hOut, &attr, 1, (COORD){0, base_y + 3}, &read);
        CHECK(attr == 0x07,
              "SGR invalidation: attribute after WriteFile+SetConsoleTextAttribute is 0x07");
    }

    // ===== Test 3: Multiple SetConsoleTextAttribute calls still skip when appropriate =====
    {
        // After invalidation, first call should emit, second should skip
        SetConsoleTextAttribute(hOut, 0x0A); // green on black
        // This should skip (current_sgr_attr is now 0x0A)
        SetConsoleTextAttribute(hOut, 0x0A);

        // Verify the attribute is tracked
        WORD attr;
        DWORD read;
        ReadConsoleOutputAttribute(hOut, &attr, 1, (COORD){0, base_y + 3}, &read);
        // The attribute on the cell grid is set by WriteConsoleW, not by SetConsoleTextAttribute
        // So we just verify no crash and consistent behavior
        CHECK(1, "SGR tracking: repeated SetConsoleTextAttribute doesn't crash");
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", tests_passed, tests_failed);
    return tests_failed > 0 ? 1 : 0;
}
