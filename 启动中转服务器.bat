@echo off
rem FindYou 中转服务器启动脚本（Windows）
cd /d "%~dp0"
set PORT=8377
node findyou-relay.js
pause
