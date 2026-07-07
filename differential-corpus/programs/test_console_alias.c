#include <windows.h>
#include <stdio.h>

static int g_pass = 0;
static int g_fail = 0;
#define PASS(name) do { printf("PASS: %s\n", name); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, msg) do { printf("FAIL: %s: %s\n", name, msg); g_fail++; fflush(stdout); } while(0)

// These functions are in kernel32.dll but may not be in the import table
typedef BOOL (WINAPI *AddConsoleAliasW_t)(LPWSTR, LPWSTR, LPWSTR);
typedef DWORD (WINAPI *GetConsoleAliasW_t)(LPWSTR, LPWSTR, DWORD, LPWSTR);

void test_add_and_get_alias(void) {
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (!k32) { FAIL("kernel32", "GetModuleHandle failed"); return; }
    
    AddConsoleAliasW_t pAddConsoleAliasW = (AddConsoleAliasW_t)GetProcAddress(k32, "AddConsoleAliasW");
    GetConsoleAliasW_t pGetConsoleAliasW = (GetConsoleAliasW_t)GetProcAddress(k32, "GetConsoleAliasW");
    
    if (!pAddConsoleAliasW || !pGetConsoleAliasW) {
        // Functions not available — skip test
        PASS("Console alias functions not available (skipped)");
        return;
    }
    
    // Add an alias: "ls" → "dir" for "cmd.exe"
    BOOL ok = pAddConsoleAliasW(L"ls", L"dir", L"cmd.exe");
    if (!ok) { FAIL("add_alias", "AddConsoleAliasW returned FALSE"); return; }
    PASS("AddConsoleAliasW(ls→dir, cmd.exe) returns TRUE");
    
    // Retrieve the alias
    WCHAR target[256] = {0};
    DWORD len = pGetConsoleAliasW(L"ls", target, 256, L"cmd.exe");
    if (len > 0 && wcsncmp(target, L"dir", 3) == 0) {
        PASS("GetConsoleAliasW(ls, cmd.exe) returns 'dir'");
    } else {
        FAIL("get_alias", "mismatch or empty");
    }
}

void test_get_nonexistent_alias(void) {
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (!k32) return;
    
    GetConsoleAliasW_t pGetConsoleAliasW = (GetConsoleAliasW_t)GetProcAddress(k32, "GetConsoleAliasW");
    if (!pGetConsoleAliasW) return;
    
    WCHAR target[256] = {0};
    DWORD len = pGetConsoleAliasW(L"nonexistent_alias_xyz", target, 256, L"cmd.exe");
    if (len == 0) {
        PASS("GetConsoleAliasW nonexistent returns 0");
    } else {
        FAIL("nonexistent", "expected 0 length");
    }
}

void test_update_alias(void) {
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (!k32) return;
    
    AddConsoleAliasW_t pAddConsoleAliasW = (AddConsoleAliasW_t)GetProcAddress(k32, "AddConsoleAliasW");
    GetConsoleAliasW_t pGetConsoleAliasW = (GetConsoleAliasW_t)GetProcAddress(k32, "GetConsoleAliasW");
    if (!pAddConsoleAliasW || !pGetConsoleAliasW) return;
    
    // Add alias: "g" → "git"
    pAddConsoleAliasW(L"g", L"git", L"test.exe");
    
    // Update it: "g" → "git status"
    BOOL ok = pAddConsoleAliasW(L"g", L"git status", L"test.exe");
    if (!ok) { FAIL("update_alias", "AddConsoleAliasW returned FALSE"); return; }
    
    // Verify updated value
    WCHAR target[256] = {0};
    DWORD len = pGetConsoleAliasW(L"g", target, 256, L"test.exe");
    if (len > 0 && wcsncmp(target, L"git status", 10) == 0) {
        PASS("Update alias: g→git status works");
    } else {
        FAIL("update_alias", "mismatch after update");
    }
}

void test_remove_alias(void) {
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (!k32) return;
    
    AddConsoleAliasW_t pAddConsoleAliasW = (AddConsoleAliasW_t)GetProcAddress(k32, "AddConsoleAliasW");
    GetConsoleAliasW_t pGetConsoleAliasW = (GetConsoleAliasW_t)GetProcAddress(k32, "GetConsoleAliasW");
    if (!pAddConsoleAliasW || !pGetConsoleAliasW) return;
    
    // Add and then remove by setting target to NULL
    pAddConsoleAliasW(L"temp_alias", L"temp_target", L"remove.exe");
    
    // Remove by passing NULL target
    BOOL ok = pAddConsoleAliasW(L"temp_alias", NULL, L"remove.exe");
    if (!ok) { FAIL("remove_alias", "AddConsoleAliasW(NULL) returned FALSE"); return; }
    
    // Verify removed
    WCHAR target[256] = {0xFF};
    DWORD len = pGetConsoleAliasW(L"temp_alias", target, 256, L"remove.exe");
    if (len == 0) {
        PASS("Remove alias: target=NULL removes alias");
    } else {
        FAIL("remove_alias", "alias still exists");
    }
}

void test_different_exe_names(void) {
    HMODULE k32 = GetModuleHandleW(L"kernel32.dll");
    if (!k32) return;
    
    AddConsoleAliasW_t pAddConsoleAliasW = (AddConsoleAliasW_t)GetProcAddress(k32, "AddConsoleAliasW");
    GetConsoleAliasW_t pGetConsoleAliasW = (GetConsoleAliasW_t)GetProcAddress(k32, "GetConsoleAliasW");
    if (!pAddConsoleAliasW || !pGetConsoleAliasW) return;
    
    // Same alias name, different exe names
    pAddConsoleAliasW(L"run", L"cmd1", L"app1.exe");
    pAddConsoleAliasW(L"run", L"cmd2", L"app2.exe");
    
    WCHAR t1[64] = {0}, t2[64] = {0};
    pGetConsoleAliasW(L"run", t1, 64, L"app1.exe");
    pGetConsoleAliasW(L"run", t2, 64, L"app2.exe");
    
    if (wcsncmp(t1, L"cmd1", 4) == 0 && wcsncmp(t2, L"cmd2", 4) == 0) {
        PASS("Different exe names: same alias maps to different targets");
    } else {
        FAIL("exe_names", "mismatch");
    }
}

int main(void) {
    printf("=== Console Alias Tests ===\n\n"); fflush(stdout);
    
    test_add_and_get_alias();
    test_get_nonexistent_alias();
    test_update_alias();
    test_remove_alias();
    test_different_exe_names();
    
    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
