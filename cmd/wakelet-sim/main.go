package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"github.com/fishy-stick/wakelet/internal/protocol"
)

var errShutdownReceived = errors.New("shutdown command received")

type simulatorConfig struct {
	Server       string `json:"server"`
	DeviceID     string `json:"deviceId"`
	DeviceSecret string `json:"deviceSecret"`
	DeviceName   string `json:"deviceName"`
	MACAddress   string `json:"macAddress"`
}

type apiError struct {
	Error struct {
		Code    string `json:"code"`
		Message string `json:"message"`
	} `json:"error"`
}

func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "wakelet-sim:", err)
		os.Exit(1)
	}
}

func run(args []string) error {
	if len(args) == 0 {
		return errors.New("expected bind or run command")
	}
	switch args[0] {
	case "bind":
		return bind(args[1:])
	case "run":
		return runSimulator(args[1:])
	default:
		return fmt.Errorf("unknown command %q", args[0])
	}
}

func bind(args []string) error {
	flags := flag.NewFlagSet("bind", flag.ContinueOnError)
	serverURL := flags.String("server", "", "Wakelet server URL")
	token := flags.String("token", "", "one-time pairing token")
	name := flags.String("name", "", "device name")
	mac := flags.String("mac", "", "device MAC address")
	configPath := flags.String("config", "wakelet-sim.json", "output config file")
	if err := flags.Parse(args); err != nil {
		return err
	}
	if *serverURL == "" || *token == "" || *name == "" || *mac == "" {
		return errors.New("--server, --token, --name, and --mac are required")
	}
	base, err := normalizeServerURL(*serverURL)
	if err != nil {
		return err
	}
	payload, _ := json.Marshal(map[string]string{"token": *token, "deviceName": *name, "macAddress": *mac})
	request, err := http.NewRequest(http.MethodPost, base+"/api/v1/client/bind", bytes.NewReader(payload))
	if err != nil {
		return err
	}
	request.Header.Set("Content-Type", "application/json")
	response, err := (&http.Client{Timeout: 10 * time.Second}).Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusCreated {
		return readAPIError(response)
	}
	var bound struct {
		DeviceID     string `json:"deviceId"`
		DeviceSecret string `json:"deviceSecret"`
	}
	if err := json.NewDecoder(response.Body).Decode(&bound); err != nil {
		return err
	}
	config := simulatorConfig{
		Server: base, DeviceID: bound.DeviceID, DeviceSecret: bound.DeviceSecret,
		DeviceName: *name, MACAddress: *mac,
	}
	contents, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(*configPath), 0o700); err != nil {
		return err
	}
	if err := os.WriteFile(*configPath, contents, 0o600); err != nil {
		return err
	}
	fmt.Printf("bound device %s; credentials saved to %s\n", bound.DeviceID, *configPath)
	return nil
}

func runSimulator(args []string) error {
	flags := flag.NewFlagSet("run", flag.ContinueOnError)
	configPath := flags.String("config", "wakelet-sim.json", "simulator config file")
	exitOnShutdown := flags.Bool("exit-on-shutdown", true, "exit after acknowledging shutdown")
	if err := flags.Parse(args); err != nil {
		return err
	}
	contents, err := os.ReadFile(*configPath)
	if err != nil {
		return err
	}
	var config simulatorConfig
	if err := json.Unmarshal(contents, &config); err != nil {
		return err
	}
	if config.Server == "" || config.DeviceID == "" || config.DeviceSecret == "" || config.DeviceName == "" || config.MACAddress == "" {
		return errors.New("simulator config is incomplete")
	}

	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()
	delay := time.Second
	for {
		err := connect(ctx, config, *exitOnShutdown)
		if errors.Is(err, errShutdownReceived) || errors.Is(err, context.Canceled) {
			return nil
		}
		fmt.Fprintf(os.Stderr, "connection lost: %v; retrying in %s\n", err, delay)
		timer := time.NewTimer(delay)
		select {
		case <-ctx.Done():
			timer.Stop()
			return nil
		case <-timer.C:
		}
		delay *= 2
		if delay > 30*time.Second {
			delay = 30 * time.Second
		}
	}
}

func connect(ctx context.Context, config simulatorConfig, exitOnShutdown bool) error {
	endpoint, err := url.Parse(config.Server)
	if err != nil {
		return err
	}
	switch endpoint.Scheme {
	case "http":
		endpoint.Scheme = "ws"
	case "https":
		endpoint.Scheme = "wss"
	default:
		return errors.New("server URL must use http or https")
	}
	endpoint.Path = strings.TrimRight(endpoint.Path, "/") + "/api/v1/client/connect"
	headers := http.Header{}
	headers.Set("X-Wakelet-Device-ID", config.DeviceID)
	headers.Set("Authorization", "Bearer "+config.DeviceSecret)
	conn, response, err := websocket.Dial(ctx, endpoint.String(), &websocket.DialOptions{HTTPHeader: headers})
	if err != nil {
		if response != nil {
			defer response.Body.Close()
			return readAPIError(response)
		}
		return err
	}
	defer conn.CloseNow()
	conn.SetReadLimit(4 << 10)

	var writeMu sync.Mutex
	write := func(message protocol.ClientMessage) error {
		writeMu.Lock()
		defer writeMu.Unlock()
		writeCtx, cancel := context.WithTimeout(ctx, 5*time.Second)
		defer cancel()
		return wsjson.Write(writeCtx, conn, message)
	}
	if err := write(protocol.ClientMessage{
		Type: "hello", ProtocolVersion: protocol.Version,
		DeviceName: config.DeviceName, MACAddress: config.MACAddress,
	}); err != nil {
		return err
	}
	var ready protocol.ServerMessage
	if err := wsjson.Read(ctx, conn, &ready); err != nil {
		return err
	}
	if ready.Type != "ready" || ready.ProtocolVersion != protocol.Version {
		return errors.New("server returned an unsupported ready message")
	}
	heartbeatEvery := time.Duration(ready.HeartbeatIntervalSeconds) * time.Second
	if heartbeatEvery <= 0 {
		heartbeatEvery = 15 * time.Second
	}
	heartbeatsDone := make(chan struct{})
	defer close(heartbeatsDone)
	go func() {
		ticker := time.NewTicker(heartbeatEvery)
		defer ticker.Stop()
		for {
			select {
			case <-heartbeatsDone:
				return
			case <-ctx.Done():
				return
			case <-ticker.C:
				if err := write(protocol.ClientMessage{Type: "heartbeat"}); err != nil {
					return
				}
			}
		}
	}()
	fmt.Printf("device %s is online\n", config.DeviceName)
	for {
		var message protocol.ServerMessage
		if err := wsjson.Read(ctx, conn, &message); err != nil {
			return err
		}
		if message.Type != "shutdown" || message.CommandID == "" {
			return errors.New("server sent an unsupported command")
		}
		if err := write(protocol.ClientMessage{Type: "shutdown_ack", CommandID: message.CommandID}); err != nil {
			return err
		}
		fmt.Printf("shutdown command %s acknowledged\n", message.CommandID)
		if exitOnShutdown {
			return errShutdownReceived
		}
	}
}

func normalizeServerURL(value string) (string, error) {
	parsed, err := url.Parse(strings.TrimRight(value, "/"))
	if err != nil || parsed.Host == "" || (parsed.Scheme != "http" && parsed.Scheme != "https") {
		return "", errors.New("server URL must be an absolute http or https URL")
	}
	if parsed.RawQuery != "" || parsed.Fragment != "" {
		return "", errors.New("server URL must not contain a query or fragment")
	}
	return parsed.String(), nil
}

func readAPIError(response *http.Response) error {
	var payload apiError
	if err := json.NewDecoder(response.Body).Decode(&payload); err == nil && payload.Error.Message != "" {
		return fmt.Errorf("server returned HTTP %d: %s", response.StatusCode, payload.Error.Message)
	}
	return fmt.Errorf("server returned HTTP %d", response.StatusCode)
}
