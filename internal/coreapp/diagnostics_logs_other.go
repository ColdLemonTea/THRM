//go:build !linux

package coreapp

import "archive/zip"

func addPlatformDiagnosticLogs(_ *zip.Writer) error { return nil }
