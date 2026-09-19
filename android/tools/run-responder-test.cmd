@echo off
rem Compiles and runs the desktop self-tests for the Android app's protocol core.
rem These classes have no Android imports on purpose, so they can be verified on a normal JVM without a device:
rem   ResponderSelfTest  - the ESC/POS status responder, against values captured from a real Epson TM-T20II
rem   CoreSelfTest       - the raster encoder behind Android's print dialog, the ticket footer, the NO CUT filter, the licence check
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
  "app\src\main\java\com\usblanbridge\core\RasterEncoder.java" ^
  "app\src\main\java\com\usblanbridge\core\EscPosCommand.java" ^
  "app\src\main\java\com\usblanbridge\core\FooterInjector.java" ^
  "app\src\main\java\com\usblanbridge\core\CutFilter.java" ^
  "app\src\main\java\com\usblanbridge\core\TicketText.java" ^
  "app\src\main\java\com\usblanbridge\core\PrintTarget.java" ^
  "app\src\main\java\com\usblanbridge\core\BrandedPrintTarget.java" ^
  "app\src\main\java\com\usblanbridge\core\NoCutPrintTarget.java" ^
  "app\src\main\java\com\usblanbridge\core\License.java" ^
  "tools\ResponderSelfTest.java" ^
  "tools\CoreSelfTest.java"
if errorlevel 1 (
  echo Compilation failed.
  exit /b 1
)

echo === responder ===
"%JAVA_HOME%\bin\java.exe" -cp "%OUT%" ResponderSelfTest
set R1=%errorlevel%

echo.
echo === raster, footer, no cut, licence ===
"%JAVA_HOME%\bin\java.exe" -cp "%OUT%" CoreSelfTest
set R2=%errorlevel%

set /a RESULT=%R1%+%R2%
echo.
if %RESULT%==0 (
  echo All checks passed.
) else (
  echo %RESULT% check^(s^) failed.
)
exit /b %RESULT%
