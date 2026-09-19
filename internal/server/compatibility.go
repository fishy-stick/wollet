package server

import (
	"context"
	"encoding/json"
	"github.com/coder/websocket"
	"github.com/fishy-stick/wollet/internal/compatibility"
	"net/http"
	"sync"
)

type peerInfo struct {
	Endpoint compatibility.Endpoint `json:"endpoint"`
	socket   *websocket.Conn
}
type peerRegistry struct {
	mu   sync.Mutex
	live map[string]peerInfo
}

func (s *Server) recordPeer(ctx context.Context, id string, conn *websocket.Conn, version string, caps []string) error {
	if compatibility.Normalize(version) == "" {
		version = ""
	}
	endpoint := compatibility.Endpoint{Version: version, Capabilities: append([]string{"protocol.v1"}, caps...), Known: true}
	data, err := json.Marshal(peerInfo{Endpoint: endpoint})
	if err != nil {
		return err
	}
	if err = s.store.SaveCompatibility(ctx, id, data); err != nil {
		return err
	}
	s.peers.mu.Lock()
	s.peers.live[id] = peerInfo{endpoint, conn}
	s.peers.mu.Unlock()
	return nil
}
func (s *Server) forgetPeer(id string, conn *websocket.Conn) {
	s.peers.mu.Lock()
	defer s.peers.mu.Unlock()
	if s.peers.live[id].socket == conn {
		delete(s.peers.live, id)
	}
}
func (s *Server) compatibilityView(id string) compatibility.Result {
	s.peers.mu.Lock()
	peer, live := s.peers.live[id]
	s.peers.mu.Unlock()
	live = live && s.hub.IsOnline(id)
	if !live {
		data, err := s.store.LoadCompatibility(context.Background(), id)
		if err == nil && len(data) > 0 {
			_ = json.Unmarshal(data, &peer)
		}
		peer.Endpoint.Known = false
	}
	result := compatibility.Default.Evaluate(peer.Endpoint, compatibility.Endpoint{Version: compatibility.Version, Capabilities: compatibility.ServerCapabilities(), Known: true})
	result.Historical = !live
	if !live {
		result.Label = ""
		result.Detail = "设备未连接；显示上次连接的版本，当前功能支持待确认。"
	}
	return result
}
func (s *Server) handleServerInfo(w http.ResponseWriter, r *http.Request) {
	writeJSON(w, http.StatusOK, compatibility.Endpoint{Version: compatibility.Version, Capabilities: compatibility.ServerCapabilities(), Known: true})
}
