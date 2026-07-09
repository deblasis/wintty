/*
 * tree_child: a two-generation process tree for the oracle's teardown-probe.
 *
 * Run with no args it is the CHILD: it prints CHILD:<pid>, spawns a copy of
 * itself as the GRANDCHILD (inheriting its stdout, i.e. the transport pipe),
 * prints GRAND:<pid>, then sleeps a long time. Run with "grand" it is the
 * GRANDCHILD: it just sleeps, holding the inherited stdout handle.
 *
 * Two things get tested with this shape:
 *   1. Tree kill: put the CHILD in a job with KILL_ON_JOB_CLOSE; the
 *      grandchild auto-joins the job, so closing the job handle must kill
 *      BOTH (no grandchild leak) -- the POSIX-equivalent teardown a raw-pipe
 *      transport needs and that ConPTY's own job object blocks.
 *   2. No read-loop wedge: because the grandchild inherits the stdout pipe,
 *      the reader only sees EOF once BOTH die. If teardown leaked the
 *      grandchild, the reader would wedge forever. EOF-after-kill == clean.
 *
 * Deterministic; VT/text out via WriteFile only.
 */
#include <windows.h>
#include <string.h>

static void emit(const char *s, DWORD n) {
    DWORD w;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), s, n, &w, NULL);
}

int main(int argc, char **argv) {
    if (argc >= 2 && strcmp(argv[1], "grand") == 0) {
        /* Grandchild: hold the inherited stdout and live. */
        Sleep(30000);
        return 0;
    }

    char b[64];
    int n = wsprintfA(b, "CHILD:%lu\r\n", (unsigned long)GetCurrentProcessId());
    emit(b, (DWORD)n);

    /* Spawn a grandchild that inherits our stdout (the transport pipe). */
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(NULL, self, MAX_PATH);
    wchar_t cmd[MAX_PATH + 16];
    wsprintfW(cmd, L"\"%s\" grand", self);

    STARTUPINFOW si;
    ZeroMemory(&si, sizeof si);
    si.cb = sizeof si;
    si.dwFlags = STARTF_USESTDHANDLES;
    si.hStdOutput = GetStdHandle(STD_OUTPUT_HANDLE);
    si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    PROCESS_INFORMATION pi;
    ZeroMemory(&pi, sizeof pi);

    BOOL ok = CreateProcessW(NULL, cmd, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi);
    if (ok) {
        n = wsprintfA(b, "GRAND:%lu\r\n", (unsigned long)pi.dwProcessId);
        emit(b, (DWORD)n);
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
    } else {
        emit("GRAND:0\r\n", 9);
    }

    Sleep(30000);
    return 0;
}
