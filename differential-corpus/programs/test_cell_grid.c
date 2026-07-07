/**
 * Cell Grid Integration Test
 *
 * Comprehensive test that writes data via various APIs, reads it back,
 * and verifies the cell grid correctly tracks all mutations.
 *
 * Run: wintty-pcon-inject.exe test_cell_grid.exe
 * Exit 0 = all pass, non-zero = failures
 */

#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>

static int g_pass = 0;
static int g_fail = 0;

#define TEST(name) printf("TEST: %s\n", name); fflush(stdout)
#define PASS(name, ...) do { printf("PASS: "); printf(name, ##__VA_ARGS__); printf("\n"); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

void test_writeconsole_wide_readback(void) {
    TEST("WriteConsoleW -> ReadConsoleOutputCharacterW");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    COORD pos = {0, 0};
    SetConsoleCursorPosition(hOut, pos);
    SetConsoleTextAttribute(hOut, 0x07);

    const wchar_t *text = L"ABCDE";
    DWORD written = 0;
    WriteConsoleW(hOut, text, 5, &written, NULL);

    wchar_t buf[8] = {0};
    DWORD read = 0;
    BOOL ok = ReadConsoleOutputCharacterW(hOut, buf, 5, pos, &read);

    if (ok && read == 5 && wmemcmp(buf, text, 5) == 0) {
        PASS("WriteConsoleW -> ReadConsoleOutputCharacterW: '%ls' == '%ls'", buf, text);
    } else {
        FAIL("round-trip", "ok=%d read=%lu got='%.*ls'", ok, read, (int)read, buf);
    }
}

void test_writeconsolea_readback(void) {
    TEST("WriteConsoleA -> ReadConsoleOutputCharacterA");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    COORD pos = {10, 0};
    SetConsoleCursorPosition(hOut, pos);

    const char *text = "HELLO";
    DWORD written = 0;
    WriteConsoleA(hOut, text, 5, &written, NULL);

    char buf[8] = {0};
    DWORD read = 0;
    BOOL ok = ReadConsoleOutputCharacterA(hOut, buf, 5, pos, &read);

    if (ok && read == 5 && memcmp(buf, text, 5) == 0) {
        PASS("WriteConsoleA -> ReadConsoleOutputCharacterA: '%s' == '%s'", buf, text);
    } else {
        FAIL("round-trip", "ok=%d read=%lu got='%.*s'", ok, read, (int)read, buf);
    }
}

void test_fill_char_readback(void) {
    TEST("FillConsoleOutputCharacterW -> ReadConsoleOutputCharacterW");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    COORD pos = {0, 2};
    DWORD written = 0;
    FillConsoleOutputCharacterW(hOut, L'X', 20, pos, &written);

    wchar_t buf[24] = {0};
    DWORD read = 0;
    BOOL ok = ReadConsoleOutputCharacterW(hOut, buf, 20, pos, &read);

    int match = 1;
    for (int i = 0; i < 20; i++) {
        if (buf[i] != L'X') { match = 0; break; }
    }
    if (ok && read == 20 && match) {
        PASS("FillConsoleOutputCharacterW: 20 'X' chars verified");
    } else {
        FAIL("fill round-trip", "ok=%d read=%lu match=%d", ok, read, match);
    }
}

void test_fill_attr_readback(void) {
    TEST("FillConsoleOutputAttribute -> ReadConsoleOutputAttribute");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    COORD pos = {0, 3};
    // First fill some characters so the cells have content
    DWORD written = 0;
    FillConsoleOutputCharacterW(hOut, L'A', 10, pos, &written);
    // Now fill attributes
    FillConsoleOutputAttribute(hOut, 0x0C, 10, pos, &written); // red on black

    WORD attrs[10] = {0};
    DWORD read = 0;
    BOOL ok = ReadConsoleOutputAttribute(hOut, attrs, 10, pos, &read);

    int match = 1;
    for (int i = 0; i < 10; i++) {
        if (attrs[i] != 0x0C) { match = 0; break; }
    }
    if (ok && read == 10 && match) {
        PASS("FillConsoleOutputAttribute -> ReadConsoleOutputAttribute: 10 attrs = 0x0C");
    } else {
        FAIL("attr round-trip", "ok=%d read=%lu match=%d first=%04X", ok, read, match, attrs[0]);
    }
}

void test_writeoutput_readoutput(void) {
    TEST("WriteConsoleOutputW -> ReadConsoleOutputW");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    // Write a 3x2 block with unique chars and attrs
    CHAR_INFO src[6] = {
        {{L'1'}, 0x07}, {{L'2'}, 0x0A}, {{L'3'}, 0x0C},
        {{L'4'}, 0x09}, {{L'5'}, 0x0E}, {{L'6'}, 0x0B},
    };
    COORD bufSize = {3, 2};
    COORD bufCoord = {0, 0};
    SMALL_RECT region = {0, 5, 2, 6};
    BOOL ok = WriteConsoleOutputW(hOut, src, bufSize, bufCoord, &region);
    if (!ok) {
        FAIL("write", "WriteConsoleOutputW failed");
        return;
    }

    // Read back
    CHAR_INFO dst[6] = {0};
    SMALL_RECT read_region = {0, 5, 2, 6};
    ok = ReadConsoleOutputW(hOut, dst, bufSize, bufCoord, &read_region);
    if (!ok) {
        FAIL("read", "ReadConsoleOutputW failed");
        return;
    }

    int chars_match = 1, attrs_match = 1;
    for (int i = 0; i < 6; i++) {
        if (dst[i].Char.UnicodeChar != src[i].Char.UnicodeChar) chars_match = 0;
        if (dst[i].Attributes != src[i].Attributes) attrs_match = 0;
    }
    if (chars_match && attrs_match) {
        PASS("WriteConsoleOutputW -> ReadConsoleOutputW: 3x2 block chars+attrs match");
    } else {
        FAIL("block round-trip", "chars_match=%d attrs_match=%d", chars_match, attrs_match);
    }
}

void test_write_char_at_coord(void) {
    TEST("WriteConsoleOutputCharacterW -> ReadConsoleOutputCharacterW at coord");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    COORD pos = {5, 8};
    const wchar_t *text = L"COORD";
    DWORD written = 0;
    BOOL ok = WriteConsoleOutputCharacterW(hOut, text, 5, pos, &written);
    if (!ok) {
        FAIL("write", "WriteConsoleOutputCharacterW failed");
        return;
    }

    wchar_t buf[8] = {0};
    DWORD read = 0;
    ok = ReadConsoleOutputCharacterW(hOut, buf, 5, pos, &read);
    if (ok && read == 5 && wmemcmp(buf, text, 5) == 0) {
        PASS("WriteConsoleOutputCharacterW at (5,8): '%ls' round-trip OK", buf);
    } else {
        FAIL("coord round-trip", "ok=%d read=%lu got='%.*ls'", ok, read, (int)read, buf);
    }
}

void test_write_attrs_readback(void) {
    TEST("WriteConsoleOutputAttribute -> ReadConsoleOutputAttribute");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    COORD pos = {0, 10};
    // Fill chars first
    DWORD written = 0;
    FillConsoleOutputCharacterW(hOut, L'Z', 5, pos, &written);

    WORD write_attrs[] = {0x01, 0x02, 0x03, 0x04, 0x05};
    WriteConsoleOutputAttribute(hOut, write_attrs, 5, pos, &written);

    WORD read_attrs[5] = {0};
    DWORD read = 0;
    BOOL ok = ReadConsoleOutputAttribute(hOut, read_attrs, 5, pos, &read);
    if (ok && read == 5 &&
        read_attrs[0] == 0x01 && read_attrs[1] == 0x02 &&
        read_attrs[2] == 0x03 && read_attrs[3] == 0x04 &&
        read_attrs[4] == 0x05) {
        PASS("WriteConsoleOutputAttribute: 5 individual attrs round-trip OK");
    } else {
        FAIL("attr round-trip", "ok=%d read=%lu got=%02X %02X %02X %02X %02X",
            ok, read, read_attrs[0], read_attrs[1], read_attrs[2], read_attrs[3], read_attrs[4]);
    }
}

void test_set_attr_then_write(void) {
    TEST("SetConsoleTextAttribute -> WriteConsole -> ReadConsoleOutputAttribute");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    COORD pos = {0, 12};
    SetConsoleCursorPosition(hOut, pos);
    SetConsoleTextAttribute(hOut, 0x0A); // green on black

    const wchar_t *text = L"GREEN";
    DWORD written = 0;
    WriteConsoleW(hOut, text, 5, &written, NULL);

    WORD attrs[5] = {0};
    DWORD read = 0;
    BOOL ok = ReadConsoleOutputAttribute(hOut, attrs, 5, pos, &read);
    int match = 1;
    for (int i = 0; i < 5; i++) {
        if (attrs[i] != 0x0A) { match = 0; break; }
    }
    if (ok && read == 5 && match) {
        PASS("SetConsoleTextAttribute(0x0A) -> WriteConsole -> attrs all 0x0A");
    } else {
        FAIL("attr flow", "ok=%d read=%lu match=%d first=%04X", ok, read, match, attrs[0]);
    }

    // Reset
    SetConsoleTextAttribute(hOut, 0x07);
}

void test_overwrite_cells(void) {
    TEST("Overwrite cells: write -> verify -> overwrite -> verify again");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    COORD pos = {0, 14};

    // Write "AAAAA"
    FillConsoleOutputCharacterW(hOut, L'A', 5, pos, NULL);
    wchar_t buf[8] = {0};
    DWORD read = 0;
    ReadConsoleOutputCharacterW(hOut, buf, 5, pos, &read);
    if (buf[0] != L'A' || buf[4] != L'A') {
        FAIL("first write", "expected AAAAA, got %lc...%lc", buf[0], buf[4]);
        return;
    }

    // Overwrite with "BBBBB"
    FillConsoleOutputCharacterW(hOut, L'B', 5, pos, NULL);
    ReadConsoleOutputCharacterW(hOut, buf, 5, pos, &read);
    if (buf[0] == L'B' && buf[4] == L'B') {
        PASS("Overwrite: AAAAA -> BBBBB verified");
    } else {
        FAIL("overwrite", "expected BBBBB, got %lc...%lc", buf[0], buf[4]);
    }
}

void test_writefile_readback(void) {
    TEST("WriteFile on console -> ReadConsoleOutputCharacterW");
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);

    COORD pos = {0, 16};
    SetConsoleCursorPosition(hOut, pos);

    const char *text = "FILEIO";
    DWORD written = 0;
    WriteFile(hOut, text, 6, &written, NULL);

    wchar_t buf[8] = {0};
    DWORD read = 0;
    BOOL ok = ReadConsoleOutputCharacterW(hOut, buf, 6, pos, &read);

    // Convert expected to wide for comparison
    wchar_t expected[8] = {0};
    for (int i = 0; i < 6; i++) expected[i] = (wchar_t)text[i];

    if (ok && read == 6 && wmemcmp(buf, expected, 6) == 0) {
        PASS("WriteFile -> ReadConsoleOutputCharacterW: '%ls' round-trip OK", buf);
    } else {
        FAIL("writefile round-trip", "ok=%d read=%lu", ok, read);
    }
}

int main(void) {
    printf("=== Cell Grid Integration Test Suite ===\n\n");
    fflush(stdout);

    test_writeconsole_wide_readback();
    test_writeconsolea_readback();
    test_fill_char_readback();
    test_fill_attr_readback();
    test_writeoutput_readoutput();
    test_write_char_at_coord();
    test_write_attrs_readback();
    test_set_attr_then_write();
    test_overwrite_cells();
    test_writefile_readback();

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
