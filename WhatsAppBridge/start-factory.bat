@echo off
setlocal EnableExtensions
REM WhatsApp Bridge Factory Startup Script
REM Inicia a factory que gerencia multiplas instancias de Bridge.

echo Iniciando WhatsApp Bridge Factory...
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

set "FACTORY_PORT=2999"
set "BACKEND_URL=http://127.0.0.1:5000"
set "CORS_ORIGINS=http://localhost:4000,http://127.0.0.1:4000"

echo Variaveis configuradas:
echo API_KEY: carregada de ambiente/.env.local
echo FACTORY_PORT: %FACTORY_PORT%
echo BACKEND_URL: %BACKEND_URL%
echo CORS_ORIGINS: %CORS_ORIGINS%
echo.

node --version >nul 2>&1
if errorlevel 1 (
    echo ERRO: Node.js nao esta instalado ou nao esta no PATH
    pause
    exit /b 1
)

cd /d "%BRIDGE_DIR%"
if not exist "bridge-factory.js" (
    echo ERRO: bridge-factory.js nao encontrado.
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
echo Iniciando Bridge Factory...
echo Pressione Ctrl+C para parar
echo.

node bridge-factory.js
