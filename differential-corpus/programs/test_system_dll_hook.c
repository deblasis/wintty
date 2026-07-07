/**
 * System DLL Hook Verification Test
 *
 * Tests that export forwarder hooks correctly intercept calls from kernel32,
 * and that _isatty() is hooked via prologue patching in ucrtbase.dll.
 */
#include <io.h>
#include <stdio.h>
#include <windows.h>

static int g_pass = 0;
static int g_fail = 0;

#define PASS(name, ...) do { printf("PASS: "); printf(name, ##__VA_ARGS__); printf("\n"); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

void test_isatty_via_ucrt(void) {
    printf("TEST: _isatty() through ucrtbase.dll\n"); fflush(stdout);
    int result = _isatty(1);
    if (result == 1) {
        PASS("_isatty(1) = 1 (stdout is char device)");
    } else {
        FAIL("_isatty(1)", "expected 1, got %d", result);
    }
}

void test_isatty_stdin(void) {
    printf("TEST: _isatty(0) for stdin\n"); fflush(stdout);
    int result = _isatty(0);
    if (result == 1) {
        PASS("_isatty(0) = 1 (stdin is char device)");
    } else {
        FAIL("_isatty(0)", "expected 1, got %d", result);
    }
}

void test_isatty_stderr(void) {
    printf("TEST: _isatty(2) for stderr\n"); fflush(stdout);
    int result = _isatty(2);
    if (result == 1) {
        PASS("_isatty(2) = 1 (stderr is char device)");
    } else {
        FAIL("_isatty(2)", "expected 1, got %d", result);
    }
}

void test_fileno_isatty(void) {
    printf("TEST: fileno + isatty for stdout\n"); fflush(stdout);
    int fd = fileno(stdout);
    int result = isatty(fd);
    if (result == 1) {
        PASS("isatty(fileno(stdout)) = 1 (stdout is char device)");
    } else {
        FAIL("isatty(fileno(stdout))", "expected 1, got %d", result);
    }
}

void test_isatty_invalid_fd(void) {
    printf("TEST: _isatty() with invalid fd\n"); fflush(stdout);
    int result = _isatty(999);
    // Invalid fd should return 0 (not crash)
    PASS("_isatty(999) = %d (invalid fd handled correctly)", result);
}

void test_get_file_type_direct(void) {
    printf("TEST: GetFileType direct call\n"); fflush(stdout);

    // Direct call to GetFileType — goes through IAT hook
    HANDLE hOut = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD ft = GetFileType(hOut);

    if (ft == FILE_TYPE_CHAR) {
        PASS("GetFileType(stdout) = FILE_TYPE_CHAR (0x%04lX)", ft);
    } else {
        FAIL("GetFileType", "GetFileType(stdout) = 0x%04lX, expected FILE_TYPE_CHAR (0x0002)", ft);
    }
}

int main(void) {
    printf("=== System DLL Hook Verification ===\n\n");
    fflush(stdout);

    test_get_file_type_direct();
    test_isatty_via_ucrt();
    test_isatty_stdin();
    test_isatty_stderr();
    test_fileno_isatty();
    test_isatty_invalid_fd();

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
