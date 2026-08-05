package coreapp

import (
	"archive/zip"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"time"

	"github.com/TIANLI0/THRM/internal/appmeta"
)

// ExportDiagnosticPackage writes the diagnostic ZIP. Flutter only chooses the destination.
func (a *CoreApp) ExportDiagnosticPackage(path string) error {
	path = strings.TrimSpace(path)
	if !filepath.IsAbs(path) || !strings.EqualFold(filepath.Ext(path), ".zip") {
		return fmt.Errorf("诊断包必须保存为绝对路径下的 ZIP 文件")
	}

	a.mutex.RLock()
	temperature := a.currentTemp
	a.mutex.RUnlock()
	bridgeStatus := map[string]any{}
	if a.bridgeManager != nil {
		bridgeStatus = a.bridgeManager.GetStatus()
	}
	manifest := map[string]any{
		"createdAt": time.Now().Format(time.RFC3339),
		"app":       appmeta.AppName,
		"os":        runtime.GOOS,
		"arch":      runtime.GOARCH,
		"numCpu":    runtime.NumCPU(),
		"hardware": map[string]any{
			"temperature": temperature,
			"bridge":      bridgeStatus,
		},
		"debug":  a.GetDebugInfo(),
		"config": a.configManager.Get(),
	}
	data, err := json.MarshalIndent(manifest, "", "  ")
	if err != nil {
		return err
	}

	file, err := os.Create(path)
	if err != nil {
		return err
	}
	archive := zip.NewWriter(file)
	closeArchive := func(current error) error {
		if closeErr := archive.Close(); current == nil {
			current = closeErr
		}
		if closeErr := file.Close(); current == nil {
			current = closeErr
		}
		return current
	}
	entry, err := archive.Create("diagnostics.json")
	if err == nil {
		_, err = entry.Write(data)
	}
	if err != nil {
		return closeArchive(err)
	}

	if a.logger != nil {
		_ = addRecentDiagnosticLogs(archive, a.logger.GetLogDir(), "core", 8)
	}
	executable, _ := os.Executable()
	base := filepath.Dir(executable)
	_ = addRecentDiagnosticLogs(archive, filepath.Join(base, "logs"), "app", 8)
	_ = addRecentDiagnosticLogs(archive, filepath.Join(base, "bridge", "logs"), "bridge", 8)
	_ = addPlatformDiagnosticLogs(archive)
	return closeArchive(nil)
}

func addRecentDiagnosticLogs(archive *zip.Writer, directory, prefix string, limit int) error {
	entries, err := os.ReadDir(directory)
	if err != nil {
		return nil
	}
	sort.Slice(entries, func(i, j int) bool {
		left, _ := entries[i].Info()
		right, _ := entries[j].Info()
		return left.ModTime().After(right.ModTime())
	})
	added := 0
	for _, item := range entries {
		if item.IsDir() || added >= limit || !strings.HasSuffix(strings.ToLower(item.Name()), ".log") {
			continue
		}
		source, openErr := os.Open(filepath.Join(directory, item.Name()))
		if openErr != nil {
			continue
		}
		destination, createErr := archive.Create(filepath.Join("logs", prefix+"-"+item.Name()))
		if createErr == nil {
			_, createErr = io.Copy(destination, io.LimitReader(source, 2<<20))
		}
		_ = source.Close()
		if createErr == nil {
			added++
		}
	}
	return nil
}
