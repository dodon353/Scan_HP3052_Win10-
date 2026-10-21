"""Model a WSL Linux job which is not a descendant of the Windows launcher.
Delay PID publication after starting scanimage to exercise early cancellation.
"""
import os
import sys
import time

command = sys.argv[1].replace(' & job=$!; printf', ' & job=$!; sleep 0.4; printf')
if os.fork() == 0:
    os.setsid()
    if os.fork() != 0:
        os._exit(0)
    os.execv('/bin/bash', ['/bin/bash', '-c', command])
# Windows launcher emulation: killing it does not kill the detached Linux shell.
time.sleep(20)
