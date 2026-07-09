/*
 * rawpty_child: the integrated end-to-end child for the P1.2 raw-pipe
 * transport prototype. It exercises all three transport realities in ONE
 * process so the harness can prove they COMPOSE, not just work in isolation:
 *
 *   - RESIZE  : enables in-band resize (DECSET 2048) and, on receiving a
 *               `CSI 48;rows;cols;..t` report on its stdin PIPE, prints
 *               RESIZE:<cols>x<rows>.
 *   - SIGNAL  : registers a console control handler and, on a CTRL_BREAK
 *               event delivered via its inherited console group, prints
 *               SIGNAL:<type>.
 *   - TEARDOWN: forks a grandchild that inherits the stdout pipe, then (after
 *               both resize and signal have landed) prints COMPOSED and sleeps
 *               — so the harness can close the job and observe the whole tree
 *               die (no leak) and the pipe reach EOF (no wedge).
 *
 * Run with "grand" it is the grandchild: it just holds the inherited stdout
 * and sleeps. Text out via WriteFile only; UTF-8 CP set as in production.
 */
#include <windows.h>
#include <string.h>

#define ENABLE_VIRTUAL_TERMINAL_INPUT_ 0x0200

static HANDLE g_evt;
static volatile LONG g_sig_type = -1;

static BOOL WINAPI onCtrl(DWORD t) {
    InterlockedExchange(&g_sig_type, (LONG)t);
    SetEvent(g_evt);
    return TRUE; /* handled: don't let the default action kill us */
}

static void emit(const char *s, DWORD n) {
    DWORD w;
    WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), s, n, &w, NULL);
}
#define W(lit) emit((lit), (DWORD)(sizeof(lit) - 1))

/* Find `CSI 48;rows;cols;..t` in buf; fill cols/rows; return 1 on success. */
static int parse_2048(const char *buf, DWORD len, unsigned *cols, unsigned *rows) {
    for (DWORD i = 0; i + 5 < len; i++) {
        if (buf[i] == 0x1b && buf[i + 1] == '[' &&
            buf[i + 2] == '4' && buf[i + 3] == '8' && buf[i + 4] == ';') {
            DWORD j = i + 5;
            unsigned r = 0, c = 0;
            while (j < len && buf[j] >= '0' && buf[j] <= '9') r = r * 10 + (buf[j++] - '0');
            if (j >= len || buf[j] != ';') continue;
            j++;
            while (j < len && buf[j] >= '0' && buf[j] <= '9') c = c * 10 + (buf[j++] - '0');
            if (r == 0 || c == 0) continue;
            *rows = r;
            *cols = c;
            return 1;
        }
    }
    return 0;
}

int main(int argc, char **argv) {
    if (argc >= 2 && strcmp(argv[1], "grand") == 0) {
        Sleep(30000);
        return 0;
    }

    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);
    HANDLE in = GetStdHandle(STD_INPUT_HANDLE);
    SetConsoleOutputCP(65001);

    g_evt = CreateEventW(NULL, FALSE, FALSE, NULL);
    SetConsoleCtrlHandler(onCtrl, TRUE);

    /* Enable in-band resize; the transport sends reports on our stdin pipe. */
    W("\x1b[?2048h");

    /* Fork a grandchild that inherits our stdout (the transport pipe). */
    unsigned long gpid = 0;
    {
        wchar_t self[MAX_PATH];
        GetModuleFileNameW(NULL, self, MAX_PATH);
        wchar_t cmd[MAX_PATH + 16];
        wsprintfW(cmd, L"\"%s\" grand", self);
        STARTUPINFOW si;
        ZeroMemory(&si, sizeof si);
        si.cb = sizeof si;
        si.dwFlags = STARTF_USESTDHANDLES;
        si.hStdOutput = out;
        si.hStdError = GetStdHandle(STD_ERROR_HANDLE);
        si.hStdInput = in;
        PROCESS_INFORMATION pi;
        ZeroMemory(&pi, sizeof pi);
        if (CreateProcessW(NULL, cmd, NULL, NULL, TRUE, 0, NULL, NULL, &si, &pi)) {
            gpid = pi.dwProcessId;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    char b[128];
    int n = wsprintfA(b, "READY grand=%lu\r\n", gpid);
    emit(b, (DWORD)n);

    char inbuf[1024];
    DWORD held = 0;
    int got_resize = 0, got_signal = 0;
    for (int tries = 0; tries < 100 && !(got_resize && got_signal); tries++) {
        if (!got_signal && WaitForSingleObject(g_evt, 0) == WAIT_OBJECT_0) {
            got_signal = 1;
            n = wsprintfA(b, "SIGNAL:%ld\r\n", (long)g_sig_type);
            emit(b, (DWORD)n);
        }
        if (!got_resize) {
            DWORD avail = 0;
            if (PeekNamedPipe(in, NULL, 0, NULL, &avail, NULL) && avail > 0) {
                DWORD rd = 0, want = sizeof(inbuf) - held;
                if (avail < want) want = avail;
                if (ReadFile(in, inbuf + held, want, &rd, NULL) && rd > 0) {
                    held += rd;
                    unsigned cols = 0, rows = 0;
                    if (parse_2048(inbuf, held, &cols, &rows)) {
                        got_resize = 1;
                        n = wsprintfA(b, "RESIZE:%ux%u\r\n", cols, rows);
                        emit(b, (DWORD)n);
                    } else if (held == sizeof(inbuf)) {
                        held = 0;
                    }
                }
            }
        }
        Sleep(100);
    }

    if (got_resize && got_signal) W("COMPOSED\r\n");

    /* Stay alive (with the grandchild) so the harness can prove teardown. */
    Sleep(30000);
    return 0;
}
