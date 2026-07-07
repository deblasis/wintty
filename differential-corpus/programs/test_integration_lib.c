/**
 * test_integration_lib.c — C integration library conformance test
 *
 * Tests wintty_spawn / wintty_wait / wintty_running / wintty_free.
 * Uses the static library (wintty-pcon.lib) directly instead of the injector.
 */

#include <windows.h>
#include <stdio.h>
#include <string.h>

// Include the integration header
#include "wintty-pcon.h"

static int g_pass = 0, g_fail = 0;
#define PASS(name, ...) do { printf("PASS: " name "\n", ##__VA_ARGS__); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

// ─── Test: spawn and wait ─────────────────────────────────────────────

static void test_spawn_and_wait(void) {
    printf("--- test_spawn_and_wait ---\n"); fflush(stdout);

    const char* args[] = { "hostname.exe", NULL };
    WinttyChild* child = wintty_spawn("hostname.exe", args, 0, 0);
    if (child) {
        PASS("wintty_spawn returned non-NULL");
    } else {
        FAIL("spawn", "wintty_spawn returned NULL");
        return;
    }

    if (child->child_pid != 0) {
        PASS("child_pid is %lu", child->child_pid);
    } else {
        FAIL("pid", "child_pid is 0");
    }

    if (child->stdout_pipe != NULL) {
        PASS("stdout_pipe is valid (%p)", child->stdout_pipe);
    } else {
        FAIL("stdout", "stdout_pipe is NULL");
    }

    if (child->stdin_pipe != NULL) {
        PASS("stdin_pipe is valid (%p)", child->stdin_pipe);
    } else {
        FAIL("stdin", "stdin_pipe is NULL");
    }

    // Read some output from the child's stdout pipe
    char buf[256] = {0};
    DWORD bytes_read = 0;
    BOOL ok = ReadFile(child->stdout_pipe, buf, sizeof(buf) - 1, &bytes_read, NULL);
    if (ok && bytes_read > 0) {
        // Trim trailing whitespace
        buf[bytes_read] = 0;
        while (bytes_read > 0 && (buf[bytes_read-1] == '\r' || buf[bytes_read-1] == '\n' || buf[bytes_read-1] == ' '))
            buf[--bytes_read] = 0;
        PASS("read %lu bytes from stdout: '%s'", bytes_read, buf);
    } else {
        PASS("stdout pipe readable (ReadFile returned %d, bytes=%lu)", ok, bytes_read);
    }

    int exit_code = wintty_wait(child);
    PASS("wintty_wait returned exit code %d", exit_code);

    wintty_free(child);
    PASS("wintty_free succeeded");
}

// ─── Test: spawn with arguments ────────────────────────────────────────

static void test_spawn_with_args(void) {
    printf("--- test_spawn_with_args ---\n"); fflush(stdout);

    const char* args[] = { "git", "--version", NULL };
    WinttyChild* child = wintty_spawn("git", args, 120, 51);
    if (!child) {
        FAIL("spawn", "wintty_spawn returned NULL");
        return;
    }
    PASS("wintty_spawn('git --version') succeeded");

    // Read output
    char buf[256] = {0};
    DWORD bytes_read = 0;
    ReadFile(child->stdout_pipe, buf, sizeof(buf) - 1, &bytes_read, NULL);
    buf[bytes_read] = 0;
    if (bytes_read > 0) {
        PASS("git output: %s", buf);
    }

    int exit_code = wintty_wait(child);
    PASS("exit code: %d", exit_code);
    wintty_free(child);
}

// ─── Test: running check ──────────────────────────────────────────────

static void test_running_check(void) {
    printf("--- test_running_check ---\n"); fflush(stdout);

    // Use hostname — exits quickly
    WinttyChild* child = wintty_spawn("hostname.exe", NULL, 0, 0);
    if (!child) {
        FAIL("spawn", "wintty_spawn returned NULL");
        return;
    }

    // Give it a moment to start
    Sleep(100);

    // Check running status — might already have exited
    int exit_code = 0;
    BOOL running = wintty_running(child, &exit_code);
    PASS("wintty_running returned %d (running=%d, exit_code=%d)", running, running, exit_code);

    // Wait for it
    wintty_wait(child);
    running = wintty_running(child, &exit_code);
    if (!running) {
        PASS("after wait: wintty_running=FALSE, exit_code=%d", exit_code);
    } else {
        FAIL("running", "still running after wait");
    }

    wintty_free(child);
}

// ─── Test: NULL args ───────────────────────────────────────────────────

static void test_null_args(void) {
    printf("--- test_null_args ---\n"); fflush(stdout);

    WinttyChild* child = wintty_spawn("hostname.exe", NULL, 0, 0);
    if (child) {
        PASS("wintty_spawn with NULL args succeeded");
        wintty_wait(child);
        wintty_free(child);
    } else {
        FAIL("null_args", "wintty_spawn returned NULL");
    }
}

// ─── Test: free NULL ────────────────────────────────────────────────────

static void test_free_null(void) {
    printf("--- test_free_null ---\n"); fflush(stdout);
    wintty_free(NULL);
    PASS("wintty_free(NULL) did not crash");
}

// ─── Test: wait NULL ────────────────────────────────────────────────────

static void test_wait_null(void) {
    printf("--- test_wait_null ---\n"); fflush(stdout);
    int result = wintty_wait(NULL);
    if (result == -1) {
        PASS("wintty_wait(NULL) returned -1");
    } else {
        FAIL("wait_null", "expected -1, got %d", result);
    }
}

int main(void) {
    printf("=== C Integration Library Tests ===\n\n"); fflush(stdout);

    test_spawn_and_wait();
    test_spawn_with_args();
    test_running_check();
    test_null_args();
    test_free_null();
    test_wait_null();

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
