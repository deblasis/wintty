/**
 * Input Round-Trip Test
 *
 * Tests that WriteConsoleInputW → ReadConsoleInputW works correctly.
 * We write key events via WriteConsoleInputW, then read them back.
 * This validates the INPUT_RECORD pipeline under injection.
 */
#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;

#define TEST(name) printf("TEST: %s\n", name); fflush(stdout)
#define PASS(name, ...) do { printf("PASS: "); printf(name, ##__VA_ARGS__); printf("\n"); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

void test_write_read_input(void) {
    TEST("WriteConsoleInputW -> ReadConsoleInputW");
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);

    // Write a key event
    INPUT_RECORD ir = {0};
    ir.EventType = KEY_EVENT;
    ir.Event.KeyEvent.bKeyDown = TRUE;
    ir.Event.KeyEvent.wRepeatCount = 1;
    ir.Event.KeyEvent.wVirtualKeyCode = 0x41; // 'A'
    ir.Event.KeyEvent.uChar.UnicodeChar = L'A';

    DWORD written = 0;
    BOOL ok = WriteConsoleInputW(hIn, &ir, 1, &written);
    if (!ok || written != 1) {
        FAIL("write", "WriteConsoleInputW failed, ok=%d written=%lu err=%lu", ok, written, GetLastError());
        return;
    }

    // Read it back
    INPUT_RECORD read_ir = {0};
    DWORD read = 0;
    ok = ReadConsoleInputW(hIn, &read_ir, 1, &read);
    if (!ok || read != 1) {
        FAIL("read", "ReadConsoleInputW failed, ok=%d read=%lu err=%lu", ok, read, GetLastError());
        return;
    }

    if (read_ir.EventType == KEY_EVENT &&
        read_ir.Event.KeyEvent.uChar.UnicodeChar == L'A' &&
        read_ir.Event.KeyEvent.bKeyDown == TRUE) {
        PASS("WriteConsoleInputW -> ReadConsoleInputW: KEY_EVENT A round-trip OK");
    } else {
        FAIL("round-trip", "EventType=%u KeyCode=%u Char=%c Down=%d",
            read_ir.EventType, read_ir.Event.KeyEvent.wVirtualKeyCode,
            read_ir.Event.KeyEvent.uChar.UnicodeChar, read_ir.Event.KeyEvent.bKeyDown);
    }
}

void test_write_multiple_events(void) {
    TEST("Write 3 events -> Read 3 events");
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);

    INPUT_RECORD irs[3] = {0};
    // Key down 'B'
    irs[0].EventType = KEY_EVENT;
    irs[0].Event.KeyEvent.bKeyDown = TRUE;
    irs[0].Event.KeyEvent.wRepeatCount = 1;
    irs[0].Event.KeyEvent.wVirtualKeyCode = 0x42;
    irs[0].Event.KeyEvent.uChar.UnicodeChar = L'B';
    // Key up 'B'
    irs[1].EventType = KEY_EVENT;
    irs[1].Event.KeyEvent.bKeyDown = FALSE;
    irs[1].Event.KeyEvent.wRepeatCount = 1;
    irs[1].Event.KeyEvent.wVirtualKeyCode = 0x42;
    irs[1].Event.KeyEvent.uChar.UnicodeChar = L'B';
    // Key down 'C'
    irs[2].EventType = KEY_EVENT;
    irs[2].Event.KeyEvent.bKeyDown = TRUE;
    irs[2].Event.KeyEvent.wRepeatCount = 1;
    irs[2].Event.KeyEvent.wVirtualKeyCode = 0x43;
    irs[2].Event.KeyEvent.uChar.UnicodeChar = L'C';

    DWORD written = 0;
    BOOL ok = WriteConsoleInputW(hIn, irs, 3, &written);
    if (!ok || written != 3) {
        FAIL("write", "WriteConsoleInputW failed, ok=%d written=%lu", ok, written);
        return;
    }

    // Read all 3
    INPUT_RECORD read_irs[3] = {0};
    DWORD read = 0;
    ok = ReadConsoleInputW(hIn, read_irs, 3, &read);
    if (ok && read == 3) {
        int match = (read_irs[0].Event.KeyEvent.uChar.UnicodeChar == L'B' &&
                     read_irs[0].Event.KeyEvent.bKeyDown == TRUE &&
                     read_irs[1].Event.KeyEvent.uChar.UnicodeChar == L'B' &&
                     read_irs[1].Event.KeyEvent.bKeyDown == FALSE &&
                     read_irs[2].Event.KeyEvent.uChar.UnicodeChar == L'C' &&
                     read_irs[2].Event.KeyEvent.bKeyDown == TRUE);
        if (match) {
            PASS("3 events round-trip: B-down, B-up, C-down all correct");
        } else {
            FAIL("match", "Events don't match expected sequence");
        }
    } else {
        FAIL("read", "ReadConsoleInputW ok=%d read=%lu expected=3", ok, read);
    }
}

void test_peek_does_not_consume(void) {
    TEST("PeekConsoleInputW does not consume events");
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);

    // Write an event
    INPUT_RECORD ir = {0};
    ir.EventType = KEY_EVENT;
    ir.Event.KeyEvent.bKeyDown = TRUE;
    ir.Event.KeyEvent.wRepeatCount = 1;
    ir.Event.KeyEvent.wVirtualKeyCode = 0x44;
    ir.Event.KeyEvent.uChar.UnicodeChar = L'D';
    DWORD written = 0;
    WriteConsoleInputW(hIn, &ir, 1, &written);

    // Peek
    INPUT_RECORD peek_ir = {0};
    DWORD peeked = 0;
    BOOL ok = PeekConsoleInputW(hIn, &peek_ir, 1, &peeked);
    if (!ok) {
        FAIL("peek", "PeekConsoleInputW failed");
        return;
    }

    // Read (should still get the event)
    INPUT_RECORD read_ir = {0};
    DWORD read = 0;
    ok = ReadConsoleInputW(hIn, &read_ir, 1, &read);
    if (ok && read == 1 && read_ir.Event.KeyEvent.uChar.UnicodeChar == L'D') {
        PASS("PeekConsoleInputW does not consume: event still available for ReadConsoleInputW");
    } else {
        FAIL("consume", "ok=%d read=%lu char=%c", ok, read, read_ir.Event.KeyEvent.uChar.UnicodeChar);
    }
}

void test_flush_clears_buffer(void) {
    TEST("FlushConsoleInputBuffer clears events");
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);

    // Write an event
    INPUT_RECORD ir = {0};
    ir.EventType = KEY_EVENT;
    ir.Event.KeyEvent.bKeyDown = TRUE;
    ir.Event.KeyEvent.wRepeatCount = 1;
    ir.Event.KeyEvent.wVirtualKeyCode = 0x45;
    ir.Event.KeyEvent.uChar.UnicodeChar = L'E';
    DWORD written = 0;
    WriteConsoleInputW(hIn, &ir, 1, &written);

    // Flush
    BOOL ok = FlushConsoleInputBuffer(hIn);
    if (!ok) {
        FAIL("flush", "FlushConsoleInputBuffer failed");
        return;
    }

    // Check count is 0
    DWORD count = 0;
    GetNumberOfConsoleInputEvents(hIn, &count);
    if (count == 0) {
        PASS("FlushConsoleInputBuffer: events cleared, count=0");
    } else {
        FAIL("clear", "Expected count=0 after flush, got %lu", count);
    }
}

int main(void) {
    printf("=== Input Round-Trip Test Suite ===\n\n");
    fflush(stdout);

    test_write_read_input();
    test_write_multiple_events();
    test_peek_does_not_consume();
    test_flush_clears_buffer();

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
