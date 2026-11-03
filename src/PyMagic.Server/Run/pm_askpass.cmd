@echo off
rem PyMagic's SSH_ASKPASS helper: ssh runs it for the password of an SSH tunnel, which it gets in its environment only.
setlocal EnableDelayedExpansion
echo(!PYMAGIC_SSH_PASSWORD!
