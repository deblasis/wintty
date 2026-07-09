/*
 * sig_child: a cross-process Ctrl-C receiver for the oracle's signal-probe.
 *
 * Registers a console control handler, announces READY on stdout, then
 * blocks waiting for a CTRL_C_EVENT / CTRL_BREAK_EVENT. On receipt it prints
 * GOT-SIGNAL:<type> and exits 0; on timeout it prints NO-SIGNAL and exits 2.
 * The handler returns TRUE (event handled) so the default terminate action
 * is suppressed and the child can report cleanly.
 *
 * This is the observable end of the "can we deliver Ctrl-C injection-free"
 * question. Under ConPTY the harness writes 0x03 to the ConPTY input pipe;
 * over a raw pipe the harness runs the AttachConsole courier (ctrlc_helper).
 * Either way, if this child prints GOT-SIGNAL, delivery worked.
 *
 * VT/text out via WriteFile only; deterministic modulo the delivered type.
 */
#include <windows.h>

static HANDLE g_evt;
static volatile LONG g_type = -1;

static BOOL WINAPI onCtrl(DWORD t) {
    InterlockedExchange(&g_type, (LONG)t);
    SetEvent(g_evt);
    return TRUE; /* handled: suppress default termination so we can report */
}

static void emit(const char *s, DWORD n) {
    DWORD w;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), s, n, &w, NULL);
}
#define W(lit) emit((lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    g_evt = CreateEventW(NULL, FALSE, FALSE, NULL);
    if (!SetConsoleCtrlHandler(onCtrl, TRUE)) {
        W("NO-HANDLER\r\n");
        return 3;
    }

    /* Self-report console state so the harness can tell "child has no
     * console" apart from "console exists but courier can't attach". A
     * process with no console has GetConsoleCP()==0; GetConsoleWindow() is
     * NULL for a console with no window (CREATE_NO_WINDOW) but non-NULL for a
     * visible one. */
    {
        char b[96];
        UINT cp = GetConsoleCP();
        HWND hw = GetConsoleWindow();
        int n = wsprintfA(b, "CON: cp=%u win=%p\r\n", cp, (void *)hw);
        emit(b, (DWORD)n);
    }

    W("READY\r\n");

    DWORD r = WaitForSingleObject(g_evt, 5000);
    if (r == WAIT_OBJECT_0) {
        char b[64];
        int n = wsprintfA(b, "GOT-SIGNAL:%ld\r\n", (long)g_type);
        emit(b, (DWORD)n);
        return 0;
    }

    W("NO-SIGNAL\r\n");
    return 2;
}
