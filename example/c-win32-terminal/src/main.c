// example/c-win32-terminal/src/main.c
//
// Minimal Win32 host for libghostty. Creates an HWND and passes it to
// ghostty which creates a surface with DX11 rendering and ConPTY.
// Forwards keyboard, mouse, resize, focus, and DPI events to ghostty.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <windowsx.h>
#include <ghostty.h>
#include <stdio.h>

// --- Globals ---

static HWND g_hwnd = NULL;
static ghostty_app_t g_app = NULL;
static ghostty_surface_t g_surface = NULL;
static WCHAR g_high_surrogate = 0;

// --- Forward declarations ---

static LRESULT CALLBACK wnd_proc(HWND, UINT, WPARAM, LPARAM);

// --- Runtime callbacks ---
// ghostty calls wakeup from background threads when the app needs to tick.
// We post a message to the main thread's message loop.

#define WM_GHOSTTY_WAKEUP (WM_APP + 1)
#define WM_GHOSTTY_RESIZE_TIMER 1
#define RESIZE_TIMER_MS 8  // ~120 Hz for smooth resize

static void wakeup_cb(void* userdata) {
    (void)userdata;
    if (g_hwnd) PostMessage(g_hwnd, WM_GHOSTTY_WAKEUP, 0, 0);
}

// Stub callbacks -- minimum required by ghostty_runtime_config_s.
// A real app would implement clipboard, close surface, etc.

static bool action_cb(ghostty_app_t app, ghostty_target_s target,
                       ghostty_action_s action) {
    (void)app; (void)target; (void)action;
    return false;
}

static bool read_clipboard_cb(void* userdata, ghostty_clipboard_e loc,
                                void* state) {
    (void)userdata; (void)loc; (void)state;
    return false;
}

static void confirm_read_clipboard_cb(void* userdata, const char* str,
                                       void* state,
                                       ghostty_clipboard_request_e req) {
    (void)userdata; (void)str; (void)state; (void)req;
}

static void write_clipboard_cb(void* userdata, ghostty_clipboard_e loc,
                                const ghostty_clipboard_content_s* content,
                                size_t content_count, bool confirm) {
    (void)userdata; (void)loc; (void)content;
    (void)content_count; (void)confirm;
}

static void close_surface_cb(void* userdata, bool process_alive) {
    (void)userdata; (void)process_alive;
    if (g_hwnd) PostMessage(g_hwnd, WM_CLOSE, 0, 0);
}

// --- Input helpers ---

// Extract the Win32 scancode from WM_KEYDOWN/WM_KEYUP lParam.
// Bits 16-23 are the scancode. Bit 24 is the extended key flag.
// Extended keys (arrows, numpad, etc.) need the 0xE000 prefix.
static uint32_t scancode_from_lparam(LPARAM lp) {
    uint32_t sc = (lp >> 16) & 0xFF;
    if (lp & (1 << 24)) sc |= 0xE000;  // extended key
    return sc;
}

// Map Win32 modifier state to ghostty mods.
static ghostty_input_mods_e current_mods(void) {
    ghostty_input_mods_e mods = GHOSTTY_MODS_NONE;
    if (GetKeyState(VK_SHIFT) & 0x8000) mods |= GHOSTTY_MODS_SHIFT;
    if (GetKeyState(VK_CONTROL) & 0x8000) mods |= GHOSTTY_MODS_CTRL;
    if (GetKeyState(VK_MENU) & 0x8000) mods |= GHOSTTY_MODS_ALT;
    if (GetKeyState(VK_LWIN) & 0x8000 || GetKeyState(VK_RWIN) & 0x8000)
        mods |= GHOSTTY_MODS_SUPER;
    if (GetKeyState(VK_CAPITAL) & 0x0001) mods |= GHOSTTY_MODS_CAPS;
    if (GetKeyState(VK_NUMLOCK) & 0x0001) mods |= GHOSTTY_MODS_NUM;
    return mods;
}

// --- Window procedure ---

static LRESULT CALLBACK wnd_proc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_GHOSTTY_WAKEUP:
        if (g_app) ghostty_app_tick(g_app);
        return 0;

    case WM_KEYDOWN:
    case WM_SYSKEYDOWN: {
        if (!g_surface) break;
        ghostty_input_key_s key = {
            .action = (lp & (1 << 30)) ? GHOSTTY_ACTION_REPEAT : GHOSTTY_ACTION_PRESS,
            .mods = current_mods(),
            .consumed_mods = GHOSTTY_MODS_NONE,
            .keycode = scancode_from_lparam(lp),
            .text = NULL,
            .composing = false,
            .unshifted_codepoint = 0,
        };
        ghostty_surface_key(g_surface, key);
        return 0;
    }

    case WM_KEYUP:
    case WM_SYSKEYUP: {
        if (!g_surface) break;
        ghostty_input_key_s key = {
            .action = GHOSTTY_ACTION_RELEASE,
            .mods = current_mods(),
            .consumed_mods = GHOSTTY_MODS_NONE,
            .keycode = scancode_from_lparam(lp),
            .text = NULL,
            .composing = false,
            .unshifted_codepoint = 0,
        };
        ghostty_surface_key(g_surface, key);
        return 0;
    }

    case WM_CHAR: {
        if (!g_surface) break;
        // wp is a UTF-16 code unit. Characters outside the BMP arrive as
        // two WM_CHAR messages (high surrogate then low surrogate).
        WCHAR wc = (WCHAR)wp;
        wchar_t wc_buf[3] = {0};
        int count;

        if (IS_HIGH_SURROGATE(wc)) {
            g_high_surrogate = wc;
            return 0;
        }
        if (IS_LOW_SURROGATE(wc)) {
            if (g_high_surrogate) {
                wc_buf[0] = g_high_surrogate;
                wc_buf[1] = wc;
                g_high_surrogate = 0;
                count = 2;
            } else {
                return 0;  // orphaned low surrogate
            }
        } else {
            g_high_surrogate = 0;
            wc_buf[0] = wc;
            count = 1;
        }

        char utf8[8] = {0};
        int len = WideCharToMultiByte(CP_UTF8, 0, wc_buf, count, utf8, sizeof(utf8) - 1, NULL, NULL);
        if (len > 0) {
            utf8[len] = '\0';
            ghostty_surface_text(g_surface, utf8, (uintptr_t)len);
        }
        return 0;
    }

    case WM_MOUSEMOVE:
        if (g_surface) {
            // GET_X/Y_LPARAM handles sign correctly during mouse capture
            double x = (double)GET_X_LPARAM(lp);
            double y = (double)GET_Y_LPARAM(lp);
            ghostty_surface_mouse_pos(g_surface, x, y, current_mods());
        }
        return 0;

    case WM_LBUTTONDOWN:
        if (g_surface) {
            SetCapture(g_hwnd);
            ghostty_surface_mouse_button(g_surface,
                GHOSTTY_MOUSE_PRESS, GHOSTTY_MOUSE_LEFT, current_mods());
        }
        return 0;

    case WM_LBUTTONUP:
        if (g_surface) {
            ReleaseCapture();
            ghostty_surface_mouse_button(g_surface,
                GHOSTTY_MOUSE_RELEASE, GHOSTTY_MOUSE_LEFT, current_mods());
        }
        return 0;

    case WM_RBUTTONDOWN:
        if (g_surface)
            ghostty_surface_mouse_button(g_surface,
                GHOSTTY_MOUSE_PRESS, GHOSTTY_MOUSE_RIGHT, current_mods());
        return 0;

    case WM_RBUTTONUP:
        if (g_surface)
            ghostty_surface_mouse_button(g_surface,
                GHOSTTY_MOUSE_RELEASE, GHOSTTY_MOUSE_RIGHT, current_mods());
        return 0;

    case WM_MBUTTONDOWN:
        if (g_surface)
            ghostty_surface_mouse_button(g_surface,
                GHOSTTY_MOUSE_PRESS, GHOSTTY_MOUSE_MIDDLE, current_mods());
        return 0;

    case WM_MBUTTONUP:
        if (g_surface)
            ghostty_surface_mouse_button(g_surface,
                GHOSTTY_MOUSE_RELEASE, GHOSTTY_MOUSE_MIDDLE, current_mods());
        return 0;

    case WM_MOUSEWHEEL: {
        if (!g_surface) break;
        double delta = (double)GET_WHEEL_DELTA_WPARAM(wp) / WHEEL_DELTA;
        ghostty_surface_mouse_scroll(g_surface, 0, delta, 0);
        return 0;
    }

    case WM_MOUSEHWHEEL: {
        if (!g_surface) break;
        double delta = (double)GET_WHEEL_DELTA_WPARAM(wp) / WHEEL_DELTA;
        ghostty_surface_mouse_scroll(g_surface, delta, 0, 0);
        return 0;
    }

    case WM_ENTERSIZEMOVE:
        // Windows enters a modal loop during resize/move. Normal messages
        // don't flow, so we use a timer to keep the renderer ticking.
        SetTimer(hwnd, WM_GHOSTTY_RESIZE_TIMER, RESIZE_TIMER_MS, NULL);
        return 0;

    case WM_EXITSIZEMOVE:
        KillTimer(hwnd, WM_GHOSTTY_RESIZE_TIMER);
        // One final tick to render at the settled size.
        if (g_app) ghostty_app_tick(g_app);
        return 0;

    case WM_TIMER:
        if (wp == WM_GHOSTTY_RESIZE_TIMER && g_app) {
            ghostty_app_tick(g_app);
        }
        return 0;

    case WM_SIZE:
        if (g_surface) {
            ghostty_surface_set_size(g_surface, LOWORD(lp), HIWORD(lp));
        }
        return 0;

    case WM_SETFOCUS:
        if (g_surface) ghostty_surface_set_focus(g_surface, true);
        return 0;

    case WM_KILLFOCUS:
        if (g_surface) ghostty_surface_set_focus(g_surface, false);
        return 0;

    case WM_DPICHANGED: {
        if (g_surface) {
            UINT new_dpi = HIWORD(wp);
            double new_scale = (double)new_dpi / 96.0;
            ghostty_surface_set_content_scale(g_surface, new_scale, new_scale);
        }
        // Resize to the suggested rect
        RECT* suggested = (RECT*)lp;
        SetWindowPos(g_hwnd, NULL,
            suggested->left, suggested->top,
            suggested->right - suggested->left,
            suggested->bottom - suggested->top,
            SWP_NOZORDER | SWP_NOACTIVATE);
        return 0;
    }

    case WM_DESTROY:
        KillTimer(hwnd, WM_GHOSTTY_RESIZE_TIMER);
        PostQuitMessage(0);
        return 0;

    default:
        break;
    }

    return DefWindowProc(hwnd, msg, wp, lp);
}

// --- Entry point ---

int WINAPI WinMain(HINSTANCE hInst, HINSTANCE hPrev, LPSTR cmdLine, int show) {
    (void)hPrev; (void)cmdLine;

    // Attach to parent console or allocate one so we can see stderr/stdout.
    if (!AttachConsole(ATTACH_PARENT_PROCESS)) AllocConsole();
    freopen("CONOUT$", "w", stdout);
    freopen("CONOUT$", "w", stderr);

    // 1. Register window class
    WNDCLASSEX wc = {
        .cbSize = sizeof(wc),
        .style = CS_HREDRAW | CS_VREDRAW,
        .lpfnWndProc = wnd_proc,
        .hInstance = hInst,
        .hCursor = LoadCursor(NULL, IDC_IBEAM),
        .hbrBackground = NULL,  // ghostty renders the background
        .lpszClassName = "GhosttyExample",
    };
    RegisterClassEx(&wc);

    // 2. Create window
    g_hwnd = CreateWindowEx(
        0, "GhosttyExample", "Ghostty Win32 Example",
        WS_OVERLAPPEDWINDOW,
        CW_USEDEFAULT, CW_USEDEFAULT, 800, 600,
        NULL, NULL, hInst, NULL);
    if (!g_hwnd) {
        fprintf(stderr, "CreateWindowEx failed: %lu\n", GetLastError());
        return 1;
    }

    // 3. Initialize ghostty global state
    char* argv[] = { "ghostty-example" };
    if (ghostty_init(1, argv) != 0) {
        fprintf(stderr, "ghostty_init failed\n");
        return 1;
    }

    // 4. Create ghostty config
    ghostty_config_t config = ghostty_config_new();
    ghostty_config_load_default_files(config);
    ghostty_config_load_recursive_files(config);
    ghostty_config_finalize(config);

    // 5. Create ghostty app with runtime callbacks
    ghostty_runtime_config_s runtime_cfg = {
        .userdata = NULL,
        .supports_selection_clipboard = false,
        .wakeup_cb = wakeup_cb,
        .action_cb = action_cb,
        .read_clipboard_cb = read_clipboard_cb,
        .confirm_read_clipboard_cb = confirm_read_clipboard_cb,
        .write_clipboard_cb = write_clipboard_cb,
        .close_surface_cb = close_surface_cb,
    };

    g_app = ghostty_app_new(&runtime_cfg, config);
    ghostty_config_free(config);
    if (!g_app) {
        fprintf(stderr, "ghostty_app_new failed\n");
        return 1;
    }

    // 6. Create surface with HWND
    UINT dpi = GetDpiForWindow(g_hwnd);
    double scale = (double)dpi / 96.0;

    ghostty_surface_config_s surface_cfg = ghostty_surface_config_new();
    surface_cfg.platform_tag = GHOSTTY_PLATFORM_WINDOWS;
    surface_cfg.platform.windows.hwnd = (void*)g_hwnd;
    surface_cfg.scale_factor = scale;

    g_surface = ghostty_surface_new(g_app, &surface_cfg);
    if (!g_surface) {
        fprintf(stderr, "ghostty_surface_new failed\n");
        ghostty_app_free(g_app);
        return 1;
    }

    // 7. Set initial size
    RECT rc;
    GetClientRect(g_hwnd, &rc);
    ghostty_surface_set_size(g_surface,
        (uint32_t)(rc.right - rc.left),
        (uint32_t)(rc.bottom - rc.top));

    // 8. Show window and tell ghostty the surface is visible and focused
    ShowWindow(g_hwnd, show);
    UpdateWindow(g_hwnd);
    ghostty_surface_set_occlusion(g_surface, true);
    ghostty_surface_set_focus(g_surface, true);

    MSG msg;
    while (GetMessage(&msg, NULL, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessage(&msg);
    }

    // 9. Cleanup
    ghostty_surface_free(g_surface);
    ghostty_app_free(g_app);

    return (int)msg.wParam;
}
