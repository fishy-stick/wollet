package compatibility

import (
	"embed"
	"encoding/json"
	"regexp"
	"slices"
	"strconv"
	"strings"
)

// Version is injected from the release tag. An unversioned build must not claim a release.
var Version = "unknown"

//go:embed catalog.json
var resources embed.FS

type Feature struct {
	ID     string   `json:"id"`
	Name   string   `json:"name"`
	Client []string `json:"client"`
	Server []string `json:"server"`
}
type Profile struct {
	Client []string `json:"client"`
	Server []string `json:"server"`
}
type Release struct {
	Version string `json:"version"`
	Profile string `json:"profile"`
	Order   int    `json:"order"`
}
type Catalog struct {
	Features       []Feature          `json:"features"`
	Profiles       map[string]Profile `json:"profiles"`
	Versions       []Release          `json:"versions"`
	CurrentProfile string             `json:"currentProfile"`
}
type Endpoint struct {
	Version      string   `json:"version"`
	Capabilities []string `json:"capabilities"`
	Known        bool     `json:"known"`
}
type Missing struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	Component string `json:"component"`
	Target    string `json:"target,omitempty"`
}
type Result struct {
	Kind          string    `json:"kind"`
	Label         string    `json:"label"`
	ClientVersion string    `json:"clientVersion"`
	ServerVersion string    `json:"serverVersion"`
	Missing       []Missing `json:"missing"`
	Detail        string    `json:"detail"`
	Historical    bool      `json:"historical"`
}

var Default = load()

func load() Catalog {
	b, _ := resources.ReadFile("catalog.json")
	var c Catalog
	if err := json.Unmarshal(b, &c); err != nil {
		panic(err)
	}
	return c
}
func ServerCapabilities() []string {
	return slices.Clone(Default.Profiles[Default.CurrentProfile].Server)
}

var versionPattern = regexp.MustCompile(`^([0-9]+\.[0-9]+(?:\.[0-9]+){0,2})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$`)

func Normalize(value string) string {
	if len(value) > 128 {
		return ""
	}
	m := versionPattern.FindStringSubmatch(value)
	if m == nil {
		return ""
	}
	parts := strings.Split(m[1], ".")
	for len(parts) < 3 {
		parts = append(parts, "0")
	}
	for i, p := range parts {
		n, e := strconv.ParseInt(p, 10, 32)
		if e != nil {
			return ""
		}
		parts[i] = strconv.FormatInt(n, 10)
	}
	if len(parts) == 4 && parts[3] == "0" {
		parts = parts[:3]
	}
	v := strings.Join(parts, ".")
	if m[2] != "" {
		v += "-" + strings.ToLower(m[2])
	}
	return v
}
func Display(value string) string {
	if Normalize(value) == "" {
		return "版本未知"
	}
	return value
}
func (c Catalog) release(value string) *Release {
	n := Normalize(value)
	for _, r := range c.Versions {
		if n != "" && Normalize(r.Version) == n {
			return &r
		}
	}
	return nil
}
func supports(caps, needs []string) bool {
	for _, n := range needs {
		if !slices.Contains(caps, n) {
			return false
		}
	}
	return true
}
func (c Catalog) capabilities(e Endpoint, role string) ([]string, bool) {
	if !e.Known {
		return nil, false
	}
	if e.Capabilities != nil {
		return e.Capabilities, true
	}
	if r := c.release(e.Version); r != nil {
		p := c.Profiles[r.Profile]
		if role == "client" {
			return p.Client, true
		}
		return p.Server, true
	}
	return nil, false
}
func (c Catalog) target(e Endpoint, role string, needs []string) string {
	old := c.release(e.Version)
	if old == nil {
		return ""
	}
	expected := c.Profiles[old.Profile].Server
	if role == "client" {
		expected = c.Profiles[old.Profile].Client
	}
	// A version/capability contradiction is not evidence that upgrading fixes it.
	if e.Capabilities != nil && !supports(e.Capabilities, expected) {
		return ""
	}
	best := ""
	order := int(^uint(0) >> 1)
	for _, r := range c.Versions {
		caps := c.Profiles[r.Profile].Server
		if role == "client" {
			caps = c.Profiles[r.Profile].Client
		}
		if r.Order > old.Order && r.Order < order && supports(caps, needs) {
			best = r.Version
			order = r.Order
		}
	}
	return best
}
func (c Catalog) Evaluate(client, server Endpoint) Result {
	r := Result{Kind: "compatible", ClientVersion: Display(client.Version), ServerVersion: Display(server.Version), Missing: []Missing{}}
	cc, ck := c.capabilities(client, "client")
	sc, sk := c.capabilities(server, "server")
	if !ck || !sk {
		r.Kind = "unknown"
		r.Label = "兼容性未确认"
		r.Detail = "功能支持尚未确认，连接后可重新检查。"
		return r
	}
	roles := map[string]bool{}
	allTargets := true
	for _, f := range c.Features {
		// Single-component features cannot be missing because the other endpoint is old.
		if len(f.Client) == 0 || len(f.Server) == 0 {
			continue
		}
		cp, sp := supports(cc, f.Client), supports(sc, f.Server)
		if cp == sp {
			continue
		}
		role, e, needs := "client", client, f.Client
		if cp {
			role, e, needs = "server", server, f.Server
		}
		target := c.target(e, role, needs)
		r.Missing = append(r.Missing, Missing{f.ID, f.Name, role, target})
		roles[role] = true
		if target == "" {
			allTargets = false
		}
	}
	if len(r.Missing) > 0 {
		r.Kind = "limited"
		r.Label = "兼容性受限"
		r.Detail = "以下功能当前不可用，其他已支持功能仍可使用。"
		if allTargets && len(roles) == 1 {
			if roles["client"] {
				r.Kind = "client_upgrade"
				r.Label = "客户端可升级"
			} else {
				r.Kind = "server_upgrade"
				r.Label = "服务端可升级"
			}
			r.Detail = "更新对应组件可启用以下功能；目标来自内置目录，未查询最新发布版本。"
		}
	} else if Normalize(client.Version) == "" || Normalize(server.Version) == "" {
		r.Kind = "unknown"
		r.Label = "版本未知"
		r.Detail = "已确认的功能可正常使用，部分版本信息未提供。"
	} else if Normalize(client.Version) != Normalize(server.Version) {
		r.Kind = "different"
		r.Label = "版本不一致"
		r.Detail = "当前已确认的功能均可用，无需为保持一致而升级。"
	}
	return r
}
