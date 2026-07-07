#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_writefile_ascii(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 5};
    SetConsoleCursorPosition(hOut, pos);

    // Write ASCII via WriteFile (like Python sys.stdout.write does)
    const char *text = "ABCDE";
    DWORD written;
    WriteFile(hOut, text, 5, &written, NULL);

    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    if (info.dwCursorPosition.X == 5 && info.dwCursorPosition.Y == 5) {
        PASS("WriteFile ASCII: cursor at (5,5)");
    } else {
        FAIL("writefile_ascii", "wrong cursor position");
    }

    // Read back
    WCHAR buf[6] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 5, pos, &read);
    if (read == 5 && wcsncmp(buf, L"ABCDE", 5) == 0) {
        PASS("WriteFile ASCII: read back 'ABCDE'");
    } else {
        FAIL("writefile_ascii_read", "read-back mismatch");
    }
}

void test_writefile_utf8_cjk(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 6};
    SetConsoleCursorPosition(hOut, pos);

    // Write UTF-8 CJK text via WriteFile: 你好 (6 bytes in UTF-8)
    // 你 = E4 BD A0, 好 = E5 A5 BD
    const unsigned char text[] = {0xE4, 0xBD, 0xA0, 0xE5, 0xA5, 0xBD};
    DWORD written;
    WriteFile(hOut, (const char*)text, 6, &written, NULL);

    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    // 2 CJK chars × 2 cells = 4
    if (info.dwCursorPosition.X == 4 && info.dwCursorPosition.Y == 6) {
        PASS("WriteFile UTF-8 CJK: cursor at (4,6) — wide char handled");
    } else {
        // Currently: 6 raw bytes × 1 cell each = cursor at 6 (wrong)
        FAIL("writefile_utf8_cjk", "cursor position wrong (UTF-8 multibyte not decoded?)");
    }
}

void test_writefile_utf8_readback(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 7};
    SetConsoleCursorPosition(hOut, pos);

    // Write 你 (U+4F60) via WriteFile UTF-8
    const unsigned char text[] = {0xE4, 0xBD, 0xA0}; // 你 in UTF-8
    DWORD written;
    WriteFile(hOut, (const char*)text, 3, &written, NULL);

    // Read back via ReadConsoleOutputCharacterW
    WCHAR buf[4] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 2, pos, &read);
    // Should read back U+4F60 (你) in both cells (wide char occupies 2)
    if (buf[0] == 0x4F60) {
        PASS("WriteFile UTF-8 CJK: read back U+4F60 (你) correctly");
    } else {
        FAIL("writefile_utf8_readback", "cell grid has wrong codepoint");
    }
}

int main(void) {
    printf("=== WriteFile UTF-8 Multibyte Test ===\n\n"); fflush(stdout);
    test_writefile_ascii();
    test_writefile_utf8_cjk();
    test_writefile_utf8_readback();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
