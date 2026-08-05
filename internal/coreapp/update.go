package coreapp

import (
	"context"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/TIANLI0/THRM/internal/appmeta"
	"github.com/TIANLI0/THRM/internal/config"
	"github.com/TIANLI0/THRM/internal/ipc"
)

const updateInstallerName = "THRM-amd64-installer.exe"

type updateRequest struct {
	URL              string `json:"url"`
	GuiPID           int    `json:"guiPid"`
	WindowTitle      string `json:"windowTitle"`
	WindowBody       string `json:"windowBody"`
	WindowRestarting string `json:"windowRestarting"`
}

type updateProgress struct {
	Percent  int    `json:"percent"`
	Received int64  `json:"received"`
	Total    int64  `json:"total"`
	Stage    string `json:"stage"`
	Message  string `json:"message"`
}

func (a *CoreApp) emitUpdateProgress(progress updateProgress) {
	if a.ipcServer != nil {
		a.ipcServer.BroadcastEvent(ipc.EventUpdateDownloadProgress, progress)
	}
}

func validUpdateURL(raw string) (*url.URL, error) {
	parsed, err := url.Parse(strings.TrimSpace(raw))
	if err != nil || parsed.Scheme != "https" || parsed.User != nil {
		return nil, fmt.Errorf("无效的下载地址")
	}
	host := strings.ToLower(parsed.Hostname())
	if host != "github.com" && !strings.HasSuffix(host, ".github.com") &&
		!strings.HasSuffix(host, ".githubusercontent.com") {
		return nil, fmt.Errorf("下载地址不在允许的来源内: %s", parsed.Hostname())
	}
	return parsed, nil
}

func (a *CoreApp) DownloadAndInstallUpdate(request updateRequest) {
	parsed, err := validUpdateURL(request.URL)
	if err != nil {
		a.emitUpdateProgress(updateProgress{Percent: -1, Stage: "error", Message: err.Error()})
		return
	}
	installer, err := a.downloadUpdateInstaller(parsed.String())
	if err != nil {
		a.logError("下载更新安装包失败: %v", err)
		a.emitUpdateProgress(updateProgress{Percent: -1, Stage: "error", Message: err.Error()})
		return
	}
	guiExecutable := appmeta.FirstExistingPath(appmeta.GUIExecutableCandidates(config.GetInstallDir()))
	if err := launchUpdateInstaller(installer, guiExecutable, request); err != nil {
		a.logError("启动更新安装程序失败: %v", err)
		a.emitUpdateProgress(updateProgress{Percent: 100, Stage: "error", Message: err.Error()})
		return
	}
	a.emitUpdateProgress(updateProgress{Percent: 100, Stage: "installing"})
}

func (a *CoreApp) downloadUpdateInstaller(downloadURL string) (string, error) {
	directory := filepath.Join(os.TempDir(), "THRM-update")
	if err := os.MkdirAll(directory, 0o755); err != nil {
		return "", fmt.Errorf("创建临时目录失败: %w", err)
	}
	target := filepath.Join(directory, updateInstallerName)
	partial := target + ".part"
	_ = os.Remove(partial)

	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Minute)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, downloadURL, nil)
	if err != nil {
		return "", fmt.Errorf("构造下载请求失败: %w", err)
	}
	req.Header.Set("Accept", "application/octet-stream")
	response, err := http.DefaultClient.Do(req)
	if err != nil {
		return "", fmt.Errorf("下载失败: %w", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return "", fmt.Errorf("下载失败: HTTP %d", response.StatusCode)
	}
	if _, err := validUpdateURL(response.Request.URL.String()); err != nil {
		return "", err
	}

	output, err := os.Create(partial)
	if err != nil {
		return "", fmt.Errorf("创建安装包文件失败: %w", err)
	}
	ok := false
	defer func() {
		_ = output.Close()
		if !ok {
			_ = os.Remove(partial)
		}
	}()
	total := response.ContentLength
	a.emitUpdateProgress(updateProgress{Percent: 0, Total: max(total, 0), Stage: "downloading"})
	buffer := make([]byte, 64*1024)
	var received int64
	lastPercent := -100
	for {
		n, readErr := response.Body.Read(buffer)
		if n > 0 {
			if _, err := output.Write(buffer[:n]); err != nil {
				return "", fmt.Errorf("写入安装包失败: %w", err)
			}
			received += int64(n)
			percent := -1
			if total > 0 {
				percent = int(received * 100 / total)
			}
			if percent < 0 || percent-lastPercent >= 2 || percent >= 100 {
				lastPercent = percent
				a.emitUpdateProgress(updateProgress{Percent: percent, Received: received, Total: max(total, 0), Stage: "downloading"})
			}
		}
		if readErr == io.EOF {
			break
		}
		if readErr != nil {
			return "", fmt.Errorf("下载中断: %w", readErr)
		}
	}
	if err := output.Sync(); err != nil {
		return "", fmt.Errorf("刷新安装包到磁盘失败: %w", err)
	}
	if err := output.Close(); err != nil {
		return "", err
	}
	_ = os.Remove(target)
	if err := os.Rename(partial, target); err != nil {
		return "", err
	}
	ok = true
	return target, nil
}
