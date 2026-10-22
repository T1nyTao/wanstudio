@echo off
setlocal
cd /d "%~dp0"

set "EXE=WanStudio\bin\x64\WanStudio.exe"

if exist "%EXE%" goto launch

echo [1/2] 首次运行需要先构建，请稍候...
dotnet build WanStudio\WanStudio.csproj -p:Platform=x64 -v:m
if errorlevel 1 (
  echo.
  echo 构建失败。请确认已安装 .NET 8 SDK：
  echo https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)

:launch
echo [2/2] 正在启动 WanStudio...
start "" "%EXE%"
