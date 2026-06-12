#!/usr/bin/env bash
# Drive vttest to a menu selection WITHOUT GUI keyboard input.
#
# vttest reads its menu from the controlling tty in raw mode, so stdin cannot be
# piped to it directly. Instead run it under an inner `script` pty (which gives
# vttest a real tty) and forward the menu choice + paging RETURNs from a pipe;
# vttest's rendering still flows script -> Wintty ConPTY -> libghostty, so it
# shows in the pane. A short pre-delay lets vttest start and prompt (it flushes
# any input fed before the prompt), and the trailing sleep freezes the selected
# screen for a screenshot instead of racing to the end on EOF.
#
#   $1 = menu digits to select (e.g. "3" for the character-set test)
#   $2 = number of paging RETURNs to advance within a multi-screen test
sel="${1:-0}"
pages="${2:-0}"
{
  sleep 4
  printf '%s\r' "$sel"
  for _ in $(seq 1 "$pages"); do sleep 2; printf '\r'; done
  sleep 90
} | script -qec "$HOME/vttest" /dev/null
