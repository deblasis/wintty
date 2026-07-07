#include <windows.h>
#include <stdio.h>
#include <string.h>

static int g_pass = 0;
static int g_fail = 0;

#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)
#define CHECK(cond, name) do { if (cond) { PASS(name); } else { FAIL(name, #cond); } } while(0)

// Test mouse input via VT sequences written to stdin pipe.
// We'll simulate what a terminal would send by writing to a pipe.

int main(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    
    printf("=== Mouse Input VT Sequence Test ===\n\n"); fflush(stdout);
    
    // Set up console mode with mouse input enabled
    DWORD mode;
    GetConsoleMode(hIn, &mode);
    SetConsoleMode(hIn, mode | ENABLE_MOUSE_INPUT);
    
    // Enable mouse tracking in terminal (X10 + SGR + extended)
    // We write these to stdout — the terminal should respond with mouse events on stdin
    // For testing, we manually inject VT mouse sequences via the console input
    // But since we can't easily inject into our own stdin pipe, we use WriteConsoleInputW
    // to verify our MOUSE_EVENT_RECORD structure is correct.
    
    // Instead, let's test that mouse mode is tracked properly
    GetConsoleMode(hIn, &mode);
    CHECK((mode & ENABLE_MOUSE_INPUT) != 0,
          "ENABLE_MOUSE_INPUT flag set in console mode");
    
    // Test that we can write and read back mouse events
    INPUT_RECORD write_rec;
    memset(&write_rec, 0, sizeof(write_rec));
    write_rec.EventType = MOUSE_EVENT;
    write_rec.Event.MouseEvent.dwMousePosition.X = 10;
    write_rec.Event.MouseEvent.dwMousePosition.Y = 5;
    write_rec.Event.MouseEvent.dwButtonState = FROM_LEFT_1ST_BUTTON_PRESSED;
    write_rec.Event.MouseEvent.dwControlKeyState = 0;
    write_rec.Event.MouseEvent.dwEventFlags = 0;
    
    DWORD written;
    WriteConsoleInputW(hIn, &write_rec, 1, &written);
    CHECK(written == 1, "WriteConsoleInputW wrote 1 mouse event");
    
    // Read it back
    INPUT_RECORD read_rec;
    DWORD read_count;
    PeekConsoleInputW(hIn, &read_rec, 1, &read_count);
    CHECK(read_count >= 1, "PeekConsoleInputW sees at least 1 event");
    
    if (read_count >= 1) {
        CHECK(read_rec.EventType == MOUSE_EVENT,
              "Read back event is MOUSE_EVENT type");
        CHECK(read_rec.Event.MouseEvent.dwMousePosition.X == 10,
              "Mouse X position preserved (10)");
        CHECK(read_rec.Event.MouseEvent.dwMousePosition.Y == 5,
              "Mouse Y position preserved (5)");
        CHECK(read_rec.Event.MouseEvent.dwButtonState == FROM_LEFT_1ST_BUTTON_PRESSED,
              "Mouse button state preserved (left button)");
    }
    
    // Flush
    FlushConsoleInputBuffer(hIn);
    
    // Test right mouse button
    memset(&write_rec, 0, sizeof(write_rec));
    write_rec.EventType = MOUSE_EVENT;
    write_rec.Event.MouseEvent.dwMousePosition.X = 20;
    write_rec.Event.MouseEvent.dwMousePosition.Y = 15;
    write_rec.Event.MouseEvent.dwButtonState = RIGHTMOST_BUTTON_PRESSED;
    
    WriteConsoleInputW(hIn, &write_rec, 1, &written);
    PeekConsoleInputW(hIn, &read_rec, 1, &read_count);
    
    if (read_count >= 1) {
        CHECK(read_rec.Event.MouseEvent.dwButtonState == RIGHTMOST_BUTTON_PRESSED,
              "Right mouse button state preserved");
    }
    
    // Test mouse move event
    FlushConsoleInputBuffer(hIn);
    memset(&write_rec, 0, sizeof(write_rec));
    write_rec.EventType = MOUSE_EVENT;
    write_rec.Event.MouseEvent.dwMousePosition.X = 30;
    write_rec.Event.MouseEvent.dwMousePosition.Y = 25;
    write_rec.Event.MouseEvent.dwButtonState = FROM_LEFT_1ST_BUTTON_PRESSED;
    write_rec.Event.MouseEvent.dwEventFlags = MOUSE_MOVED;
    
    WriteConsoleInputW(hIn, &write_rec, 1, &written);
    PeekConsoleInputW(hIn, &read_rec, 1, &read_count);
    
    if (read_count >= 1) {
        CHECK(read_rec.Event.MouseEvent.dwEventFlags == MOUSE_MOVED,
              "Mouse move event flag preserved");
    }
    
    // Test mouse wheel event
    FlushConsoleInputBuffer(hIn);
    memset(&write_rec, 0, sizeof(write_rec));
    write_rec.EventType = MOUSE_EVENT;
    write_rec.Event.MouseEvent.dwMousePosition.X = 0;
    write_rec.Event.MouseEvent.dwMousePosition.Y = 0;
    write_rec.Event.MouseEvent.dwButtonState = (DWORD)(120 << 16); // Wheel delta up
    write_rec.Event.MouseEvent.dwEventFlags = MOUSE_WHEELED;
    
    WriteConsoleInputW(hIn, &write_rec, 1, &written);
    PeekConsoleInputW(hIn, &read_rec, 1, &read_count);
    
    if (read_count >= 1) {
        CHECK(read_rec.Event.MouseEvent.dwEventFlags == MOUSE_WHEELED,
              "Mouse wheel event flag preserved");
    }
    
    FlushConsoleInputBuffer(hIn);
    
    // Get number of console input events (should be 0 after flush)
    DWORD num_events;
    GetNumberOfConsoleInputEvents(hIn, &num_events);
    CHECK(num_events == 0, "No input events after flush");
    
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
