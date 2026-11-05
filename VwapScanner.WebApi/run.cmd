@echo off
cd /d "%~dp0"
rem Web API on the address in appsettings.json (Urls)
dotnet run -c Release
