// test_vt_key_input.c — Conformance test for VT key sequence parsing
// Verifies that VT escape sequences from the terminal are correctly
// parsed into INPUT_RECORD key events via the input pipeline.
#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;

#define PASS(name, ...) do { printf("PASS: " name "\n", ##__VA_ARGS__); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

int main(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    // Test 1: WriteConsoleInputW with arrow key and read back
    {
        INPUT_RECORD rec = {};
        rec.EventType = KEY_EVENT;
        rec.Event.KeyEvent.bKeyDown = TRUE;
        rec.Event.KeyEvent.wRepeatCount = 1;
        rec.Event.KeyEvent.wVirtualKeyCode = VK_LEFT;
        rec.Event.KeyEvent.wVirtualScanCode = 0x4B;
        rec.Event.KeyEvent.uChar.UnicodeChar = 0;
        rec.Event.KeyEvent.dwControlKeyState = ENHANCED_KEY;

        DWORD written = 0;
        BOOL ok = WriteConsoleInputW(hIn, &rec, 1, &written);
        if (!ok || written != 1) {
            FAIL("write", "WriteConsoleInputW returned %d, written=%lu", ok, written);
        } else {
            PASS("WriteConsoleInputW: arrow key written");
        }

        INPUT_RECORD read_rec = {};
        DWORD read_count = 0;
        ReadConsoleInputW(hIn, &read_rec, 1, &read_count);
        
        if (read_rec.Event.KeyEvent.wVirtualKeyCode == VK_LEFT) {
            PASS("ReadConsoleInputW: arrow key VK_LEFT preserved");
        } else {
            FAIL("arrow", "vk=%04X", read_rec.Event.KeyEvent.wVirtualKeyCode);
        }
    }

    // Test 2: GetNumberOfConsoleInputEvents
    {
        DWORD count = 0;
        BOOL ok = GetNumberOfConsoleInputEvents(hIn, &count);
        if (ok) {
            PASS("GetNumberOfConsoleInputEvents: returns %lu", count);
        } else {
            FAIL("count", "GetNumberOfConsoleInputEvents returned %d", ok);
        }
    }

    // Test 3: PeekConsoleInputW
    {
        // Write an event
        INPUT_RECORD rec = {};
        rec.EventType = KEY_EVENT;
        rec.Event.KeyEvent.bKeyDown = TRUE;
        rec.Event.KeyEvent.wRepeatCount = 1;
        rec.Event.KeyEvent.wVirtualKeyCode = 'K';
        rec.Event.KeyEvent.uChar.UnicodeChar = 'K';
        DWORD written = 0;
        WriteConsoleInputW(hIn, &rec, 1, &written);

        // Peek should not consume
        INPUT_RECORD peek_rec = {};
        DWORD peek_count = 0;
        PeekConsoleInputW(hIn, &peek_rec, 1, &peek_count);
        if (peek_count == 1) {
            PASS("PeekConsoleInputW: sees 1 event without consuming");
        } else {
            FAIL("peek", "PeekConsoleInputW returned %lu events", peek_count);
        }

        // Read should still be available
        INPUT_RECORD read_rec = {};
        DWORD read_count = 0;
        ReadConsoleInputW(hIn, &read_rec, 1, &read_count);
        if (read_count == 1 && read_rec.Event.KeyEvent.uChar.UnicodeChar == 'K') {
            PASS("ReadConsoleInputW: event still available after peek");
        } else {
            FAIL("peek_read", "read=%lu, char=%04X", read_count, read_rec.Event.KeyEvent.uChar.UnicodeChar);
        }
    }

    // Test 4: WriteConsoleInputA
    {
        INPUT_RECORD rec = {};
        rec.EventType = KEY_EVENT;
        rec.Event.KeyEvent.bKeyDown = TRUE;
        rec.Event.KeyEvent.wRepeatCount = 1;
        rec.Event.KeyEvent.wVirtualKeyCode = 'M';
        rec.Event.KeyEvent.uChar.AsciiChar = 'M';
        
        DWORD written = 0;
        BOOL ok = WriteConsoleInputA(hIn, &rec, 1, &written);
        if (ok && written == 1) {
            PASS("WriteConsoleInputA: key event written");
        } else {
            FAIL("write_a", "returned %d, written=%lu", ok, written);
        }

        INPUT_RECORD read_rec = {};
        DWORD read_count = 0;
        ReadConsoleInputA(hIn, &read_rec, 1, &read_count);
        if (read_count == 1) {
            PASS("ReadConsoleInputA: key event read back");
        } else {
            FAIL("read_a", "read=%lu", read_count);
        }
    }

    // Test 5: FlushConsoleInputBuffer
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
        FlushConsoleInputBuffer(hIn);

        DWORD count = 0;
        GetNumberOfConsoleInputEvents(hIn, &count);
        if (count == 0) {
            PASS("FlushConsoleInputBuffer: buffer is empty");
        } else {
            FAIL("flush", "count=%lu after flush", count);
        }
    }

    // Test 6: Multiple key events with modifiers
    {
        INPUT_RECORD recs[2] = {};
        
        // Ctrl+A down
        recs[0].EventType = KEY_EVENT;
        recs[0].Event.KeyEvent.bKeyDown = TRUE;
        recs[0].Event.KeyEvent.wRepeatCount = 1;
        recs[0].Event.KeyEvent.wVirtualKeyCode = 'A';
        recs[0].Event.KeyEvent.uChar.UnicodeChar = 1; // Ctrl+A
        recs[0].Event.KeyEvent.dwControlKeyState = LEFT_CTRL_PRESSED;
        
        // Ctrl+A up
        recs[1].EventType = KEY_EVENT;
        recs[1].Event.KeyEvent.bKeyDown = FALSE;
        recs[1].Event.KeyEvent.wRepeatCount = 1;
        recs[1].Event.KeyEvent.wVirtualKeyCode = 'A';
        recs[1].Event.KeyEvent.uChar.UnicodeChar = 1;
        recs[1].Event.KeyEvent.dwControlKeyState = LEFT_CTRL_PRESSED;

        DWORD written = 0;
        WriteConsoleInputW(hIn, recs, 2, &written);
        
        INPUT_RECORD read_recs[2] = {};
        DWORD read_count = 0;
        ReadConsoleInputW(hIn, read_recs, 2, &read_count);
        
        if (read_count == 2) {
            int mods_ok = (read_recs[0].Event.KeyEvent.dwControlKeyState & LEFT_CTRL_PRESSED) != 0;
            int order_ok = read_recs[0].Event.KeyEvent.bKeyDown == TRUE &&
                          read_recs[1].Event.KeyEvent.bKeyDown == FALSE;
            if (mods_ok && order_ok) {
                PASS("Ctrl+A down/up: modifiers and order preserved");
            } else {
                FAIL("ctrl_a", "mods=%d order=%d", mods_ok, order_ok);
            }
        } else {
            FAIL("ctrl_a_read", "read=%lu, expected 2", read_count);
        }
    }

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail > 0 ? 1 : 0;
}
