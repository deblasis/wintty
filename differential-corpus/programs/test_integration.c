#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
// Use a buffer to defer PASS/FAIL output until after cell grid operations
static char g_msgs[20][256];
static int g_msg_count = 0;

#define PASS(name) do { snprintf(g_msgs[g_msg_count], 256, "PASS: %s", name); g_msg_count++; g_pass++; } while(0)
#define FAIL(name, msg) do { snprintf(g_msgs[g_msg_count], 256, "FAIL: %s: %s", name, msg); g_msg_count++; g_fail++; } while(0)

static void flush_messages(void) {
    for (int i = 0; i < g_msg_count; i++) {
        printf("%s\n", g_msgs[i]);
        fflush(stdout);
    }
    g_msg_count = 0;
}

void test_colored_write_scroll_readback(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Step 1: Write colored text at row 5
    SetConsoleCursorPosition(hOut, (COORD){0, 5});
    SetConsoleTextAttribute(hOut, 0x0C); // bright red on black
    DWORD written;
    WriteConsoleW(hOut, L"RED_LINE", 8, &written, NULL);
    
    // Step 2: Write green text at row 6
    SetConsoleCursorPosition(hOut, (COORD){0, 6});
    SetConsoleTextAttribute(hOut, 0x0A); // bright green on black
    WriteConsoleW(hOut, L"GREEN_LN", 8, &written, NULL);
    
    // Step 3: Write blue text at row 7
    SetConsoleCursorPosition(hOut, (COORD){0, 7});
    SetConsoleTextAttribute(hOut, 0x09); // bright blue on black
    WriteConsoleW(hOut, L"BLUE_LNE", 8, &written, NULL);
    
    // Step 4: Fill a region BEFORE any printf output
    COORD fill_pos = {0, 3}; // Use row 3 (above colored text)
    DWORD filled;
    FillConsoleOutputCharacterW(hOut, L'*', 10, fill_pos, &filled);
    FillConsoleOutputAttribute(hOut, 0x0E, 10, fill_pos, &filled);
    
    // Step 5: Read back fill first
    WCHAR fbuf[11] = {0};
    DWORD fread;
    ReadConsoleOutputCharacterW(hOut, fbuf, 10, (COORD){0, 3}, &fread);
    int all_stars = 1;
    for (int i = 0; i < 10; i++) {
        if (fbuf[i] != L'*') { all_stars = 0; break; }
    }
    if (all_stars) {
        PASS("Integration: Fill with '*' verified");
    } else {
        FAIL("int_fill", "not all stars");
    }
    
    WORD fattrs[10];
    ReadConsoleOutputAttribute(hOut, fattrs, 10, (COORD){0, 3}, &fread);
    int all_yellow = 1;
    for (int i = 0; i < 10; i++) {
        if (fattrs[i] != 0x0E) { all_yellow = 0; break; }
    }
    if (all_yellow) {
        PASS("Integration: Fill attribute 0x0E verified");
    } else {
        FAIL("int_fill_attr", "not all yellow");
    }
    
    // Step 6: Now safe to read text back (deferred messages)
    WCHAR buf[9] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 8, (COORD){0, 5}, &read);
    if (wcsncmp(buf, L"RED_LINE", 8) == 0) {
        PASS("Integration: RED_LINE text matches");
    } else {
        FAIL("int_text_5", "mismatch");
    }
    
    ReadConsoleOutputCharacterW(hOut, buf, 8, (COORD){0, 6}, &read);
    if (wcsncmp(buf, L"GREEN_LN", 8) == 0) {
        PASS("Integration: GREEN_LN text matches");
    } else {
        FAIL("int_text_6", "mismatch");
    }
    
    // Step 7: Read back attributes
    WORD attrs[8];
    ReadConsoleOutputAttribute(hOut, attrs, 8, (COORD){0, 5}, &read);
    if (attrs[0] == 0x0C) {
        PASS("Integration: RED_LINE attributes are 0x0C");
    } else {
        FAIL("int_attr_5", "attribute mismatch");
    }
    
    ReadConsoleOutputAttribute(hOut, attrs, 8, (COORD){0, 6}, &read);
    if (attrs[0] == 0x0A) {
        PASS("Integration: GREEN_LN attributes are 0x0A");
    } else {
        FAIL("int_attr_6", "attribute mismatch");
    }
    
    flush_messages();
}

void test_cursor_movement_pattern(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD written;
    
    // Write a diagonal pattern: A at (0,20), B at (1,21), C at (2,22)
    SetConsoleCursorPosition(hOut, (COORD){0, 20});
    WriteConsoleW(hOut, L"A", 1, &written, NULL);
    SetConsoleCursorPosition(hOut, (COORD){1, 21});
    WriteConsoleW(hOut, L"B", 1, &written, NULL);
    SetConsoleCursorPosition(hOut, (COORD){2, 22});
    WriteConsoleW(hOut, L"C", 1, &written, NULL);
    
    // Verify diagonal
    WCHAR buf[4] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, &buf[0], 1, (COORD){0, 20}, &read);
    ReadConsoleOutputCharacterW(hOut, &buf[1], 1, (COORD){1, 21}, &read);
    ReadConsoleOutputCharacterW(hOut, &buf[2], 1, (COORD){2, 22}, &read);
    
    if (buf[0] == L'A' && buf[1] == L'B' && buf[2] == L'C') {
        PASS("Integration: Diagonal cursor pattern A,B,C verified");
    } else {
        FAIL("int_diagonal", "mismatch");
    }
    
    // Verify cursor is at (3, 22) after last write
    CONSOLE_SCREEN_BUFFER_INFO info;
    GetConsoleScreenBufferInfo(hOut, &info);
    if (info.dwCursorPosition.X == 3 && info.dwCursorPosition.Y == 22) {
        PASS("Integration: Cursor at (3,22) after diagonal writes");
    } else {
        FAIL("int_cursor_pos", "cursor position wrong");
    }
    
    flush_messages();
}

void test_write_output_read_roundtrip(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Write a CHAR_INFO block at (0, 25)
    CHAR_INFO cells[4];
    cells[0].Char.UnicodeChar = L'W';
    cells[0].Attributes = 0x0B; // bright cyan
    cells[1].Char.UnicodeChar = L'X';
    cells[1].Attributes = 0x0D; // bright magenta
    cells[2].Char.UnicodeChar = L'Y';
    cells[2].Attributes = 0x0E; // bright yellow
    cells[3].Char.UnicodeChar = L'Z';
    cells[3].Attributes = 0x0F; // bright white
    
    COORD buf_size = {2, 2};
    COORD buf_coord = {0, 0};
    SMALL_RECT write_region = {0, 25, 1, 26};
    WriteConsoleOutputW(hOut, cells, buf_size, buf_coord, &write_region);
    
    // Read back
    CHAR_INFO read_cells[4];
    SMALL_RECT read_region = {0, 25, 1, 26};
    ReadConsoleOutputW(hOut, read_cells, buf_size, buf_coord, &read_region);
    
    int match = 1;
    const WCHAR expected[] = {L'W', L'X', L'Y', L'Z'};
    const WORD expected_attrs[] = {0x0B, 0x0D, 0x0E, 0x0F};
    
    for (int i = 0; i < 4; i++) {
        if (read_cells[i].Char.UnicodeChar != expected[i]) match = 0;
        if (read_cells[i].Attributes != expected_attrs[i]) match = 0;
    }
    
    if (match) {
        PASS("Integration: WriteConsoleOutputW → ReadConsoleOutputW round-trip");
    } else {
        FAIL("int_output_roundtrip", "mismatch");
    }
    
    flush_messages();
}

void test_title_and_codepage(void) {
    // Set a custom title
    SetConsoleTitleW(L"Integration Test Title");
    
    // Read it back
    WCHAR title[256] = {0};
    GetConsoleTitleW(title, 256);
    if (wcsncmp(title, L"Integration Test Title", 22) == 0) {
        PASS("Integration: Console title round-trip");
    } else {
        FAIL("int_title", "title mismatch");
    }
    
    // Verify code page
    UINT cp = GetConsoleOutputCP();
    if (cp == 65001) {
        PASS("Integration: Output code page is UTF-8 (65001)");
    } else {
        FAIL("int_codepage", "unexpected code page");
    }
    
    flush_messages();
}

void test_scroll_preserves_outside(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD written;
    
    // Write markers at rows 30 and 33 (outside scroll region 31-32)
    SetConsoleCursorPosition(hOut, (COORD){0, 30});
    WriteConsoleW(hOut, L"MARK30", 6, &written, NULL);
    SetConsoleCursorPosition(hOut, (COORD){0, 33});
    WriteConsoleW(hOut, L"MARK33", 6, &written, NULL);
    
    // Write inside scroll region
    SetConsoleCursorPosition(hOut, (COORD){0, 31});
    WriteConsoleW(hOut, L"INSIDE", 6, &written, NULL);
    
    // Scroll region 31-32 up by 1
    SMALL_RECT scrollRect = {0, 31, 79, 32};
    COORD dest = {0, 30};
    CHAR_INFO fill = {0};
    fill.Char.UnicodeChar = L'.';
    fill.Attributes = 7;
    ScrollConsoleScreenBufferW(hOut, &scrollRect, NULL, dest, &fill);
    
    // Verify scrolled content
    WCHAR buf[7] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 6, (COORD){0, 30}, &read);
    if (wcsncmp(buf, L"INSIDE", 6) == 0) {
        PASS("Integration: Scroll moved INSIDE to row 30");
    } else {
        FAIL("int_scroll_move", "mismatch");
    }
    
    // Row 33 should still have MARK33
    ReadConsoleOutputCharacterW(hOut, buf, 6, (COORD){0, 33}, &read);
    if (wcsncmp(buf, L"MARK33", 6) == 0) {
        PASS("Integration: MARK33 preserved outside scroll region");
    } else {
        FAIL("int_scroll_preserve", "MARK33 lost");
    }
    
    flush_messages();
}

void test_rapid_attribute_changes(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD written;
    
    // Rapidly change attributes and write
    SetConsoleCursorPosition(hOut, (COORD){0, 35});
    
    WORD colors[] = {0x0C, 0x0A, 0x09, 0x0E, 0x0D, 0x0B};
    WCHAR chars[] = {L'R', L'G', L'B', L'Y', L'M', L'C'};
    
    for (int i = 0; i < 6; i++) {
        SetConsoleTextAttribute(hOut, colors[i]);
        WriteConsoleW(hOut, &chars[i], 1, &written, NULL);
    }
    
    // Read back and verify each cell has the correct character and attribute
    WCHAR text[6] = {0};
    WORD attrs[6];
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, text, 6, (COORD){0, 35}, &read);
    ReadConsoleOutputAttribute(hOut, attrs, 6, (COORD){0, 35}, &read);
    
    int match = 1;
    for (int i = 0; i < 6; i++) {
        if (text[i] != chars[i]) match = 0;
        if (attrs[i] != colors[i]) match = 0;
    }
    
    if (match) {
        PASS("Integration: Rapid attribute changes — all 6 chars and colors match");
    } else {
        FAIL("int_rapid_attr", "mismatch in rapid attribute sequence");
    }
    
    flush_messages();
}

void test_large_write_and_read(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Write 80 characters to fill a row
    SetConsoleCursorPosition(hOut, (COORD){0, 37});
    SetConsoleTextAttribute(hOut, 0x07);
    
    WCHAR line[80];
    for (int i = 0; i < 80; i++) {
        line[i] = L'A' + (i % 26);
    }
    
    DWORD written;
    WriteConsoleW(hOut, line, 80, &written, NULL);
    
    // Read back immediately (before any printf)
    WCHAR buf[80] = {0};
    DWORD read;
    ReadConsoleOutputCharacterW(hOut, buf, 80, (COORD){0, 37}, &read);
    
    int match = 1;
    for (int i = 0; i < 80 && i < (int)read; i++) {
        if (buf[i] != line[i]) { match = 0; break; }
    }
    
    if (read == 80 && match) {
        PASS("Integration: 80-char write → read round-trip matches");
    } else {
        FAIL("int_large_write", "mismatch");
    }
    
    flush_messages();
}

int main(void) {
    printf("=== Integration Tests ===\n\n"); fflush(stdout);
    
    test_colored_write_scroll_readback();
    test_cursor_movement_pattern();
    test_write_output_read_roundtrip();
    test_title_and_codepage();
    test_scroll_preserves_outside();
    test_rapid_attribute_changes();
    test_large_write_and_read();
    
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
