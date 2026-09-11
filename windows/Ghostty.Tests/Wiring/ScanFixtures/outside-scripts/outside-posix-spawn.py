# Fixture: os.posix_spawn of the app (RED: must be flagged).
import os
os.posix_spawn(r".." + r"C:1\Wintty.exe", [r"C:1\Wintty.exe"], os.environ)
