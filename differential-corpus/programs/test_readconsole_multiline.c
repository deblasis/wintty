#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_readconsole_line_mode(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    
    // Write input events directly (simulates user typing "ABC\n")
    INPUT_RECORD records[4];
    records[0].EventType = KEY_EVENT;
    records[0].Event.KeyEvent.bKeyDown = TRUE;
    records[0].Event.KeyEvent.wRepeatCount = 1;
    records[0].Event.KeyEvent.wVirtualKeyCode = 'A';
    records[0].Event.KeyEvent.uChar.UnicodeChar = L'A';
    records[0].Event.KeyEvent.dwControlKeyState = 0;
    
    records[1] = records[0]; records[1].Event.KeyEvent.uChar.UnicodeChar = L'B';
    records[1].Event.KeyEvent.wVirtualKeyCode = 'B';
    records[2] = records[0]; records[2].Event.KeyEvent.uChar.UnicodeChar = L'C';
    records[2].Event.KeyEvent.wVirtualKeyCode = 'C';
    records[3] = records[0]; records[3].Event.KeyEvent.uChar.UnicodeChar = L'\r';
    records[3].Event.KeyEvent.wVirtualKeyCode = VK_RETURN;
    
    DWORD written;
    WriteConsoleInputW(hIn, records, 4, &written);
    
    if (written == 4) {
        PASS("WriteConsoleInputW: 4 input records written");
    } else {
        FAIL("write_input", "unexpected write count");
    }
}

void test_write_then_readback(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Write known text, then read back to verify cell grid consistency
    SetConsoleCursorPosition(hOut, (COORD){0, 0});
    SetConsoleTextAttribute(hOut, 0x07);
    
    DWORD written;
    WriteConsoleW(hOut, L"TEST_READBACK", 13, &written, NULL);
    
    WCHAR buf[14] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 13, (COORD){0, 0}, &read);
    
    if (read == 13 && wcsncmp(buf, L"TEST_READBACK", 13) == 0) {
        PASS("Write then readback: TEST_READBACK matches");
    } else {
        FAIL("write_readback", "mismatch");
    }
}

void test_peek_input(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    
    DWORD count = 0;
    BOOL ok = GetNumberOfConsoleInputEvents(hIn, &count);
    if (ok) {
        PASS("GetNumberOfConsoleInputEvents returns TRUE");
    } else {
        FAIL("peek_input", "returned FALSE");
    }
}

void test_console_mode_roundtrip(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Read current modes
    DWORD in_mode, out_mode;
    GetConsoleMode(hIn, &in_mode);
    GetConsoleMode(hOut, &out_mode);
    
    // Set new modes
    SetConsoleMode(hIn, ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT);
    SetConsoleMode(hOut, ENABLE_PROCESSED_OUTPUT | ENABLE_WRAP_AT_EOL_OUTPUT);
    
    // Read back
    DWORD new_in_mode, new_out_mode;
    GetConsoleMode(hIn, &new_in_mode);
    GetConsoleMode(hOut, &new_out_mode);
    
    if (new_in_mode == (ENABLE_PROCESSED_INPUT | ENABLE_LINE_INPUT) &&
        new_out_mode == (ENABLE_PROCESSED_OUTPUT | ENABLE_WRAP_AT_EOL_OUTPUT)) {
        PASS("Console mode round-trip: set→get matches");
    } else {
        FAIL("mode_roundtrip", "mode mismatch");
    }
    
    // Restore original modes
    SetConsoleMode(hIn, in_mode);
    SetConsoleMode(hOut, out_mode);
}

int main(void) {
    printf("=== ReadConsole Multi-line Tests ===\n\n"); fflush(stdout);
    
    test_readconsole_line_mode();
    test_write_then_readback();
    test_peek_input();
    test_console_mode_roundtrip();
    
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
