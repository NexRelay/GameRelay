// gamerelay-server is a lightweight single-client relay: it exposes public
// TCP/UDP ports on a VPS and forwards traffic to a Windows client that
// connects outbound (no port forwarding needed at home).
package main

import (
	"flag"
	"fmt"
	"log/slog"
	"os"
	"os/signal"
	"syscall"
)

var version = "1.3.0"

func main() {
	configPath := flag.String("config", "config.json", "path to config file")
	showVersion := flag.Bool("version", false, "print version and exit")
	flag.Parse()

	if *showVersion {
		fmt.Printf("gamerelay-server %s (protocol v%d)\n", version, ProtocolVersion)
		return
	}

	cfg, err := LoadConfig(*configPath)
	if err != nil {
		fmt.Fprintln(os.Stderr, "config error:", err)
		os.Exit(1)
	}

	level := slog.LevelInfo
	switch cfg.LogLevel {
	case "debug":
		level = slog.LevelDebug
	case "warn":
		level = slog.LevelWarn
	case "error":
		level = slog.LevelError
	}
	log := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: level}))

	srv := NewServer(cfg, log)

	sig := make(chan os.Signal, 1)
	signal.Notify(sig, os.Interrupt, syscall.SIGTERM)
	go func() {
		<-sig
		log.Info("shutting down")
		srv.Close()
	}()

	if err := srv.Run(); err != nil {
		log.Error("server exited", "err", err)
		os.Exit(1)
	}
}
