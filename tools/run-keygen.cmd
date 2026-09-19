@echo off
rem Makes licence keys that switch the printed footer off. See tools\LicenseKeygen.java for the commands:
rem   run-keygen keypair <folder>
rem   run-keygen sign <folder>\private.key <licensee name>
rem   run-keygen check <key>
setlocal

cd /d "%~dp0\.."

if "%JAVA_HOME%"=="" set "JAVA_HOME=C:\Program Files\Android\Android Studio\jbr"
if not exist "%JAVA_HOME%\bin\javac.exe" (
  echo Could not find a JDK. Set JAVA_HOME to a JDK 11 or newer, or install Android Studio.
  exit /b 1
)

set "OUT=build\keygen"
if not exist "%OUT%" mkdir "%OUT%"

"%JAVA_HOME%\bin\javac.exe" -d "%OUT%" ^
  "app\src\main\java\com\usblanbridge\core\License.java" ^
  "tools\LicenseKeygen.java"
if errorlevel 1 (
  echo Compilation failed.
  exit /b 1
)

"%JAVA_HOME%\bin\java.exe" -cp "%OUT%" LicenseKeygen %*
exit /b %errorlevel%
