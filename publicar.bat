@echo off
rem Gera o tHg Optimization pronto para usar (nao precisa instalar o .NET no PC de destino).
rem Saida: dist\tHgOptimization.exe (portatil) e, se o Inno Setup 6 estiver instalado,
rem        dist\tHgOptimization-Setup-<versao>.exe (instalador com atalhos e desinstalador).
rem Requisito para compilar: .NET 8 SDK  ->  winget install Microsoft.DotNet.SDK.8
cd /d "%~dp0"
echo Fechando o tHg Optimization se estiver aberto ...
taskkill /f /im tHgOptimization.exe >nul 2>&1
echo Gerando tHgOptimization.exe a partir do codigo atual ...
rmdir /s /q obj\Release >nul 2>&1
dotnet publish tHgOptimization.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o dist -nologo -v q
if errorlevel 1 (
    echo.
    echo FALHOU. Veja os erros acima. O .exe antigo NAO foi atualizado.
    pause
    exit /b 1
)
copy /y "LEIA-ME.txt" "dist\LEIA-ME.txt" >nul

set ISCC="%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist %ISCC% set ISCC="%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
rem Versao do instalador = Version do .csproj (fica sempre igual a do app)
set "APPVER="
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content 'tHgOptimization.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"`) do set "APPVER=%%v"
set "VERARG="
if defined APPVER set "VERARG=/DAppVersion=%APPVER%"
if exist %ISCC% (
    echo Gerando o instalador ...
    %ISCC% /Q installer\tHgOptimization.iss /DSourceExe=..\dist\tHgOptimization.exe %VERARG%
) else (
    echo Inno Setup 6 nao encontrado: so o .exe portatil foi gerado.
    echo Para gerar o instalador tambem:  winget install JRSoftware.InnoSetup
)
echo.
echo Pronto: %~dp0dist
dir /b dist
pause
