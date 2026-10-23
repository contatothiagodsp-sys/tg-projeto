@echo off
chcp 65001 >nul
title tHg Optimization - gerar instalador
rem Um clique: instala o que falta para compilar (.NET 8 SDK e Inno Setup 6, pelo winget) e gera
rem dist\tHgOptimization-Setup-<versao>.exe. Pode rodar de novo quando quiser: o que ja esta instalado e pulado.
cd /d "%~dp0"

echo.
echo  ============================================
echo     tHg Optimization  ^|  gerar o instalador
echo  ============================================
echo.

rem Aberto de dentro do .zip (o Windows extrai so o .bat para uma pasta temporaria) ou fora da pasta do projeto.
if not exist "%~dp0tHgOptimization.csproj" (
    echo NAO ENCONTREI O PROJETO ao lado deste arquivo:
    echo   %~dp0
    echo.
    echo Isso acontece quando o GERAR-INSTALADOR.bat e aberto de dentro do .zip.
    echo Faca assim:
    echo   1. Clique com o botao direito no tHgOptimization-1.0.zip e escolha "Extrair Tudo..."
    echo   2. Abra a pasta extraida tHgOptimization
    echo   3. De dois cliques no GERAR-INSTALADOR.bat que esta LA DENTRO
    echo.
    pause
    exit /b 1
)

where winget >nul 2>&1
if errorlevel 1 (
    echo O winget nao foi encontrado. Atualize o "Instalador de Aplicativo" pela Microsoft Store e rode de novo.
    start ms-windows-store://pdp/?productid=9NBLGGH4NNS1
    pause
    exit /b 1
)

set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"

"%DOTNET%" --list-sdks 2>nul | findstr /b "8." >nul
if errorlevel 1 (
    echo [1/3] Instalando o .NET 8 SDK - so na primeira vez, alguns minutos...
    winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements
) else (
    echo [1/3] .NET 8 SDK ja instalado.
)

rem O Inno Setup pode estar instalado para todos (Arquivos de Programas) ou so para o usuario (AppData\Local\Programs).
set "ISCC="
call :findiscc
if not defined ISCC (
    echo [2/3] Instalando o Inno Setup 6 - so na primeira vez...
    winget install --id JRSoftware.InnoSetup -e --accept-package-agreements --accept-source-agreements
    call :findiscc
) else (
    echo [2/3] Inno Setup ja instalado.
)

if not exist "%DOTNET%" (
    echo.
    echo Nao achei o .NET depois da instalacao. Feche esta janela e rode o GERAR-INSTALADOR.bat de novo.
    pause
    exit /b 1
)
set "PATH=%ProgramFiles%\dotnet;%PATH%"

echo [3/3] Compilando e montando o instalador...
taskkill /f /im tHgOptimization.exe >nul 2>&1
"%DOTNET%" publish "%~dp0tHgOptimization.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist -nologo -v q
if errorlevel 1 (
    echo.
    echo FALHOU a compilacao. Copie as mensagens acima e envie para quem esta ajudando com o codigo.
    pause
    exit /b 1
)
copy /y "LEIA-ME.txt" "dist\LEIA-ME.txt" >nul

if not defined ISCC (
    echo.
    echo O Inno Setup nao foi encontrado: gerei so a versao portatil em dist\tHgOptimization.exe
    explorer dist
    pause
    exit /b 0
)
echo Montando o instalador com: %ISCC%
rem Versao do instalador = Version do .csproj (fica sempre igual a do app)
set "APPVER="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content 'tHgOptimization.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"`) do set "APPVER=%%v"
set "VERARG="
if defined APPVER set "VERARG=/DAppVersion=%APPVER%"
"%ISCC%" /Q installer\tHgOptimization.iss /DSourceExe=..\dist\tHgOptimization.exe %VERARG%
if errorlevel 1 (
    echo FALHOU ao montar o instalador. A versao portatil esta em dist\tHgOptimization.exe
    pause
    exit /b 1
)
rem Versao nova pronta: apaga os instaladores das versoes anteriores (fica so o desta versao).
if defined APPVER for %%F in ("dist\tHgOptimization-Setup-*.exe") do if /i not "%%~nxF"=="tHgOptimization-Setup-%APPVER%.exe" del /q "%%F"

echo.
echo  Pronto! Instalador gerado na pasta dist:
dir /b dist\tHgOptimization-Setup-*.exe
explorer dist
pause
exit /b 0

:findiscc
for %%P in ("%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" "%ProgramFiles%\Inno Setup 6\ISCC.exe" "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe") do (
    if not defined ISCC if exist "%%~P" set "ISCC=%%~P"
)
if not defined ISCC for /f "delims=" %%P in ('where ISCC.exe 2^>nul') do if not defined ISCC set "ISCC=%%P"
exit /b 0
