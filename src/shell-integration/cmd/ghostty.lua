-- Ghostty shell integration for cmd.exe, via Clink (https://chrisant996.github.io/clink/).
--
-- cmd.exe has no rc file or pre/post-exec hooks, so Ghostty's base
-- integration sets the PROMPT env var to emit OSC 133;A (prompt start),
-- OSC 133;B (prompt end) and OSC 9;9 (cwd). PROMPT cannot mark when a
-- command starts or finishes. When the user runs Clink, this script adds
-- exactly those missing marks:
--
--   OSC 133;C         command was submitted and is about to run
--   OSC 133;D;<code>  the command finished, with its exit code
--
-- It deliberately does NOT emit 133;A/133;B: PROMPT already does, and
-- doubling the prompt marks would confuse the terminal. So this script
-- COMPLEMENTS the PROMPT-based integration rather than replacing it.
--
-- Ghostty loads this automatically by prepending its shell-integration
-- "cmd" directory to CLINK_PATH; Clink autoloads *.lua from CLINK_PATH
-- when it is injected into cmd. If Clink is not installed/injected, the
-- env var is simply ignored. Nothing here runs without Clink.
--
-- Reading the previous command's exit code relies on Clink's
-- `cmd.get_errorlevel` setting (enabled by default).

local function osc(body)
    -- BEL-terminated OSC. Clink scripts run inside cmd, so io.write goes
    -- straight to the console Ghostty is reading.
    io.write("\x1b]" .. body .. "\x07")
end

-- Emit functions are exposed on the returned module so they can be tested
-- in isolation (e.g. via `clink lua`), where the session event API below
-- is unavailable.
local M = {}

function M.mark_command_start()
    osc("133;C")
end

function M.mark_command_end(code)
    osc("133;D;" .. tostring(code))
end

-- Whether a real (non-blank) command was submitted since the last prompt.
local ran_command = false

-- Register with Clink's edit lifecycle. Guarded so the script can also be
-- loaded outside an injected session (e.g. `clink lua`) without erroring;
-- in that context the event functions don't exist and there's nothing to
-- hook anyway.
if clink and clink.onbeginedit and clink.onendedit then
    -- Fires just before each prompt is drawn. If a command ran, report its
    -- end + exit code now (before the next 133;A from PROMPT). Skipped on
    -- the first prompt, where no command has run yet.
    clink.onbeginedit(function()
        if ran_command then
            M.mark_command_end(os.geterrorlevel())
            ran_command = false
        end
    end)

    -- Fires when the user accepts the input line (Enter). Mark the command
    -- start for non-blank lines only, so bare Enter doesn't emit spurious
    -- C/D marks.
    clink.onendedit(function(line)
        if line and line:match("%S") then
            M.mark_command_start()
            ran_command = true
        end
    end)
end

return M
