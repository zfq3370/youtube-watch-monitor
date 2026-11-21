@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem 打包发布用压缩包：编译 + 自测 + 组装 dist 目录 + 生成 zip。
call build.cmd
if errorlevel 1 exit /b 1

set VER=0.0.0
if exist VERSION set /p VER=<VERSION
set OUT=dist\youtube-watch-monitor-%VER%
if not exist dist mkdir dist
if exist "%OUT%" rmdir /s /q "%OUT%"
mkdir "%OUT%"
mkdir "%OUT%\extension"

copy /y PinnedWatch.exe "%OUT%\" >nul
copy /y README.md "%OUT%\" >nul
copy /y LICENSE "%OUT%\" >nul
copy /y VERSION "%OUT%\" >nul
copy /y extension\* "%OUT%\extension\" >nul

rem 用 make-zip.ps1 压缩，条目路径是正斜杠，Mac/Linux 解压也不会把 extension\ 当成文件名
powershell -NoProfile -ExecutionPolicy Bypass -File make-zip.ps1 "%OUT%" "%OUT%.zip" >nul 2>&1
if exist "%OUT%.zip" (
  echo 已打包：%OUT%.zip，可直接上传到 GitHub Release。
) else (
  echo 已准备文件夹 %OUT%，但未能自动压缩；请右键该文件夹选择“发送到 - 压缩文件夹”。
)
exit /b 0
