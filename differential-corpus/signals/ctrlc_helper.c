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
 *   exit 0            = event generated
 *   exit 2            = bad args
 *   exit 1000 + errno = AttachConsole(<pid>) failed (errno = GetLastError)
 *   exit 2000 + errno = GenerateConsoleCtrlEvent failed
 */
#include <windows.h>
#include <stdlib.h>

int main(int argc, char **argv) {
    if (argc < 3) return 2;
    DWORD pid = (DWORD)strtoul(argv[1], NULL, 10);
    DWORD evt = (argv[2][0] == 'B' || argv[2][0] == 'b')
        ? CTRL_BREAK_EVENT
        : CTRL_C_EVENT;

    /* Leave our own console (if any), join the target's. The courier is
     * spawned DETACHED_PROCESS so this is normally a no-op and AttachConsole
     * starts from a clean, console-less state. */
    FreeConsole();
    if (!AttachConsole(pid)) return 1000 + (int)(GetLastError() & 0x3ff);

    /* Don't let the event we're about to raise terminate the courier. */
    SetConsoleCtrlHandler(NULL, TRUE);

    BOOL ok = GenerateConsoleCtrlEvent(evt, 0);
    DWORD ge = GetLastError();

    /* Give the event time to propagate to the target before we detach. */
    Sleep(300);
    FreeConsole();
    return ok ? 0 : (2000 + (int)(ge & 0x3ff));
}
