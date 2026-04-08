@echo off
cd /d "%~dp0"
cmd /k "dotnet run --project Reader.Cli -- watch"
