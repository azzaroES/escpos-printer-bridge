@echo off
rem Compiles and runs the desktop self-test for the Android EscPosResponder.
rem EscPosResponder has no Android imports on purpose, so it can be verified on a normal JVM without a device.
setlocal

cd /d "%~dp0\.."

if "%JAVA_HOME%"=="" set "JAVA_HOME=C:\Program Files\Android\Android Studio\jbr"
if not exist "%JAVA_HOME%\bin\javac.exe" (
  echo Could not find a JDK. Set JAVA_HOME to a JDK 11 or newer, or install Android Studio.
  exit /b 1
)

set "OUT=build\responder-test"
if not exist "%OUT%" mkdir "%OUT%"

echo === compiling ===
"%JAVA_HOME%\bin\javac.exe" -d "%OUT%" ^
  "app\src\main\java\com\usblanbridge\core\EscPosResponder.java" ^
  "tools\ResponderSelfTest.java"
if errorlevel 1 (
  echo Compilation failed.
  exit /b 1
)

echo === running ===
"%JAVA_HOME%\bin\java.exe" -cp "%OUT%" ResponderSelfTest
set RESULT=%errorlevel%
if %RESULT%==0 (
  echo All responder checks passed.
) else (
  echo %RESULT% check^(s^) failed.
)
exit /b %RESULT%
