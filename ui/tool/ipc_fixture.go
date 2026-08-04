package main

import (
	"encoding/json"
	"fmt"
	"os"
	"strings"
	"time"

	"github.com/TIANLI0/THRM/internal/curveprofiles"
	"github.com/TIANLI0/THRM/internal/ipc"
	"github.com/TIANLI0/THRM/internal/types"
)

func response(data any) ipc.Response {
	payload, _ := json.Marshal(data)
	return ipc.Response{Success: true, Data: payload}
}

func main() {
	restart := make(chan struct{}, 1)
	quit := make(chan struct{}, 1)
	autoControl := true
	fanCurve := []types.FanCurvePoint{
		{Temperature: 30, RPM: 1000},
		{Temperature: 60, RPM: 2200},
		{Temperature: 90, RPM: 3600},
	}
	activeProfileID := "balanced"
	nextProfileID := 1
	fanCurveProfiles := []types.FanCurveProfile{
		{ID: "balanced", Name: "均衡", Curve: append([]types.FanCurvePoint(nil), fanCurve...)},
		{ID: "quiet", Name: "静音", Curve: []types.FanCurvePoint{
			{Temperature: 30, RPM: 800},
			{Temperature: 60, RPM: 1800},
			{Temperature: 90, RPM: 3200},
		}},
	}
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
				"autoControl":             autoControl,
				"fanCurve":                fanCurve,
				"fanCurveProfiles":        fanCurveProfiles,
				"activeFanCurveProfileId": activeProfileID,
				"blob":                    strings.Repeat("x", 16*1024),
				"unknown":                 map[string]bool{"preserved": true},
			})
		case ipc.ReqSetAutoControl:
			var data ipc.SetAutoControlParams
			if err := json.Unmarshal(req.Data, &data); err != nil {
				return ipc.Response{Success: false, Error: err.Error()}
			}
			autoControl = data.Enabled
			return response(true)
		case ipc.ReqSetFanCurve:
			var data []types.FanCurvePoint
			if err := json.Unmarshal(req.Data, &data); err != nil {
				return ipc.Response{Success: false, Error: err.Error()}
			}
			fanCurve = data
			for index := range fanCurveProfiles {
				if fanCurveProfiles[index].ID == activeProfileID {
					fanCurveProfiles[index].Curve = append([]types.FanCurvePoint(nil), data...)
					break
				}
			}
			return response(true)
		case ipc.ReqSetActiveFanCurveProfile:
			var data ipc.SetActiveFanCurveProfileParams
			if err := json.Unmarshal(req.Data, &data); err != nil {
				return ipc.Response{Success: false, Error: err.Error()}
			}
			for _, profile := range fanCurveProfiles {
				if profile.ID == data.ID {
					activeProfileID = profile.ID
					fanCurve = append([]types.FanCurvePoint(nil), profile.Curve...)
					return response(profile)
				}
			}
			return ipc.Response{Success: false, Error: "profile not found"}
		case ipc.ReqSaveFanCurveProfile:
			var data ipc.SaveFanCurveProfileParams
			if err := json.Unmarshal(req.Data, &data); err != nil {
				return ipc.Response{Success: false, Error: err.Error()}
			}
			index := -1
			for candidate := range fanCurveProfiles {
				if fanCurveProfiles[candidate].ID == data.ID {
					index = candidate
					break
				}
			}
			if index < 0 {
				data.ID = fmt.Sprintf("fixture-%d", nextProfileID)
				nextProfileID++
				fanCurveProfiles = append(fanCurveProfiles, types.FanCurveProfile{ID: data.ID})
				index = len(fanCurveProfiles) - 1
			}
			fanCurveProfiles[index].Name = curveprofiles.NormalizeProfileName(data.Name, "新曲线")
			fanCurveProfiles[index].Curve = append([]types.FanCurvePoint(nil), data.Curve...)
			if data.SetActive || activeProfileID == data.ID {
				activeProfileID = data.ID
				fanCurve = append([]types.FanCurvePoint(nil), data.Curve...)
			}
			return response(fanCurveProfiles[index])
		case ipc.ReqDeleteFanCurveProfile:
			if len(fanCurveProfiles) <= 1 {
				return ipc.Response{Success: false, Error: "at least one profile is required"}
			}
			var data ipc.DeleteFanCurveProfileParams
			if err := json.Unmarshal(req.Data, &data); err != nil {
				return ipc.Response{Success: false, Error: err.Error()}
			}
			index := -1
			for candidate := range fanCurveProfiles {
				if fanCurveProfiles[candidate].ID == data.ID {
					index = candidate
					break
				}
			}
			if index < 0 {
				return ipc.Response{Success: false, Error: "profile not found"}
			}
			fanCurveProfiles = append(fanCurveProfiles[:index], fanCurveProfiles[index+1:]...)
			if activeProfileID == data.ID {
				if index >= len(fanCurveProfiles) {
					index = len(fanCurveProfiles) - 1
				}
				activeProfileID = fanCurveProfiles[index].ID
				fanCurve = append([]types.FanCurvePoint(nil), fanCurveProfiles[index].Curve...)
			}
			return response(true)
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
