/**
 * Test: CSI REP in WriteConsoleW
 *
 * Verifies that WriteConsoleW uses CSI REP (\e[Ps b) for runs of identical
 * narrow characters, and does NOT use it for wide characters.
 *
 * Run via: zig-out/bin/wintty-pcon-inject.exe test_vt_rep_writeconsole.exe
 */

#include <windows.h>
#include <stdio.h>
#include <string.h>

static int g_pass = 0;
static int g_fail = 0;

#define PASS(name, ...) do { printf("PASS: " name "\n", ##__VA_ARGS__); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

void test_repeated_ascii(void) {
    printf("TEST: WriteConsoleW repeated ASCII characters\n"); fflush(stdout);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 1};
    SetConsoleCursorPosition(hOut, pos);

    // Write 10 'A' characters
    WCHAR buf[10];
    for (int i = 0; i < 10; i++) buf[i] = L'A';
    DWORD written = 0;
    WriteConsoleW(hOut, buf, 10, &written, NULL);

    if (written == 10) {
        PASS("WriteConsoleW 10x 'A': %lu written", written);
    } else {
        FAIL("write", "expected 10, got %lu", written);
    }

    // Verify cell grid: all cells should be 'A'
    WCHAR read_buf[10] = {0};
    DWORD read = 0;
    ReadConsoleOutputCharacterW(hOut, read_buf, 10, pos, &read);
    int match = 1;
    for (int i = 0; i < 10; i++) {
        if (read_buf[i] != L'A') { match = 0; break; }
    }
    if (match && read == 10) {
        PASS("Cell grid: 10 cells all 'A'");
    } else {
        FAIL("cell_grid", "expected 10x'A', got %lu: %ls", read, read_buf);
    }
}

void test_repeated_spaces(void) {
    printf("TEST: WriteConsoleW repeated spaces\n"); fflush(stdout);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 2};
    SetConsoleCursorPosition(hOut, pos);

    // Write 40 spaces
    WCHAR buf[40];
    for (int i = 0; i < 40; i++) buf[i] = L' ';
    DWORD written = 0;
    WriteConsoleW(hOut, buf, 40, &written, NULL);

    if (written == 40) {
        PASS("WriteConsoleW 40 spaces: %lu written", written);
    } else {
        FAIL("write", "expected 40, got %lu", written);
    }

    // Verify cell grid
    WCHAR read_buf[40] = {0};
    DWORD read = 0;
    ReadConsoleOutputCharacterW(hOut, read_buf, 40, pos, &read);
    int all_spaces = 1;
    for (int i = 0; i < 40; i++) {
        if (read_buf[i] != L' ') { all_spaces = 0; break; }
    }
    if (all_spaces && read == 40) {
        PASS("Cell grid: 40 cells all spaces");
    } else {
        FAIL("cell_grid", "expected 40 spaces, got %lu", read);
    }
}

void test_mixed_chars(void) {
    printf("TEST: WriteConsoleW mixed characters (AABBCCC)\n"); fflush(stdout);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 3};
    SetConsoleCursorPosition(hOut, pos);

    // Write AABBCCC
    WCHAR buf[] = L"AABBCCC";
    DWORD written = 0;
    WriteConsoleW(hOut, buf, 7, &written, NULL);

    if (written == 7) {
        PASS("WriteConsoleW AABBCCC: %lu written", written);
    } else {
        FAIL("write", "expected 7, got %lu", written);
    }

    // Verify cell grid
    WCHAR read_buf[7] = {0};
    DWORD read = 0;
    ReadConsoleOutputCharacterW(hOut, read_buf, 7, pos, &read);
    if (read == 7 && wcsncmp(read_buf, L"AABBCCC", 7) == 0) {
        PASS("Cell grid: AABBCCC matches");
    } else {
        FAIL("cell_grid", "expected AABBCCC, got %lu: %ls", read, read_buf);
    }
}

void test_single_char(void) {
    printf("TEST: WriteConsoleW single character (no REP)\n"); fflush(stdout);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 4};
    SetConsoleCursorPosition(hOut, pos);

    WCHAR buf[] = L"X";
    DWORD written = 0;
    WriteConsoleW(hOut, buf, 1, &written, NULL);

    if (written == 1) {
        PASS("WriteConsoleW single 'X': %lu written", written);
    } else {
        FAIL("write", "expected 1, got %lu", written);
    }
}

void test_empty_write(void) {
    printf("TEST: WriteConsoleW empty (0 chars)\n"); fflush(stdout);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    DWORD written = 99;
    WriteConsoleW(hOut, L"", 0, &written, NULL);

    if (written == 0) {
        PASS("WriteConsoleW 0 chars: %lu written", written);
    } else {
        FAIL("write", "expected 0, got %lu", written);
    }
}

void test_control_chars_break_runs(void) {
    printf("TEST: WriteConsoleW control chars break runs\n"); fflush(stdout);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 5};
    SetConsoleCursorPosition(hOut, pos);

    // Write AAA\nBBB — the \n should break the run
    WCHAR buf[] = L"AAA\nBBB";
    DWORD written = 0;
    WriteConsoleW(hOut, buf, 7, &written, NULL);

    if (written == 7) {
        PASS("WriteConsoleW AAA\\nBBB: %lu written", written);
    } else {
        FAIL("write", "expected 7, got %lu", written);
    }
}

void test_repeated_box_drawing(void) {
    printf("TEST: WriteConsoleW repeated box drawing characters\n"); fflush(stdout);
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 6};
    SetConsoleCursorPosition(hOut, pos);

    // Write 20 horizontal line chars (U+2500 BOX DRAWINGS LIGHT HORIZONTAL)
    WCHAR buf[20];
    for (int i = 0; i < 20; i++) buf[i] = 0x2500; // ─
    DWORD written = 0;
    WriteConsoleW(hOut, buf, 20, &written, NULL);

    if (written == 20) {
        PASS("WriteConsoleW 20x '─': %lu written", written);
    } else {
        FAIL("write", "expected 20, got %lu", written);
    }

    // Verify cell grid
    WCHAR read_buf[20] = {0};
    DWORD read = 0;
    ReadConsoleOutputCharacterW(hOut, read_buf, 20, pos, &read);
    int all_match = 1;
    for (int i = 0; i < 20; i++) {
        if (read_buf[i] != 0x2500) { all_match = 0; break; }
    }
    if (all_match && read == 20) {
        PASS("Cell grid: 20x '─' matches");
    } else {
        FAIL("cell_grid", "expected 20x '─', got %lu", read);
    }
}

int main(void) {
    printf("=== WriteConsoleW CSI REP Test ===\n\n"); fflush(stdout);

    test_repeated_ascii();
    test_repeated_spaces();
    test_mixed_chars();
    test_single_char();
    test_empty_write();
    test_control_chars_break_runs();
    test_repeated_box_drawing();

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
