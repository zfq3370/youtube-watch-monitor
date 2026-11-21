@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem 一键编译 + 自测：生成 PinnedWatch.build.exe，跑通原生自测后再替换 PinnedWatch.exe。
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo 未找到 .NET Framework 4.x 编译器 csc.exe。
  echo Windows 10 / 11 自带该框架，请在“控制面板 - 程序和功能 - 启用或关闭 Windows 功能”里勾选 .NET Framework 4.8 后重试。
  exit /b 1
)

del /q PinnedWatch.build.exe >nul 2>&1
"%CSC%" /nologo /target:winexe /out:PinnedWatch.build.exe ^
  /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
  /reference:System.Web.Extensions.dll /reference:System.Web.dll /reference:System.Security.dll ^
  PinnedWatch.cs MonitorCore.cs CoreTests.cs
if errorlevel 1 (
  echo 编译失败，请按上面的报错行号修改源码后重跑。
  exit /b 1
)

del /q test-failure.txt self-test-result.txt >nul 2>&1
PinnedWatch.build.exe --self-test
if not exist self-test-result.txt (
  echo 原生自测未能运行，详见 test-failure.txt。
  exit /b 1
)
type self-test-result.txt
echo.

if exist PinnedWatch.exe (
  move /y PinnedWatch.build.exe PinnedWatch.exe >nul 2>&1
  if errorlevel 1 (
    echo 编译与自测已通过，但正在运行的 PinnedWatch.exe 无法被替换。
    echo 请在面板右下角点“退出程序”后重跑 build.cmd；新版本已保留为 PinnedWatch.build.exe。
    exit /b 1
  )
) else (
  ren PinnedWatch.build.exe PinnedWatch.exe
)
echo 构建完成：PinnedWatch.exe（双击即可运行）
exit /b 0
