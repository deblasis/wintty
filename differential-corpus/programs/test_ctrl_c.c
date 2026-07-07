/**
 * test_ctrl_c.c — Ctrl+C signal delivery conformance test
 *
 * Tests:
 * 1. SetConsoleCtrlHandler registers a handler
 * 2. GenerateConsoleCtrlEvent(CTRL_C_EVENT) calls the handler
 * 3. Handler receives CTRL_C_EVENT (0) and CTRL_BREAK_EVENT (1)
 * 4. Multiple handlers are called in LIFO order
 * 5. Handler returning TRUE prevents default termination
 * 6. NULL handler with Add=TRUE sets ignore mode
 * 7. Removing handler works
 */

#include <windows.h>
#include <stdio.h>

static int g_pass = 0, g_fail = 0;
#define PASS(name, ...) do { printf("PASS: " name "\n", ##__VA_ARGS__); g_pass++; fflush(stdout); } while(0)
#define FAIL(name, ...) do { printf("FAIL: %s: ", name); printf(__VA_ARGS__); printf("\n"); g_fail++; fflush(stdout); } while(0)

// ─── Test 1: Basic handler registration ────────────────────────────────

static volatile LONG g_handler1_called = 0;
static DWORD g_handler1_event = 0xFF;

static BOOL WINAPI handler1(DWORD ctrl_type) {
    InterlockedExchange(&g_handler1_called, 1);
    g_handler1_event = ctrl_type;
    return TRUE; // Handled
}

static void test_register_handler(void) {
    printf("--- test_register_handler ---\n"); fflush(stdout);
    g_handler1_called = 0;
    g_handler1_event = 0xFF;

    BOOL ok = SetConsoleCtrlHandler(handler1, TRUE);
    if (!ok) { FAIL("register", "SetConsoleCtrlHandler returned %d", ok); return; }

    // Trigger via GenerateConsoleCtrlEvent
    ok = GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
    if (!ok) { FAIL("generate", "GenerateConsoleCtrlEvent returned %d", ok); return; }

    // Small delay to let handler execute (it runs on a separate thread in real Windows,
    // but our hook calls it synchronously)
    Sleep(50);

    if (g_handler1_called == 1) {
        PASS("handler was called");
    } else {
        FAIL("handler", "handler1_called = %d, expected 1", g_handler1_called);
    }

    if (g_handler1_event == CTRL_C_EVENT) {
        PASS("handler received CTRL_C_EVENT (%d)", CTRL_C_EVENT);
    } else {
        FAIL("event", "handler received %d, expected CTRL_C_EVENT (%d)", g_handler1_event, CTRL_C_EVENT);
    }

    // Clean up
    SetConsoleCtrlHandler(handler1, FALSE);
}

// ─── Test 2: CTRL_BREAK_EVENT ─────────────────────────────────────────

static volatile LONG g_handler2_called = 0;
static DWORD g_handler2_event = 0xFF;

static BOOL WINAPI handler2(DWORD ctrl_type) {
    InterlockedExchange(&g_handler2_called, 1);
    g_handler2_event = ctrl_type;
    return TRUE;
}

static void test_ctrl_break_event(void) {
    printf("--- test_ctrl_break_event ---\n"); fflush(stdout);
    g_handler2_called = 0;
    g_handler2_event = 0xFF;

    SetConsoleCtrlHandler(handler2, TRUE);
    BOOL ok = GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, 0);
    if (!ok) { FAIL("generate", "GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT) returned %d", ok); return; }

    Sleep(50);

    if (g_handler2_called == 1) {
        PASS("handler was called for CTRL_BREAK_EVENT");
    } else {
        FAIL("handler", "handler2_called = %d, expected 1", g_handler2_called);
    }

    if (g_handler2_event == CTRL_BREAK_EVENT) {
        PASS("handler received CTRL_BREAK_EVENT (%d)", CTRL_BREAK_EVENT);
    } else {
        FAIL("event", "handler received %d, expected CTRL_BREAK_EVENT (%d)", g_handler2_event, CTRL_BREAK_EVENT);
    }

    SetConsoleCtrlHandler(handler2, FALSE);
}

// ─── Test 3: Multiple handlers (LIFO order) ────────────────────────────

static volatile LONG g_handler_a_called = 0;
static volatile LONG g_handler_b_called = 0;
static volatile LONG g_handler_a_order = 0;
static volatile LONG g_handler_b_order = 0;
static volatile LONG g_order_counter = 0;

static BOOL WINAPI handler_a(DWORD ctrl_type) {
    InterlockedExchange(&g_handler_a_called, 1);
    g_handler_a_order = InterlockedIncrement(&g_order_counter);
    return TRUE; // Consumes the event
}

static BOOL WINAPI handler_b(DWORD ctrl_type) {
    InterlockedExchange(&g_handler_b_called, 1);
    g_handler_b_order = InterlockedIncrement(&g_order_counter);
    return FALSE; // Does NOT consume — next handler should be called
}

static void test_multiple_handlers(void) {
    printf("--- test_multiple_handlers ---\n"); fflush(stdout);
    g_handler_a_called = 0;
    g_handler_b_called = 0;
    g_handler_a_order = 0;
    g_handler_b_order = 0;
    g_order_counter = 0;

    // Register A first, then B. B is "on top" (LIFO).
    SetConsoleCtrlHandler(handler_a, TRUE);
    SetConsoleCtrlHandler(handler_b, TRUE);

    GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
    Sleep(50);

    if (g_handler_b_called && g_handler_a_called) {
        PASS("both handlers were called");
    } else {
        FAIL("handlers", "b=%d a=%d, expected both=1", g_handler_b_called, g_handler_a_called);
    }

    // B should be called first (LIFO — last registered = first called)
    if (g_handler_b_order > 0 && g_handler_a_order > g_handler_b_order) {
        PASS("handlers called in LIFO order (b=%d before a=%d)", g_handler_b_order, g_handler_a_order);
    } else {
        FAIL("order", "b_order=%d a_order=%d", g_handler_b_order, g_handler_a_order);
    }

    SetConsoleCtrlHandler(handler_b, FALSE);
    SetConsoleCtrlHandler(handler_a, FALSE);
}

// ─── Test 4: Handler returning TRUE stops propagation ────────────────

static volatile LONG g_handler_stop_called = 0;
static volatile LONG g_handler_after_called = 0;

static BOOL WINAPI handler_stop(DWORD ctrl_type) {
    InterlockedExchange(&g_handler_stop_called, 1);
    return TRUE; // Stops propagation
}

static BOOL WINAPI handler_after(DWORD ctrl_type) {
    InterlockedExchange(&g_handler_after_called, 1);
    return TRUE;
}

static void test_handler_stops_propagation(void) {
    printf("--- test_handler_stops_propagation ---\n"); fflush(stdout);
    g_handler_stop_called = 0;
    g_handler_after_called = 0;

    // Register "after" first, then "stop" on top (LIFO).
    // stop returns TRUE, so after should still be called (we call all handlers
    // in LIFO order until one returns TRUE — then stop).
    SetConsoleCtrlHandler(handler_after, TRUE);
    SetConsoleCtrlHandler(handler_stop, TRUE);

    GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
    Sleep(50);

    if (g_handler_stop_called) {
        PASS("stop handler was called");
    } else {
        FAIL("stop", "handler_stop not called");
    }

    // handler_after was registered BEFORE handler_stop (lower in stack),
    // so handler_stop is called first (LIFO). Since it returns TRUE,
    // handler_after should NOT be called.
    if (!g_handler_after_called) {
        PASS("handler_after was NOT called (propagation stopped by TRUE)");
    } else {
        FAIL("propagation", "handler_after WAS called despite stop returning TRUE");
    }

    SetConsoleCtrlHandler(handler_stop, FALSE);
    SetConsoleCtrlHandler(handler_after, FALSE);
}

// ─── Test 5: Remove handler ────────────────────────────────────────────

static volatile LONG g_handler_remove_called = 0;
static volatile LONG g_handler_fallback_called = 0;

static BOOL WINAPI handler_remove(DWORD ctrl_type) {
    InterlockedExchange(&g_handler_remove_called, 1);
    return TRUE;
}

static BOOL WINAPI handler_fallback(DWORD ctrl_type) {
    InterlockedExchange(&g_handler_fallback_called, 1);
    return TRUE; // Prevent process termination
}

static void test_remove_handler(void) {
    printf("--- test_remove_handler ---\n"); fflush(stdout);

    // Register a fallback handler first (stays registered to prevent termination)
    SetConsoleCtrlHandler(handler_fallback, TRUE);

    // Register and then remove the test handler
    SetConsoleCtrlHandler(handler_remove, TRUE);
    SetConsoleCtrlHandler(handler_remove, FALSE);

    g_handler_remove_called = 0;
    g_handler_fallback_called = 0;
    GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
    Sleep(50);

    if (!g_handler_remove_called) {
        PASS("removed handler was NOT called");
    } else {
        FAIL("remove", "handler was called after removal");
    }

    // Fallback should have been called since the removed handler was not
    if (g_handler_fallback_called) {
        PASS("fallback handler was called (proving removed handler was skipped)");
    } else {
        FAIL("fallback", "fallback handler was not called");
    }

    SetConsoleCtrlHandler(handler_fallback, FALSE);
}

// ─── Test 6: Invalid event type rejected ────────────────────────────────

static void test_invalid_event(void) {
    printf("--- test_invalid_event ---\n"); fflush(stdout);

    BOOL ok = GenerateConsoleCtrlEvent(99, 0);
    if (!ok) {
        PASS("GenerateConsoleCtrlEvent(99) returns FALSE");
    } else {
        FAIL("invalid", "GenerateConsoleCtrlEvent(99) returned TRUE");
    }
}

int main(void) {
    printf("=== Ctrl+C Signal Delivery Tests ===\n\n"); fflush(stdout);

    test_register_handler();
    test_ctrl_break_event();
    test_multiple_handlers();
    test_handler_stops_propagation();
    test_remove_handler();
    test_invalid_event();

    printf("\n=== RESULTS: %d passed, %d failed ===\n", g_pass, g_fail);
    return g_fail;
}
