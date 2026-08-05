package coreapp

import "testing"

func TestValidUpdateURL(t *testing.T) {
	for _, value := range []string{
		"https://github.com/ColdLemonTea/THRM/releases/download/v1/THRM-amd64-installer.exe",
		"https://objects.githubusercontent.com/github-production-release-asset/file",
	} {
		if _, err := validUpdateURL(value); err != nil {
			t.Fatalf("validUpdateURL(%q): %v", value, err)
		}
	}
	for _, value := range []string{"http://github.com/file", "https://github.com.evil.test/file"} {
		if _, err := validUpdateURL(value); err == nil {
			t.Fatalf("validUpdateURL(%q) unexpectedly accepted", value)
		}
	}
}
