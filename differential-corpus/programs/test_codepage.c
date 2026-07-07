#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_default_output_cp(void) {
    UINT cp = GetConsoleOutputCP();
    if (cp == 65001) {
        PASS("GetConsoleOutputCP returns 65001 (UTF-8)");
    } else {
        FAIL("output_cp", "expected 65001");
    }
}

void test_default_input_cp(void) {
    UINT cp = GetConsoleCP();
    if (cp == 65001) {
        PASS("GetConsoleCP returns 65001 (UTF-8)");
    } else {
        FAIL("input_cp", "expected 65001");
    }
}

void test_set_output_cp(void) {
    BOOL ok = SetConsoleOutputCP(1252);
    if (!ok) { FAIL("set_output_cp", "SetConsoleOutputCP returned FALSE"); return; }
    
    UINT cp = GetConsoleOutputCP();
    if (cp == 1252) {
        PASS("SetConsoleOutputCP(1252) → GetConsoleOutputCP returns 1252");
    } else {
        FAIL("set_output_cp_read", "expected 1252");
    }
    
    // Restore to UTF-8
    SetConsoleOutputCP(65001);
}

void test_set_input_cp(void) {
    BOOL ok = SetConsoleCP(437);
    if (!ok) { FAIL("set_input_cp", "SetConsoleCP returned FALSE"); return; }
    
    UINT cp = GetConsoleCP();
    if (cp == 437) {
        PASS("SetConsoleCP(437) → GetConsoleCP returns 437");
    } else {
        FAIL("set_input_cp_read", "expected 437");
    }
    
    // Restore to UTF-8
    SetConsoleCP(65001);
}

void test_cp_roundtrip(void) {
    // Set, get, verify, restore
    SetConsoleOutputCP(437);
    UINT cp1 = GetConsoleOutputCP();
    SetConsoleOutputCP(65001);
    UINT cp2 = GetConsoleOutputCP();
    
    if (cp1 == 437 && cp2 == 65001) {
        PASS("Code page round-trip: 65001→437→65001");
    } else {
        FAIL("cp_roundtrip", "code page not preserved");
    }
}

int main(void) {
    printf("=== Console Code Page Test ===\n\n"); fflush(stdout);
    test_default_output_cp();
    test_default_input_cp();
    test_set_output_cp();
    test_set_input_cp();
    test_cp_roundtrip();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
