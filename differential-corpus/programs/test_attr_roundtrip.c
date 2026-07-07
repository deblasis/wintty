#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_attr_roundtrip(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 5};
    SetConsoleCursorPosition(hOut, pos);

    // Set red foreground, write text
    SetConsoleTextAttribute(hOut, FOREGROUND_RED | FOREGROUND_INTENSITY);
    DWORD written;
    WriteConsoleW(hOut, L"RED", 3, &written, NULL);

    // Set green foreground, write text
    SetConsoleTextAttribute(hOut, FOREGROUND_GREEN | FOREGROUND_INTENSITY);
    WriteConsoleW(hOut, L"GRN", 3, &written, NULL);

    // Read back attributes
    WORD attrs[6];
    DWORD read;
    BOOL ok = ReadConsoleOutputAttribute(hOut, attrs, 6, pos, &read);
    if (!ok || read != 6) {
        FAIL("attr_roundtrip", "ReadConsoleOutputAttribute failed");
        return;
    }

    WORD expected_red = FOREGROUND_RED | FOREGROUND_INTENSITY;
    WORD expected_grn = FOREGROUND_GREEN | FOREGROUND_INTENSITY;

    int correct = 1;
    for (int i = 0; i < 3; i++) {
        if (attrs[i] != expected_red) { correct = 0; break; }
    }
    for (int i = 3; i < 6; i++) {
        if (attrs[i] != expected_grn) { correct = 0; break; }
    }
    if (correct) {
        PASS("Attribute round-trip: RED(3) + GRN(3) colors preserved");
    } else {
        FAIL("attr_roundtrip", "Attributes don't match");
    }
}

void test_fill_attr(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 6};
    SetConsoleCursorPosition(hOut, pos);

    // Write some text first
    DWORD written;
    WriteConsoleW(hOut, L"XXXXXX", 6, &written, NULL);

    // Fill attributes with blue background
    WORD blue_bg = BACKGROUND_BLUE | BACKGROUND_INTENSITY;
    DWORD filled;
    FillConsoleOutputAttribute(hOut, blue_bg, 6, pos, &filled);

    // Read back
    WORD attrs[6];
    DWORD read;
    ReadConsoleOutputAttribute(hOut, attrs, 6, pos, &read);

    int correct = 1;
    for (int i = 0; i < 6; i++) {
        if (attrs[i] != blue_bg) { correct = 0; break; }
    }
    if (correct && filled == 6) {
        PASS("FillConsoleOutputAttribute: blue background set correctly");
    } else {
        FAIL("fill_attr", "Fill attribute mismatch");
    }
}

void test_write_output_attr(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD pos = {0, 7};

    // Write attributes directly
    WORD attrs[] = {
        FOREGROUND_RED,
        FOREGROUND_GREEN,
        FOREGROUND_BLUE,
        FOREGROUND_RED | FOREGROUND_GREEN,
        FOREGROUND_RED | FOREGROUND_BLUE,
        FOREGROUND_GREEN | FOREGROUND_BLUE,
    };
    DWORD written;
    WriteConsoleOutputAttribute(hOut, attrs, 6, pos, &written);

    // Read back
    WORD read_attrs[6];
    DWORD read;
    ReadConsoleOutputAttribute(hOut, read_attrs, 6, pos, &read);

    int correct = 1;
    for (int i = 0; i < 6; i++) {
        if (read_attrs[i] != attrs[i]) { correct = 0; break; }
    }
    if (correct && written == 6) {
        PASS("WriteConsoleOutputAttribute round-trip: 6 distinct colors preserved");
    } else {
        FAIL("write_output_attr", "Attribute mismatch");
    }
}

void test_cell_grid_with_writeconsole_output(void) {
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    
    // Write a CHAR_INFO grid directly
    CHAR_INFO cells[3];
    for (int i = 0; i < 3; i++) {
        cells[i].Char.UnicodeChar = 'A' + i;
        cells[i].Attributes = FOREGROUND_RED | (i * 16); // varying background
    }
    COORD bufSize = {3, 1};
    COORD bufCoord = {0, 0};
    SMALL_RECT writeRegion = {0, 8, 2, 8};
    
    WriteConsoleOutputW(hOut, cells, bufSize, bufCoord, &writeRegion);
    
    // Read back
    CHAR_INFO readCells[3];
    SMALL_RECT readRegion = {0, 8, 2, 8};
    ReadConsoleOutputW(hOut, readCells, bufSize, bufCoord, &readRegion);
    
    int correct = 1;
    for (int i = 0; i < 3; i++) {
        if (readCells[i].Char.UnicodeChar != cells[i].Char.UnicodeChar) { correct = 0; break; }
        if (readCells[i].Attributes != cells[i].Attributes) { correct = 0; break; }
    }
    if (correct) {
        PASS("WriteConsoleOutputW → ReadConsoleOutputW round-trip: chars+attrs preserved");
    } else {
        FAIL("cell_grid_output", "Round-trip mismatch");
    }
}

int main(void) {
    printf("=== Attribute Round-Trip Test ===\n\n"); fflush(stdout);
    test_attr_roundtrip();
    test_fill_attr();
    test_write_output_attr();
    test_cell_grid_with_writeconsole_output();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
