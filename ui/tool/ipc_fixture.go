package main

import (
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"time"

	"github.com/TIANLI0/THRM/internal/ipc"
)

func response(data any) ipc.Response {
	payload, _ := json.Marshal(data)
	return ipc.Response{Success: true, Data: payload}
}

func main() {
	restart := make(chan struct{}, 1)
	quit := make(chan struct{}, 1)
	handler := func(req ipc.Request) ipc.Response {
		switch req.Type {
		case ipc.ReqPing:
			var data struct {
				Sequence *int `json:"sequence"`
			}
			_ = json.Unmarshal(req.Data, &data)
			if data.Sequence != nil {
				time.Sleep(time.Duration(16-*data.Sequence) * 5 * time.Millisecond)
				return response(*data.Sequence)
			}
			return response("pong")
		case ipc.ReqGetConfig:
			return response(map[string]any{
				"autoControl": true,
				"blob":        strings.Repeat("x", 16*1024),
				"unknown":     map[string]bool{"preserved": true},
			})
		case ipc.ReqGetDeviceStatus:
			return response(map[string]any{
				"connected": true,
				"model":     "THRM fixture",
				"productId": "0x0001",
				"currentData": map[string]any{
					"currentRpm": 2345,
					"targetRpm":  2500,
					"workMode":   "fixture",
				},
				"temperature": map[string]any{
					"cpuTemp": 55,
					"gpuTemp": 60,
					"maxTemp": 60,
				},
			})
		case ipc.RequestType("RestartProbe"):
			go func() {
				time.Sleep(150 * time.Millisecond)
				restart <- struct{}{}
			}()
			return response("restarting")
		case ipc.RequestType("QuitFixture"):
			go func() {
				time.Sleep(150 * time.Millisecond)
				quit <- struct{}{}
			}()
			return response("bye")
		default:
			return ipc.Response{Success: false, Error: "unsupported fixture request"}
		}
	}

	start := func() (*ipc.Server, error) {
		server := ipc.NewServer(handler, nil)
		if err := server.Start(); err != nil {
			return nil, err
		}
		go func() {
			deadline := time.Now().Add(5 * time.Second)
			for !server.HasClients() && time.Now().Before(deadline) {
				time.Sleep(10 * time.Millisecond)
			}
			if server.HasClients() {
				server.BroadcastEvent("fixture-event", map[string]string{
					"blob": strings.Repeat("e", 8*1024),
				})
			}
		}()
		return server, nil
	}

	server, err := start()
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	fmt.Println("READY")

	timeout := time.NewTimer(30 * time.Second)
	defer timeout.Stop()
	for {
		select {
		case <-restart:
			server.Stop()
			time.Sleep(100 * time.Millisecond)
			server, err = start()
			if err != nil {
				fmt.Fprintln(os.Stderr, err)
				os.Exit(1)
			}
			fmt.Println("RESTARTED")
		case <-quit:
			server.Stop()
			return
		case <-timeout.C:
			server.Stop()
			fmt.Fprintln(os.Stderr, "fixture timed out")
			os.Exit(2)
		}
	}
}
