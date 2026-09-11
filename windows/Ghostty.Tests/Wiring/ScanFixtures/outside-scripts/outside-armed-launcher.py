# Control: the same launcher with an in-file arming token stays clean.
import os, subprocess
os.environ['WINTTY_TEST_CONFIG'] = '1'
os.environ['XDG_CONFIG_HOME'] = os.path.join(os.environ['TEMP'], 'wintty-ok-' + os.urandom(8).hex())
subprocess.run([r'C:\b1\Wintty.exe', '--flag'])
