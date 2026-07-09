/*
 * ctrlc_helper: an injection-free Ctrl-C courier for a raw-pipe transport.
 *
 * A raw-pipe transport has no shared console with the child, so it cannot
 * call GenerateConsoleCtrlEvent against the child's process group directly
 * (that requires being attached to the same console). The injection-free
 * trick, used by winpty/node/python subprocess: spawn a throwaway helper
 * that detaches from its own console, attaches to the TARGET's console, then
 * generates the control event there. Group 0 signals every process attached
 * to that console -- the child and its whole tree. The helper suppresses the
 * event in itself (SetConsoleCtrlHandler(NULL, TRUE)) so it isn't killed, and
 * because the terminal process never attached to the child's console, the
 * terminal is untouched. No DLLs, no hooks, no injection.
 *
 * usage: ctrlc_helper <pid> <C|B>
 *   exit 0  = event generated
 *   exit 2  = bad args
 *   exit 10 = AttachConsole(<pid>) failed (child has no attachable console)
 *   exit 11 = GenerateConsoleCtrlEvent failed
 */
#include <windows.h>
#include <stdlib.h>

int main(int argc, char **argv) {
    if (argc < 3) return 2;
    DWORD pid = (DWORD)strtoul(argv[1], NULL, 10);
    DWORD evt = (argv[2][0] == 'B' || argv[2][0] == 'b')
        ? CTRL_BREAK_EVENT
        : CTRL_C_EVENT;

    /* Leave our own console, join the target's. */
    FreeConsole();
    if (!AttachConsole(pid)) return 10;

    /* Don't let the event we're about to raise terminate the courier. */
    SetConsoleCtrlHandler(NULL, TRUE);

    BOOL ok = GenerateConsoleCtrlEvent(evt, 0);

    /* Give the event time to propagate to the target before we detach. */
    Sleep(300);
    FreeConsole();
    return ok ? 0 : 11;
}
