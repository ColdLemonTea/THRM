//go:build windows

package coreapp

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
)

func launchUpdateInstaller(installerPath, guiExecutable string, request updateRequest) error {
	if _, err := os.Stat(installerPath); err != nil {
		return fmt.Errorf("安装包不存在: %w", err)
	}
	title := strings.TrimSpace(request.WindowTitle)
	body := strings.TrimSpace(request.WindowBody)
	restarting := strings.TrimSpace(request.WindowRestarting)
	if title == "" {
		title = "THRM 正在更新"
	}
	if body == "" {
		body = "正在自动安装新版本，请勿关闭此窗口"
	}
	if restarting == "" {
		restarting = "更新完成，正在重启应用"
	}
	escape := func(value string) string {
		value = strings.NewReplacer("^", "^^", "&", "^&", "<", "^<", ">", "^>", "|", "^|").Replace(value)
		value = strings.ReplaceAll(value, "%", "%%")
		return strings.ReplaceAll(value, "!", "")
	}
	var script strings.Builder
	line := func(value string) { script.WriteString(value + "\r\n") }
	line("@echo off")
	line("setlocal enableextensions enabledelayedexpansion")
	line(`set "PATH=%SystemRoot%\System32;%PATH%"`)
	line("chcp 65001>nul")
	line("title " + escape(title))
	line("echo " + escape(body))
	line(":waitgui")
	line(fmt.Sprintf(`tasklist /FI "PID eq %d" 2>nul | find "%d" >nul`, request.GuiPID, request.GuiPID))
	line("if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto waitgui)")
	line(fmt.Sprintf(`start "" /wait "%s" /S`, installerPath))
	line("echo " + escape(restarting))
	line("timeout /t 2 /nobreak >nul")
	if guiExecutable != "" {
		line(fmt.Sprintf(`start "" "%s"`, guiExecutable))
	}
	line("exit")
	scriptPath := filepath.Join(filepath.Dir(installerPath), "run-update.bat")
	if err := os.WriteFile(scriptPath, []byte(script.String()), 0o644); err != nil {
		return fmt.Errorf("写入更新脚本失败: %w", err)
	}
	command := exec.Command("cmd", "/d", "/c", "start", "", "cmd", "/d", "/c", scriptPath)
	command.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x08000000 | syscall.CREATE_NEW_PROCESS_GROUP}
	if err := command.Start(); err != nil {
		return fmt.Errorf("启动更新安装程序失败: %w", err)
	}
	if command.Process != nil {
		_ = command.Process.Release()
	}
	return nil
}
