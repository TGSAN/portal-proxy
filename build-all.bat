@echo off
rem 构建 Properties\PublishProfiles 下的全部目标，产物输出到 bin\Release\publish\
rem Windows 上无法交叉编译 osx（缺少 macOS SDK），会自动跳过
setlocal EnableDelayedExpansion
chcp 65001 >nul
cd /d "%~dp0"

rem ILCompiler 链接 Windows 目标时需要 vswhere.exe
set "VSWHERE_DIR=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
if exist "%VSWHERE_DIR%\vswhere.exe" set "PATH=%VSWHERE_DIR%;%PATH%"

set "TARGETS=win-x64 win-arm64 linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64"
set "OK="
set "FAILED="

for %%R in (%TARGETS%) do (
    echo.
    echo ===== %%R =====
    dotnet publish -p:PublishProfile=%%R -nologo
    if errorlevel 1 (
        set "FAILED=!FAILED! %%R"
    ) else (
        set "OK=!OK! %%R"
    )
)

echo.
echo ===== 结果 =====
echo 成功:%OK%
if defined FAILED (
    echo 失败:%FAILED%
    echo 跳过: osx-x64 osx-arm64 ^(需在 macOS 上构建^)
    exit /b 1
)
echo 跳过: osx-x64 osx-arm64 ^(需在 macOS 上构建^)
echo 产物目录: %~dp0bin\Release\publish\
exit /b 0
