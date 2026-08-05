//go:build linux

package coreapp

import (
	"archive/zip"
	"bytes"
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"time"

	"github.com/TIANLI0/THRM/internal/appmeta"
)

type cappedDiagnosticBuffer struct{ bytes.Buffer }

func (buffer *cappedDiagnosticBuffer) Write(data []byte) (int, error) {
	written := len(data)
	if remaining := (4 << 20) - buffer.Len(); remaining > 0 {
		if len(data) > remaining {
			data = data[:remaining]
		}
		_, _ = buffer.Buffer.Write(data)
	}
	return written, nil
}

func addPlatformDiagnosticLogs(archive *zip.Writer) error {
	home, _ := os.UserHomeDir()
	_ = addRecentDiagnosticLogs(archive, filepath.Join(appmeta.UserStateDir(home), "logs"), "fallback", 8)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	command := exec.CommandContext(ctx, "journalctl", "--no-pager", "--quiet", "--output=short-iso-precise", "--since=7 days ago", "--lines=4000", "--reverse", "--identifier=thrm", "--identifier=thrm-core")
	var output cappedDiagnosticBuffer
	command.Stdout = &output
	if err := command.Run(); err != nil || output.Len() == 0 {
		return nil
	}
	entry, err := archive.Create("logs/journal.log")
	if err != nil {
		return err
	}
	_, err = entry.Write(output.Bytes())
	return err
}
