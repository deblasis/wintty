/* Alternate screen (DECSET 1049): write primary, switch to alt, write,
 * switch back. The oracle dumps the ACTIVE screen (primary at exit), so
 * this checks conhost's 1049 save/restore round-trip matches raw VT.
 * VT-only via std output. Deterministic. */
#include <windows.h>
static void emit(HANDLE h, const char *s, DWORD n) { DWORD w; WriteFile(h, s, n, &w, NULL); }
#define W(h, lit) emit((h), (lit), (DWORD)(sizeof(lit) - 1))

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    SetConsoleOutputCP(65001);

    W(h, "\x1b[2J\x1b[H");
    W(h, "primary line 1\r\n");
    W(h, "\x1b[5;1Hprimary line 5");

    W(h, "\x1b[?1049h");          /* -> alt screen (saves cursor, clears alt) */
    W(h, "\x1b[H\x1b[2J");
    W(h, "alt line 1\r\n");
    W(h, "\x1b[3;1Halt line 3");

    W(h, "\x1b[?1049l");          /* -> back to primary (restores) */
    W(h, "\x1b[7;1Hback on primary");
    return 0;
}
