#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_writeconsole_a_ascii(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 5};
    SetConsoleCursorPosition(hOut, pos);

    const char *text = "Hello";
    DWORD written;
    WriteConsoleA(hOut, text, 5, &written, NULL);

    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    if (info.dwCursorPosition.X == 5 && info.dwCursorPosition.Y == 5) {
        PASS("WriteConsoleA ASCII: cursor at (5,5)");
    } else {
        FAIL("writeconsole_a_ascii", "wrong cursor");
    }
}

void test_writeconsole_a_utf8_cjk(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 6};
    SetConsoleCursorPosition(hOut, pos);

    // WriteConsoleA with UTF-8 CJK: 你好 (6 bytes)
    const unsigned char text[] = {0xE4, 0xBD, 0xA0, 0xE5, 0xA5, 0xBD};
    DWORD written;
    WriteConsoleA(hOut, (const char*)text, 6, &written, NULL);

    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    // 2 CJK chars × 2 cells = 4
    if (info.dwCursorPosition.X == 4 && info.dwCursorPosition.Y == 6) {
        PASS("WriteConsoleA UTF-8 CJK: cursor at (4,6)");
    } else {
        FAIL("writeconsole_a_utf8", "cursor wrong (UTF-8 not decoded?)");
    }
}

void test_writeconsole_a_readback(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 7};
    SetConsoleCursorPosition(hOut, pos);

    // Write 你 (U+4F60) via WriteConsoleA UTF-8
    const unsigned char text[] = {0xE4, 0xBD, 0xA0};
    DWORD written;
    WriteConsoleA(hOut, (const char*)text, 3, &written, NULL);

    // Read back via ReadConsoleOutputCharacterW
    WCHAR buf[4] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 2, pos, &read);
    if (buf[0] == 0x4F60) {
        PASS("WriteConsoleA UTF-8: read back U+4F60 (你) correctly");
    } else {
        FAIL("writeconsole_a_readback", "cell grid has wrong codepoint");
    }
}

void test_writeconsole_a_codepage(void) {
    // Verify GetConsoleOutputCP returns UTF-8
    UINT cp = GetConsoleOutputCP();
    if (cp == 65001) {
        PASS("GetConsoleOutputCP returns 65001 (UTF-8)");
    } else {
        FAIL("codepage", "expected 65001");
    }
}

int main(void) {
    printf("=== WriteConsoleA Code Page Test ===\n\n"); fflush(stdout);
    test_writeconsole_a_ascii();
    test_writeconsole_a_codepage();
    test_writeconsole_a_utf8_cjk();
    test_writeconsole_a_readback();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
