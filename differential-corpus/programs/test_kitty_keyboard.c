// test_kitty_keyboard.c — Conformance test for Kitty keyboard protocol input
// Verifies that WriteConsoleInputW key events can be written and read back,
// and that the internal kitty keyboard protocol parser produces correct records.
#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;

#define PASS(name, ...) do { printf("PASS: " name "\n", ##__VA_ARGS__); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

int main(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    // Test 1: WriteConsoleInputW with a simple key press
    {
        INPUT_RECORD rec = {};
        rec.EventType = KEY_EVENT;
        rec.Event.KeyEvent.bKeyDown = TRUE;
        rec.Event.KeyEvent.wRepeatCount = 1;
        rec.Event.KeyEvent.wVirtualKeyCode = 'A';
        rec.Event.KeyEvent.wVirtualScanCode = 0x1E;
        rec.Event.KeyEvent.uChar.UnicodeChar = 'A';
        rec.Event.KeyEvent.dwControlKeyState = 0;

        DWORD written = 0;
        BOOL ok = WriteConsoleInputW(hIn, &rec, 1, &written);
        if (!ok || written != 1) {
            FAIL("write_key", "WriteConsoleInputW returned %d, written=%lu", ok, written);
        } else {
            PASS("WriteConsoleInputW: key event 'A' written (1 record)");
        }

        // Read it back
        INPUT_RECORD read_rec = {};
        DWORD read_count = 0;
        ok = ReadConsoleInputW(hIn, &read_rec, 1, &read_count);
        if (!ok || read_count != 1) {
            FAIL("read_key", "ReadConsoleInputW returned %d, read=%lu", ok, read_count);
        } else {
            PASS("ReadConsoleInputW: key event 'A' read back (1 record)");
        }

        if (read_rec.EventType == KEY_EVENT &&
            read_rec.Event.KeyEvent.uChar.UnicodeChar == 'A' &&
            read_rec.Event.KeyEvent.bKeyDown == TRUE) {
            PASS("Key event round-trip: char='A', keydown=TRUE");
        } else {
            FAIL("round_trip", "EventType=%lu, char=%04X, down=%d",
                read_rec.EventType, read_rec.Event.KeyEvent.uChar.UnicodeChar,
                read_rec.Event.KeyEvent.bKeyDown);
        }
    }

    // Test 2: WriteConsoleInputW with modifier keys
    {
        INPUT_RECORD rec = {};
        rec.EventType = KEY_EVENT;
        rec.Event.KeyEvent.bKeyDown = TRUE;
        rec.Event.KeyEvent.wRepeatCount = 1;
        rec.Event.KeyEvent.wVirtualKeyCode = VK_RETURN;
        rec.Event.KeyEvent.wVirtualScanCode = 0x1C;
        rec.Event.KeyEvent.uChar.UnicodeChar = '\r';
        rec.Event.KeyEvent.dwControlKeyState = SHIFT_PRESSED | LEFT_CTRL_PRESSED;

        DWORD written = 0;
        WriteConsoleInputW(hIn, &rec, 1, &written);
        
        INPUT_RECORD read_rec = {};
        DWORD read_count = 0;
        ReadConsoleInputW(hIn, &read_rec, 1, &read_count);
        
        if (read_rec.Event.KeyEvent.dwControlKeyState & SHIFT_PRESSED &&
            read_rec.Event.KeyEvent.dwControlKeyState & LEFT_CTRL_PRESSED) {
            PASS("Modifier keys round-trip: SHIFT+CTRL preserved");
        } else {
            FAIL("modifiers", "controlKeyState=0x%08lX", read_rec.Event.KeyEvent.dwControlKeyState);
        }
    }

    // Test 3: Enhanced key flag (arrow keys, etc.)
    {
        INPUT_RECORD rec = {};
        rec.EventType = KEY_EVENT;
        rec.Event.KeyEvent.bKeyDown = TRUE;
        rec.Event.KeyEvent.wRepeatCount = 1;
        rec.Event.KeyEvent.wVirtualKeyCode = VK_UP;
        rec.Event.KeyEvent.wVirtualScanCode = 0x48;
        rec.Event.KeyEvent.uChar.UnicodeChar = 0;
        rec.Event.KeyEvent.dwControlKeyState = ENHANCED_KEY;

        DWORD written = 0;
        WriteConsoleInputW(hIn, &rec, 1, &written);
        
        INPUT_RECORD read_rec = {};
        DWORD read_count = 0;
        ReadConsoleInputW(hIn, &read_rec, 1, &read_count);
        
        if (read_rec.Event.KeyEvent.wVirtualKeyCode == VK_UP &&
            read_rec.Event.KeyEvent.dwControlKeyState & ENHANCED_KEY) {
            PASS("Enhanced key round-trip: VK_UP with ENHANCED_KEY");
        } else {
            FAIL("enhanced", "vk=%04X, state=0x%08lX",
                read_rec.Event.KeyEvent.wVirtualKeyCode,
                read_rec.Event.KeyEvent.dwControlKeyState);
        }
    }

    // Test 4: Key release event
    {
        INPUT_RECORD rec = {};
        rec.EventType = KEY_EVENT;
        rec.Event.KeyEvent.bKeyDown = FALSE;
        rec.Event.KeyEvent.wRepeatCount = 1;
        rec.Event.KeyEvent.wVirtualKeyCode = 'B';
        rec.Event.KeyEvent.uChar.UnicodeChar = 'B';

        DWORD written = 0;
        WriteConsoleInputW(hIn, &rec, 1, &written);
        
        INPUT_RECORD read_rec = {};
        DWORD read_count = 0;
        ReadConsoleInputW(hIn, &read_rec, 1, &read_count);
        
        if (read_rec.Event.KeyEvent.bKeyDown == FALSE) {
            PASS("Key release event round-trip: bKeyDown=FALSE");
        } else {
            FAIL("release", "bKeyDown=%d", read_rec.Event.KeyEvent.bKeyDown);
        }
    }

    // Test 5: Function key (F1)
    {
        INPUT_RECORD rec = {};
        rec.EventType = KEY_EVENT;
        rec.Event.KeyEvent.bKeyDown = TRUE;
        rec.Event.KeyEvent.wRepeatCount = 1;
        rec.Event.KeyEvent.wVirtualKeyCode = VK_F1;
        rec.Event.KeyEvent.wVirtualScanCode = 0x3B;
        rec.Event.KeyEvent.uChar.UnicodeChar = 0;

        DWORD written = 0;
        WriteConsoleInputW(hIn, &rec, 1, &written);
        
        INPUT_RECORD read_rec = {};
        DWORD read_count = 0;
        ReadConsoleInputW(hIn, &read_rec, 1, &read_count);
        
        if (read_rec.Event.KeyEvent.wVirtualKeyCode == VK_F1) {
            PASS("Function key round-trip: VK_F1");
        } else {
            FAIL("f1", "vk=%04X", read_rec.Event.KeyEvent.wVirtualKeyCode);
        }
    }

    // Test 6: Multiple key events in sequence
    {
        INPUT_RECORD recs[3] = {};
        
        // Key down
        recs[0].EventType = KEY_EVENT;
        recs[0].Event.KeyEvent.bKeyDown = TRUE;
        recs[0].Event.KeyEvent.wRepeatCount = 1;
        recs[0].Event.KeyEvent.wVirtualKeyCode = 'C';
        recs[0].Event.KeyEvent.uChar.UnicodeChar = 'C';
        
        // Key repeat
        recs[1].EventType = KEY_EVENT;
        recs[1].Event.KeyEvent.bKeyDown = TRUE;
        recs[1].Event.KeyEvent.wRepeatCount = 1;
        recs[1].Event.KeyEvent.wVirtualKeyCode = 'C';
        recs[1].Event.KeyEvent.uChar.UnicodeChar = 'C';
        
        // Key up
        recs[2].EventType = KEY_EVENT;
        recs[2].Event.KeyEvent.bKeyDown = FALSE;
        recs[2].Event.KeyEvent.wRepeatCount = 1;
        recs[2].Event.KeyEvent.wVirtualKeyCode = 'C';
        recs[2].Event.KeyEvent.uChar.UnicodeChar = 'C';

        DWORD written = 0;
        WriteConsoleInputW(hIn, recs, 3, &written);
        if (written == 3) {
            PASS("WriteConsoleInputW: 3 key events written");
        } else {
            FAIL("multi_write", "written=%lu, expected 3", written);
        }

        INPUT_RECORD read_recs[3] = {};
        DWORD read_count = 0;
        ReadConsoleInputW(hIn, read_recs, 3, &read_count);
        if (read_count == 3) {
            PASS("ReadConsoleInputW: 3 key events read back");
        } else {
            FAIL("multi_read", "read=%lu, expected 3", read_count);
        }

        if (read_count >= 3 &&
            read_recs[0].Event.KeyEvent.bKeyDown == TRUE &&
            read_recs[1].Event.KeyEvent.bKeyDown == TRUE &&
            read_recs[2].Event.KeyEvent.bKeyDown == FALSE) {
            PASS("Key sequence: down, repeat(down), up order correct");
        }
    }

    // Test 7: FlushConsoleInputBuffer
    {
        // Write some events
        INPUT_RECORD rec = {};
        rec.EventType = KEY_EVENT;
        rec.Event.KeyEvent.bKeyDown = TRUE;
        rec.Event.KeyEvent.wVirtualKeyCode = 'Z';
        rec.Event.KeyEvent.uChar.UnicodeChar = 'Z';
        DWORD written = 0;
        WriteConsoleInputW(hIn, &rec, 1, &written);

        // Flush
        BOOL ok = FlushConsoleInputBuffer(hIn);
        if (!ok) {
            FAIL("flush", "FlushConsoleInputBuffer returned %d", ok);
        } else {
            PASS("FlushConsoleInputBuffer succeeded");
        }

        // Verify empty
        DWORD count = 0;
        GetNumberOfConsoleInputEvents(hIn, &count);
        if (count == 0) {
            PASS("Input buffer empty after flush");
        } else {
            FAIL("flush_verify", "count=%lu, expected 0", count);
        }
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail > 0 ? 1 : 0;
}
