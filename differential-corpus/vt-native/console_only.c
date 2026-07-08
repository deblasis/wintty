/*
 * console_only: the NEGATIVE control for `compare-transports`.
 *
 * Writes ONLY via the Win32 Console API (WriteConsoleW /
 * FillConsoleOutputCharacterW / SetConsoleCursorPosition) and never via
 * WriteFile to the std handle. Under ConPTY, conhost services those calls
 * and renders cells; over a raw pipe there is no console to service them,
 * so the calls fail and nothing reaches the pipe. `compare-transports`
 * therefore reports NO-OUTPUT -- demonstrating the VT-native boundary:
 * a conhost-free transport cannot carry Console-API-driven programs.
 *
 * Exits immediately; reads no input.
 */
#include <windows.h>

int main(void) {
    HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
    DWORD n = 0;
    COORD origin = {0, 0};

    FillConsoleOutputCharacterW(h, L'X', 20, origin, &n);

    COORD at = {5, 2};
    SetConsoleCursorPosition(h, at);
    WriteConsoleW(h, L"console-api", 11, &n, NULL);
    return 0;
}
