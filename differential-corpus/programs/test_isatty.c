// Test: _isatty() should return 1 for console handles
// This tests that the CRT's _isatty() correctly identifies our console handles.
// 
// Background: ucrtbase.dll caches file handle types in __pioinfo[] during _ioinit().
// Since our DLL is injected after the CRT initializes, the cached values show
// FILE_TYPE_PIPE. We need to either patch __pioinfo or hook _isatty directly.
//
// Expected: _isatty(0), _isatty(1), _isatty(2) all return 1

#include <stdio.h>
#include <io.h>      // _isatty
#include <windows.h>

int main() {
    int pass = 1;
    
    printf("=== CRT _isatty Test Suite ===\n");
    
    // Test 1: _isatty for stdin
    {
        int result = _isatty(0);
        if (result) {
            printf("PASS: _isatty(0) returns %d (console detected)\n", result);
        } else {
            printf("FAIL: _isatty(0) returns 0 (should be console)\n");
            pass = 0;
        }
    }
    
    // Test 2: _isatty for stdout  
    {
        int result = _isatty(1);
        if (result) {
            printf("PASS: _isatty(1) returns %d (console detected)\n", result);
        } else {
            printf("FAIL: _isatty(1) returns 0 (should be console)\n");
            pass = 0;
        }
    }
    
    // Test 3: _isatty for stderr
    {
        int result = _isatty(2);
        if (result) {
            printf("PASS: _isatty(2) returns %d (console detected)\n", result);
        } else {
            printf("FAIL: _isatty(2) returns 0 (should be console)\n");
            pass = 0;
        }
    }
    
    // Test 4: _isatty for non-console fd should return 0
    {
        // Open a file — _isatty should return 0
        HANDLE hFile = CreateFileA("NUL", GENERIC_WRITE, 0, NULL, OPEN_EXISTING, 0, NULL);
        if (hFile != INVALID_HANDLE_VALUE) {
            // We can't easily get an fd from a HANDLE in a portable way,
            // but we can test that _isatty(-1) returns 0
            int result = _isatty(-1);
            if (result == 0) {
                printf("PASS: _isatty(-1) returns 0 (invalid fd)\n");
            } else {
                printf("FAIL: _isatty(-1) returned %d (should be 0)\n", result);
                pass = 0;
            }
            CloseHandle(hFile);
        }
    }
    
    // Test 5: GetFileType returns FILE_TYPE_CHAR for our handles
    {
        HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
        DWORD ft = GetFileType(hOut);
        if (ft == FILE_TYPE_CHAR) {
            printf("PASS: GetFileType(stdout) = FILE_TYPE_CHAR\n");
        } else {
            printf("FAIL: GetFileType(stdout) = 0x%lx (expected FILE_TYPE_CHAR=0x2)\n", ft);
            pass = 0;
        }
    }
    
    printf("\n=== RESULTS: %d passed, %d failed ===\n", 
           pass ? 5 : 0, pass ? 0 : 5);
    
    return pass ? 0 : 1;
}
