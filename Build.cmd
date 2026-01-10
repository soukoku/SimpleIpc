@echo off
cls
dotnet clean src\SimpleIpc -c Release
dotnet pack src\SimpleIpc -c Release -o build
pause