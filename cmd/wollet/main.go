package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"github.com/fishy-stick/wollet/internal/config"
	"github.com/fishy-stick/wollet/internal/server"
	"github.com/fishy-stick/wollet/internal/store"
	"github.com/fishy-stick/wollet/internal/wol"
)

func main() {
	if err := run(os.Args[1:]); err != nil {
		fmt.Fprintln(os.Stderr, "wollet:", err)
		os.Exit(1)
	}
}

func run(args []string) error {
	if len(args) > 0 && args[0] == "healthcheck" {
		return runHealthcheck(args[1:])
	}
	if len(args) > 0 && args[0] != "serve" {
		return fmt.Errorf("unknown command %q (expected serve or healthcheck)", args[0])
	}
	return runServer()
}

func runServer() error {
	cfg, err := config.Load()
	if err != nil {
		return err
	}
	logger := newLogger(cfg.LogLevel)
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer stop()

	dataStore, err := store.Open(ctx, cfg.DBPath)
	if err != nil {
		return err
	}
	defer dataStore.Close()

	app := server.New(cfg, dataStore, wol.Sender{Broadcast: cfg.WOLBroadcast, Port: cfg.WOLPort}, logger)
	app.StartBackground(ctx)

	httpServer := &http.Server{
		Addr:              cfg.ListenAddr,
		Handler:           app,
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       90 * time.Second,
		MaxHeaderBytes:    16 << 10,
	}
	serveErrors := make(chan error, 1)
	go func() {
		logger.Warn("Wollet is serving plain HTTP; use only on a trusted LAN or VPN", "listen_addr", cfg.ListenAddr)
		serveErrors <- httpServer.ListenAndServe()
	}()

	select {
	case err := <-serveErrors:
		if !errors.Is(err, http.ErrServerClosed) {
			return fmt.Errorf("serve HTTP: %w", err)
		}
	case <-ctx.Done():
	}

	logger.Info("shutting down Wollet")
	app.Close()
	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := httpServer.Shutdown(shutdownCtx); err != nil {
		return fmt.Errorf("shutdown HTTP server: %w", err)
	}
	return nil
}

func runHealthcheck(args []string) error {
	flags := flag.NewFlagSet("healthcheck", flag.ContinueOnError)
	url := flags.String("url", "http://127.0.0.1:8080/readyz", "readiness URL")
	if err := flags.Parse(args); err != nil {
		return err
	}
	client := &http.Client{Timeout: 3 * time.Second}
	response, err := client.Get(*url)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		return fmt.Errorf("readiness returned HTTP %d", response.StatusCode)
	}
	return nil
}

func newLogger(level string) *slog.Logger {
	var slogLevel slog.Level
	switch level {
	case "debug":
		slogLevel = slog.LevelDebug
	case "warn":
		slogLevel = slog.LevelWarn
	case "error":
		slogLevel = slog.LevelError
	default:
		slogLevel = slog.LevelInfo
	}
	return slog.New(slog.NewJSONHandler(os.Stdout, &slog.HandlerOptions{Level: slogLevel}))
}
