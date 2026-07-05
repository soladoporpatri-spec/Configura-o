@echo off
setlocal EnableExtensions
REM WhatsApp Bridge Startup Script
REM Configura variaveis de ambiente e inicia uma Bridge unica.

echo Iniciando WhatsApp Bridge...
echo.

set "BRIDGE_DIR=%~dp0"
set "ROOT=%BRIDGE_DIR%..\\"

if not defined API_KEY (
    if exist "%ROOT%.env.local" (
        for /f "usebackq tokens=1,* delims==" %%A in ("%ROOT%.env.local") do (
            if /I "%%A"=="API_KEY" set "API_KEY=%%B"
        )
    )
)

if not defined API_KEY (
    echo ERRO: API_KEY nao encontrada. Execute primeiro: node scripts\gerar-segredos.js
    pause
    exit /b 1
)

set "BRIDGE_HOST=127.0.0.1"
set "CORS_ORIGINS=http://localhost:4000,http://127.0.0.1:4000"
set "BACKEND_URL=http://127.0.0.1:5000"

echo Variaveis configuradas:
echo API_KEY: carregada de ambiente/.env.local
echo BRIDGE_HOST: %BRIDGE_HOST%
echo CORS_ORIGINS: %CORS_ORIGINS%
echo BACKEND_URL: %BACKEND_URL%
echo.

node --version >nul 2>&1
if errorlevel 1 (
    echo ERRO: Node.js nao esta instalado ou nao esta no PATH
    pause
    exit /b 1
)

cd /d "%BRIDGE_DIR%"
if not exist "package.json" (
    echo ERRO: package.json nao encontrado.
    pause
    exit /b 1
)

if not exist "node_modules" (
    echo Instalando dependencias...
    call npm install
    if errorlevel 1 (
        echo ERRO: Falha ao instalar dependencias
        pause
        exit /b 1
    )
)

echo.
echo Iniciando Bridge...
echo Pressione Ctrl+C para parar
echo.

node index.js
