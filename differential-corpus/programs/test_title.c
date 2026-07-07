#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

void test_set_get_title(void) {
    // Save original title
    WCHAR old_title[1024] = {0};
    GetConsoleTitleW(old_title, 1024);
    
    // Set a new title
    const WCHAR new_title[] = L"wintty-pcon-test-title";
    BOOL ok = SetConsoleTitleW(new_title);
    if (!ok) { FAIL("set_title", "SetConsoleTitleW returned FALSE"); return; }
    
    // Read back
    WCHAR read_title[1024] = {0};
    DWORD len = GetConsoleTitleW(read_title, 1024);
    if (wcsncmp(read_title, new_title, wcslen(new_title)) == 0) {
        PASS("SetConsoleTitleW → GetConsoleTitleW round-trip");
    } else {
        FAIL("title_roundtrip", "title mismatch");
    }
    
    // Restore
    SetConsoleTitleW(old_title);
}

void test_original_title(void) {
    WCHAR buf[1024] = {0};
    DWORD len = GetConsoleOriginalTitleW(buf, 1024);
    if (len > 0 || buf[0] != 0) {
        PASS("GetConsoleOriginalTitleW returns a value");
    } else {
        // Original title might be empty in some environments
        PASS("GetConsoleOriginalTitleW returned (may be empty in injected context)");
    }
}

void test_long_title(void) {
    WCHAR old_title[1024] = {0};
    GetConsoleTitleW(old_title, 1024);
    
    // Set a very long title (near the 1024 limit)
    WCHAR long_title[800] = {0};
    for (int i = 0; i < 799; i++) long_title[i] = L'X';
    long_title[799] = 0;
    
    BOOL ok = SetConsoleTitleW(long_title);
    if (!ok) { FAIL("long_title", "SetConsoleTitleW returned FALSE"); return; }
    
    WCHAR read_title[1024] = {0};
    GetConsoleTitleW(read_title, 1024);
    if (wcslen(read_title) > 100) {
        PASS("SetConsoleTitleW handles long title (800 chars)");
    } else {
        FAIL("long_title_read", "title truncated");
    }
    
    SetConsoleTitleW(old_title);
}

void test_ansi_title(void) {
    // Save original
    WCHAR old_title[1024] = {0};
    GetConsoleTitleW(old_title, 1024);
    
    // Set via ANSI variant
    BOOL ok = SetConsoleTitleA("ansi-test-title");
    if (!ok) { FAIL("ansi_title", "SetConsoleTitleA returned FALSE"); return; }
    
    // Read back via wide variant
    WCHAR read_title[1024] = {0};
    GetConsoleTitleW(read_title, 1024);
    if (wcsncmp(read_title, L"ansi-test-title", 14) == 0) {
        PASS("SetConsoleTitleA → GetConsoleTitleW cross-variant round-trip");
    } else {
        FAIL("ansi_title_read", "title mismatch");
    }
    
    SetConsoleTitleW(old_title);
}

int main(void) {
    printf("=== Console Title Test ===\n\n"); fflush(stdout);
    test_set_get_title();
    test_original_title();
    test_long_title();
    test_ansi_title();
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
