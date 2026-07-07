#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_write_read_key(void) {
    // Write a KEY_EVENT via WriteConsoleInputW, then read it back
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    
    INPUT_RECORD ir = {0};
    ir.EventType = KEY_EVENT;
    ir.Event.KeyEvent.bKeyDown = TRUE;
    ir.Event.KeyEvent.wRepeatCount = 1;
    ir.Event.KeyEvent.wVirtualKeyCode = 'A';
    ir.Event.KeyEvent.wVirtualScanCode = 0x1E;
    ir.Event.KeyEvent.uChar.UnicodeChar = L'A';
    ir.Event.KeyEvent.dwControlKeyState = 0;
    
    DWORD written;
    WriteConsoleInputW(hIn, &ir, 1, &written);
    if (written != 1) { FAIL("write_key", "WriteConsoleInputW failed"); return; }
    
    INPUT_RECORD read_ir = {0};
    DWORD read;
    ReadConsoleInputW(hIn, &read_ir, 1, &read);
    if (read != 1) { FAIL("read_key", "ReadConsoleInputW failed"); return; }
    
    if (read_ir.EventType == KEY_EVENT &&
        read_ir.Event.KeyEvent.bKeyDown == TRUE &&
        read_ir.Event.KeyEvent.wVirtualKeyCode == 'A' &&
        read_ir.Event.KeyEvent.uChar.UnicodeChar == L'A') {
        PASS("WriteConsoleInputW → ReadConsoleInputW: KEY_EVENT A round-trip");
    } else {
        FAIL("write_read_key", "data mismatch");
    }
}

void test_peek_doesnt_consume(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    
    INPUT_RECORD ir = {0};
    ir.EventType = KEY_EVENT;
    ir.Event.KeyEvent.bKeyDown = TRUE;
    ir.Event.KeyEvent.wVirtualKeyCode = 'B';
    ir.Event.KeyEvent.uChar.UnicodeChar = L'B';
    ir.Event.KeyEvent.wRepeatCount = 1;
    
    DWORD written;
    WriteConsoleInputW(hIn, &ir, 1, &written);
    
    // Peek should not consume
    INPUT_RECORD peek_ir = {0};
    DWORD peeked;
    PeekConsoleInputW(hIn, &peek_ir, 1, &peeked);
    if (peeked != 1) { FAIL("peek", "PeekConsoleInputW returned 0"); return; }
    
    // Read should still get the event
    INPUT_RECORD read_ir = {0};
    DWORD read;
    ReadConsoleInputW(hIn, &read_ir, 1, &read);
    if (read == 1 && read_ir.Event.KeyEvent.wVirtualKeyCode == 'B') {
        PASS("PeekConsoleInputW does not consume event");
    } else {
        FAIL("peek_consume", "event consumed by peek");
    }
}

void test_flush_clears(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    
    INPUT_RECORD ir = {0};
    ir.EventType = KEY_EVENT;
    ir.Event.KeyEvent.bKeyDown = TRUE;
    ir.Event.KeyEvent.wVirtualKeyCode = 'C';
    ir.Event.KeyEvent.uChar.UnicodeChar = L'C';
    ir.Event.KeyEvent.wRepeatCount = 1;
    
    DWORD written;
    WriteConsoleInputW(hIn, &ir, 1, &written);
    
    FlushConsoleInputBuffer(hIn);
    
    // Peek should return 0 events
    DWORD count;
    GetNumberOfConsoleInputEvents(hIn, &count);
    if (count == 0) {
        PASS("FlushConsoleInputBuffer clears input buffer");
    } else {
        FAIL("flush", "buffer not cleared");
    }
}

void test_multiple_events(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    
    INPUT_RECORD irs[3] = {{0}};
    for (int i = 0; i < 3; i++) {
        irs[i].EventType = KEY_EVENT;
        irs[i].Event.KeyEvent.bKeyDown = (i % 2 == 0) ? TRUE : FALSE;
        irs[i].Event.KeyEvent.wVirtualKeyCode = 'D' + i;
        irs[i].Event.KeyEvent.uChar.UnicodeChar = L'D' + i;
        irs[i].Event.KeyEvent.wRepeatCount = 1;
    }
    
    DWORD written;
    WriteConsoleInputW(hIn, irs, 3, &written);
    if (written != 3) { FAIL("multi_write", "WriteConsoleInputW failed"); return; }
    
    INPUT_RECORD read_irs[3] = {{0}};
    DWORD read;
    ReadConsoleInputW(hIn, read_irs, 3, &read);
    if (read != 3) { FAIL("multi_read", "ReadConsoleInputW count wrong"); return; }
    
    int correct = 1;
    for (int i = 0; i < 3; i++) {
        if (read_irs[i].Event.KeyEvent.wVirtualKeyCode != 'D' + i) correct = 0;
    }
    if (correct) {
        PASS("Multiple events: 3 KEY_EVENTs round-trip correctly");
    } else {
        FAIL("multi", "data mismatch");
    }
}

void test_get_number_of_events(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    FlushConsoleInputBuffer(hIn);
    
    DWORD count1;
    GetNumberOfConsoleInputEvents(hIn, &count1);
    if (count1 != 0) { FAIL("count_init", "buffer not empty"); return; }
    
    INPUT_RECORD ir = {0};
    ir.EventType = KEY_EVENT;
    ir.Event.KeyEvent.bKeyDown = TRUE;
    ir.Event.KeyEvent.wVirtualKeyCode = 'E';
    ir.Event.KeyEvent.uChar.UnicodeChar = L'E';
    ir.Event.KeyEvent.wRepeatCount = 1;
    
    DWORD written;
    WriteConsoleInputW(hIn, &ir, 1, &written);
    
    DWORD count2;
    GetNumberOfConsoleInputEvents(hIn, &count2);
    if (count2 == 1) {
        PASS("GetNumberOfConsoleInputEvents: 0 → 1 after write");
    } else {
        FAIL("count", "expected 1 event");
    }
    
    // Clean up
    FlushConsoleInputBuffer(hIn);
}

int main(void) {
    printf("=== VT Input Pipeline Test ===\n\n"); fflush(stdout);
    test_write_read_key();
    test_peek_doesnt_consume();
    test_flush_clears();
    test_multiple_events();
    test_get_number_of_events();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
