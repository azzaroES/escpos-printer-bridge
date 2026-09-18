@echo off
setlocal
cd /d "%~dp0"

echo === Building USB LAN Printer Bridge (Release, .NET Framework 4.5) ===
dotnet build "src\UsbLanPrinterBridge\UsbLanPrinterBridge.csproj" -c Release -nologo
if errorlevel 1 goto :fail

echo === Running self-tests ===
dotnet build "tests\UsbLanPrinterBridge.Tests\UsbLanPrinterBridge.Tests.csproj" -c Release -nologo -v q
if errorlevel 1 goto :fail
"tests\UsbLanPrinterBridge.Tests\bin\Release\net45\UsbLanPrinterBridge.Tests.exe"
if errorlevel 1 (
  echo Self-tests reported failures. The build output is still in dist\ but check the messages above.
)

echo === Copying to dist\ ===
if not exist dist mkdir dist
copy /y "src\UsbLanPrinterBridge\bin\Release\net45\UsbLanPrinterBridge.exe" dist\ >nul
copy /y "src\UsbLanPrinterBridge\bin\Release\net45\UsbLanPrinterBridge.exe.config" dist\ >nul
copy /y README.md dist\ >nul
echo Done: dist\UsbLanPrinterBridge.exe
exit /b 0

:fail
echo Build failed.
exit /b 1
