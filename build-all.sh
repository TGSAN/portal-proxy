#!/usr/bin/env bash
# 构建 Properties/PublishProfiles 下当前宿主能交叉编译的全部目标，产物输出到 bin/Release/publish/
# - Linux 目标：任意宿主（zig）
# - Windows 目标：仅 Windows 宿主（需要 MSVC）
# - macOS 目标：仅 macOS 宿主（需要 macOS SDK）
set -u
cd "$(dirname "$0")"

linux_targets=(linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64)
targets=()
skipped=()

case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*)
        targets=(win-x64 win-arm64 "${linux_targets[@]}")
        skipped=(osx-x64 osx-arm64)
        # ILCompiler 链接 Windows 目标时需要 vswhere.exe
        vswhere_dir="/c/Program Files (x86)/Microsoft Visual Studio/Installer"
        [ -x "$vswhere_dir/vswhere.exe" ] && export PATH="$vswhere_dir:$PATH"
        ;;
    Darwin)
        targets=(osx-x64 osx-arm64 "${linux_targets[@]}")
        skipped=(win-x64 win-arm64)
        ;;
    *)
        targets=("${linux_targets[@]}")
        skipped=(win-x64 win-arm64 osx-x64 osx-arm64)
        ;;
esac

ok=()
failed=()
for rid in "${targets[@]}"; do
    echo
    echo "===== $rid ====="
    # 用 -p: 而非 /p:，避免 Git Bash 把参数当成路径转换
    if dotnet publish -p:PublishProfile="$rid" -nologo; then
        ok+=("$rid")
    else
        failed+=("$rid")
    fi
done

echo
echo "===== 结果 ====="
echo "成功: ${ok[*]:-无}"
[ ${#skipped[@]} -gt 0 ] && echo "跳过: ${skipped[*]} (当前宿主无法交叉编译)"
if [ ${#failed[@]} -gt 0 ]; then
    echo "失败: ${failed[*]}"
    exit 1
fi
echo "产物目录: $(pwd)/bin/Release/publish/"
