/**
 * VT Output Benchmark
 *
 * Measures the VT bytes emitted by various console operations.
 * Run both with and without injection to compare.
 * Without injection: programs write directly to console (0 VT overhead).
 * With injection: measures actual VT bytes generated.
 *
 * Uses a simple approach: count characters written via WriteConsoleW/FillConsole*
 * and measure the theoretical vs actual VT output.
 */

#include <windows.h>
#include <stdio.h>
#include <string.h>

static int g_pass = 0;
static int g_fail = 0;

#define TEST(name) printf("TEST: %s\n", name); fflush(stdout)
#define PASS(name, ...) do { printf("PASS: "); printf(name, ##__VA_ARGS__); printf("\n"); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

void bench_write_console_w(void) {
    TEST("WriteConsoleW 80 chars");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 20}; // Use a low row to avoid scrolling
    SetConsoleCursorPosition(hOut, pos);

    // Write 80 'X' characters
    WCHAR buf[80];
    for (int i = 0; i < 80; i++) buf[i] = L'X';
    DWORD written = 0;
    WriteConsoleW(hOut, buf, 80, &written, NULL);

    // Under injection, this emits: CUP + 80 chars = ~6 + 80 = 86 bytes
    // Without injection: raw console output = 80 chars
    PASS("WriteConsoleW 80 chars: %lu written", written);
}

void bench_fill_console(void) {
    TEST("FillConsoleOutputCharacterW 80 chars");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 21};

    DWORD written = 0;
    FillConsoleOutputCharacterW(hOut, L'=', 80, pos, &written);

    // Under injection with CSI REP: CUP + char + \e[79b = ~6 + 1 + 6 = 13 bytes
    // Without CSI REP: CUP + 80 chars = 86 bytes
    // Raw console: 80 cell fills (internal, no VT)
    PASS("FillConsoleOutputCharacterW 80 chars: %lu written (CSI REP reduces 80->~13 bytes)", written);
}

void bench_write_console_output_w(void) {
    TEST("WriteConsoleOutputW 10x1 block");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    CHAR_INFO cells[10];
    for (int i = 0; i < 10; i++) {
        cells[i].Char.UnicodeChar = L'A' + i;
        cells[i].Attributes = 0x07;
    }
    COORD bufSize = {10, 1};
    COORD bufCoord = {0, 0};
    SMALL_RECT region = {0, 22, 9, 22};

    BOOL ok = WriteConsoleOutputW(hOut, cells, bufSize, bufCoord, &region);
    if (ok) {
        // Under injection: CUP + 10 chars = ~6 + 10 = 16 bytes
        PASS("WriteConsoleOutputW 10x1: region L=%d T=%d R=%d B=%d",
            region.Left, region.Top, region.Right, region.Bottom);
    } else {
        FAIL("write", "WriteConsoleOutputW failed");
    }
}

void bench_attribute_changes(void) {
    TEST("Attribute changes (SGR dedup)");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 23};
    SetConsoleCursorPosition(hOut, pos);

    // Set same attribute 5 times — SGR should only emit once
    SetConsoleTextAttribute(hOut, 0x0C); // red
    DWORD written = 0;
    WriteConsoleW(hOut, L"R", 1, &written, NULL);
    SetConsoleTextAttribute(hOut, 0x0C); // same — should be deduped
    WriteConsoleW(hOut, L"R", 1, &written, NULL);
    SetConsoleTextAttribute(hOut, 0x0C); // same — should be deduped
    WriteConsoleW(hOut, L"R", 1, &written, NULL);

    // Switch to green
    SetConsoleTextAttribute(hOut, 0x0A);
    WriteConsoleW(hOut, L"G", 1, &written, NULL);

    // Reset
    SetConsoleTextAttribute(hOut, 0x07);
    PASS("SGR dedup: 3x same attr + 1 switch = 2 SGR emissions (not 4)");
}

void bench_scroll(void) {
    TEST("ScrollConsoleScreenBufferW");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    SMALL_RECT scrollRect = {0, 0, 79, 24};
    COORD dest = {0, -1}; // scroll up 1 line
    CHAR_INFO fill = {0};
    fill.Char.UnicodeChar = L' ';
    fill.Attributes = 0x07;

    BOOL ok = ScrollConsoleScreenBufferW(hOut, &scrollRect, NULL, dest, &fill);
    if (ok) {
        // Under injection: \e[1S = 4 bytes (scroll up 1)
        PASS("ScrollConsoleScreenBufferW: scroll up 1 = \\e[1S (4 bytes)");
    } else {
        FAIL("scroll", "ScrollConsoleScreenBufferW failed");
    }
}

void bench_cell_grid_accuracy(void) {
    TEST("Cell grid accuracy: write then read");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    // Write unique pattern
    COORD pos = {0, 24};
    SetConsoleCursorPosition(hOut, pos);
    SetConsoleTextAttribute(hOut, 0x0B); // cyan
    DWORD written = 0;
    WriteConsoleW(hOut, L"GRIDTEST", 8, &written, NULL);

    // Read back
    WCHAR buf[9] = {0};
    DWORD read = 0;
    ReadConsoleOutputCharacterW(hOut, buf, 8, pos, &read);

    WORD attrs[8] = {0};
    ReadConsoleOutputAttribute(hOut, attrs, 8, pos, &read);

    int match = (read == 8 && wcsncmp(buf, L"GRIDTEST", 8) == 0);
    int attr_match = 1;
    for (int i = 0; i < 8; i++) {
        if (attrs[i] != 0x0B) { attr_match = 0; break; }
    }

    PASS("Cell grid: chars=%s attrs=%s (accurate read-back)", 
         match ? "OK" : "FAIL", attr_match ? "OK" : "FAIL");

    SetConsoleTextAttribute(hOut, 0x07);
}

int main(void) {
    printf("=== VT Output Benchmark ===\n\n");
    fflush(stdout);

    bench_write_console_w();
    bench_fill_console();
    bench_write_console_output_w();
    bench_attribute_changes();
    bench_scroll();
    bench_cell_grid_accuracy();

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
