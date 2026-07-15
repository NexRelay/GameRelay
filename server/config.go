package main

import (
	"encoding/json"
	"fmt"
	"os"
)

// PortRange is an inclusive range of public ports the client may claim.
type PortRange struct {
	From int `json:"from"`
	To   int `json:"to"`
}

// Config is the relay server configuration, loaded from JSON.
type Config struct {
	// ListenAddr is the address public listeners and the control port bind to.
	ListenAddr string `json:"listen_addr"`
	// ControlPort is the TCP port the Windows client connects to.
	ControlPort int `json:"control_port"`
	// UDPPort is the UDP port used as the carrier for all UDP tunnels.
	UDPPort int `json:"udp_port"`
	// Secret is the shared secret. May be overridden by GAMERELAY_SECRET.
	Secret string `json:"secret"`
	// AllowedPorts restricts which public ports tunnels may use.
	AllowedPorts []PortRange `json:"allowed_ports"`
	// LogLevel is one of debug, info, warn, error.
	LogLevel string `json:"log_level"`
}

// LoadConfig reads and validates the config file at path.
func LoadConfig(path string) (*Config, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read config: %w", err)
	}
	cfg := &Config{
		ListenAddr:  "0.0.0.0",
		ControlPort: 7000,
		UDPPort:     7000,
		LogLevel:    "info",
	}
	if err := json.Unmarshal(data, cfg); err != nil {
		return nil, fmt.Errorf("parse config: %w", err)
	}
	if env := os.Getenv("GAMERELAY_SECRET"); env != "" {
		cfg.Secret = env
	}
	if err := cfg.Validate(); err != nil {
		return nil, err
	}
	return cfg, nil
}

// Validate checks the configuration for obvious mistakes.
func (c *Config) Validate() error {
	if c.Secret == "" || c.Secret == "change-me" {
		return fmt.Errorf("secret must be set (config file or GAMERELAY_SECRET env var) and must not be the placeholder")
	}
	if len(c.Secret) < 16 {
		return fmt.Errorf("secret must be at least 16 characters, got %d", len(c.Secret))
	}
	if c.ControlPort < 1 || c.ControlPort > 65535 {
		return fmt.Errorf("control_port out of range: %d", c.ControlPort)
	}
	if c.UDPPort < 1 || c.UDPPort > 65535 {
		return fmt.Errorf("udp_port out of range: %d", c.UDPPort)
	}
	if len(c.AllowedPorts) == 0 {
		c.AllowedPorts = []PortRange{{From: 1024, To: 65535}}
	}
	for _, r := range c.AllowedPorts {
		if r.From < 1 || r.To > 65535 || r.From > r.To {
			return fmt.Errorf("invalid allowed_ports range %d-%d", r.From, r.To)
		}
	}
	return nil
}

// PortAllowed reports whether the client may open a tunnel on port, and
// refuses the relay's own ports.
func (c *Config) PortAllowed(port int) bool {
	if port == c.ControlPort {
		return false
	}
	for _, r := range c.AllowedPorts {
		if port >= r.From && port <= r.To {
			return true
		}
	}
	return false
}
