# Control: posix_spawn with an in-file arming token stays clean.
import os
os.environ["WINTTY_TEST_CONFIG"] = "1"
os.posix_spawn(r"C:1\Wintty.exe", [r"C:1\Wintty.exe"], os.environ)
