/**
 * Interactive Input Test
 *
 * Tests the input pipeline: reads a line from stdin and echoes it.
 * Run: echo "Hello from stdin" | wintty-pcon-inject.exe test_input.exe
 */
#include <windows.h>
#include <stdio.h>
#include <string.h>

int main(void) {
    HANDLE hIn = GetStdHandle(STD_INPUT_HANDLE);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    printf("=== Interactive Input Test ===\n");
    fflush(stdout);

    // Test 1: GetConsoleMode
    DWORD mode = 0;
    BOOL ok = GetConsoleMode(hIn, &mode);
    printf("GetConsoleMode(stdin): ok=%d mode=0x%08lX\n", ok, mode);
    fflush(stdout);

    // Test 2: GetNumberOfConsoleInputEvents
    DWORD count = 0;
    ok = GetNumberOfConsoleInputEvents(hIn, &count);
    printf("GetNumberOfConsoleInputEvents: ok=%d count=%lu\n", ok, count);
    fflush(stdout);

    // Test 3: PeekConsoleInputW
    INPUT_RECORD ir[4] = {0};
    DWORD peeked = 0;
    ok = PeekConsoleInputW(hIn, ir, 4, &peeked);
    printf("PeekConsoleInputW: ok=%d peeked=%lu\n", ok, peeked);
    fflush(stdout);

    // Test 4: ReadConsoleW
    WCHAR buf[256] = {0};
    DWORD read = 0;
    printf("Calling ReadConsoleW (will read from stdin)...\n");
    fflush(stdout);
    ok = ReadConsoleW(hIn, buf, 255, &read, NULL);
    printf("ReadConsoleW: ok=%d read=%lu text='%ls'\n", ok, read, buf);
    fflush(stdout);

    // Test 5: Write back what we read
    if (read > 0) {
        DWORD written = 0;
        // Build "ECHO: <text>\n"
        WCHAR echo_buf[280] = {0};
        wcscpy(echo_buf, L"ECHO: ");
        wcsncat(echo_buf, buf, read);
        WriteConsoleW(hOut, echo_buf, (DWORD)wcslen(echo_buf), &written, NULL);
        fflush(stdout);
    }

    // Test 6: FlushConsoleInputBuffer
    ok = FlushConsoleInputBuffer(hIn);
    printf("\nFlushConsoleInputBuffer: ok=%d\n", ok);
    fflush(stdout);

    printf("=== All input tests complete ===\n");
    return 0;
}
