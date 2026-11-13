@echo off
rem Publication Windows self-contained de Musilib Manager (voir publish.ps1 pour les options).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
exit /b %ERRORLEVEL%
