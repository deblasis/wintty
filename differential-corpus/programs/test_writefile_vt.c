#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;

#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_writefile_plain_text(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    const char *text = "Hello";
    DWORD written;
    WriteFile(hOut, text, 5, &written, NULL);
    
    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    // Should have advanced cursor by 5
    if (info.dwCursorPosition.X >= 5) {
        PASS("WriteFile plain text cursor advance");
    } else {
        FAIL("writefile_plain", "cursor not advanced");
    }
}

void test_writefile_vt_sequence(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 10};
    SetConsoleCursorPosition(hOut, pos);
    
    // Write VT color sequence via WriteFile (like Python sys.stdout.write does)
    const char *vt = "\033[31mRed\033[0m";
    DWORD written;
    WriteFile(hOut, vt, 12, &written, NULL);
    
    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    // Cursor should be at X=3 (just "Red"), NOT at X=11 (including escape chars)
    if (info.dwCursorPosition.X == 3 && info.dwCursorPosition.Y == 10) {
        PASS("WriteFile VT escape sequences skipped in cursor tracking");
    } else {
        FAIL("writefile_vt", "cursor wrong (VT not parsed?)");
    }
}

void test_writefile_lf_cr(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {5, 11};
    SetConsoleCursorPosition(hOut, pos);
    
    // Write "A\nB\r\nC" via WriteFile
    const char *text = "A\nB\r\nC";
    DWORD written;
    WriteFile(hOut, text, 6, &written, NULL);
    
    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    // After A(5→6), LF(→12,0), B(0→1), CR(0), LF(→13,0), C(0→1)
    if (info.dwCursorPosition.X == 1 && info.dwCursorPosition.Y == 13) {
        PASS("WriteFile LF/CR handling correct");
    } else {
        FAIL("writefile_lf_cr", "cursor wrong");
    }
}

int main(void) {
    printf("=== WriteFile VT Passthrough Test ===\n\n"); fflush(stdout);
    test_writefile_plain_text();
    test_writefile_vt_sequence();
    test_writefile_lf_cr();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
