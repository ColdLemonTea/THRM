//go:build !windows

package coreapp

import "fmt"

func launchUpdateInstaller(_, _ string, _ updateRequest) error {
	return fmt.Errorf("当前平台暂不支持自动安装更新，请手动下载安装包")
}
