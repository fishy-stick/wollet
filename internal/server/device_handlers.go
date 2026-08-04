package server

import (
	"context"
	"errors"
	"net"
	"net/http"
	"time"

	"github.com/fishy-stick/wollet/internal/devicehub"
	"github.com/fishy-stick/wollet/internal/identity"
	"github.com/fishy-stick/wollet/internal/store"
)

func (s *Server) handleWakeDevice(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	device, err := s.store.GetDevice(r.Context(), id)
	if errors.Is(err, store.ErrNotFound) {
		writeError(w, http.StatusNotFound, "device_not_found", "设备不存在")
		return
	}
	if err != nil {
		writeError(w, http.StatusInternalServerError, "database_error", "无法读取设备")
		return
	}
	if s.hub.IsOnline(id) {
		writeError(w, http.StatusConflict, "device_online", "设备在线时不能执行唤醒")
		return
	}
	mac, err := net.ParseMAC(device.MACAddress)
	if err != nil {
		s.logger.Error("stored device has invalid MAC", "device_id", id, "error", err)
		writeError(w, http.StatusInternalServerError, "invalid_device_mac", "设备 MAC 地址无效")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 3*time.Second)
	defer cancel()
	if err := s.wol.Send(ctx, mac); err != nil {
		s.logger.Error("send Wake-on-LAN packet", "device_id", id, "error", err)
		writeError(w, http.StatusBadGateway, "wol_send_failed", "唤醒数据包发送失败")
		return
	}
	writeJSON(w, http.StatusOK, map[string]string{"status": "sent"})
}

func (s *Server) handleShutdownDevice(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	if _, err := s.store.GetDevice(r.Context(), id); errors.Is(err, store.ErrNotFound) {
		writeError(w, http.StatusNotFound, "device_not_found", "设备不存在")
		return
	} else if err != nil {
		writeError(w, http.StatusInternalServerError, "database_error", "无法读取设备")
		return
	}
	if !s.hub.IsOnline(id) {
		writeError(w, http.StatusConflict, "device_offline", "设备当前离线")
		return
	}
	commandID, err := identity.NewUUID()
	if err != nil {
		writeError(w, http.StatusInternalServerError, "command_generation_failed", "无法创建关机指令")
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), s.cfg.CommandTimeout)
	defer cancel()
	err = s.hub.SendShutdown(ctx, id, commandID)
	switch {
	case err == nil:
		writeJSON(w, http.StatusOK, map[string]string{"status": "delivered", "commandId": commandID})
	case errors.Is(err, devicehub.ErrCommandInProgress):
		writeError(w, http.StatusConflict, "command_in_progress", "该设备已有正在发送的关机指令")
	case errors.Is(err, devicehub.ErrOffline):
		writeError(w, http.StatusConflict, "device_offline", "设备已离线，指令未送达")
	case errors.Is(err, context.DeadlineExceeded):
		writeError(w, http.StatusGatewayTimeout, "delivery_timeout", "设备未确认收到关机指令")
	default:
		s.logger.Error("deliver shutdown command", "device_id", id, "error", err)
		writeError(w, http.StatusBadGateway, "delivery_failed", "关机指令发送失败")
	}
}

func (s *Server) handleDeleteDevice(w http.ResponseWriter, r *http.Request) {
	id := r.PathValue("id")
	if err := s.store.DeleteDevice(r.Context(), id); errors.Is(err, store.ErrNotFound) {
		writeError(w, http.StatusNotFound, "device_not_found", "设备不存在")
		return
	} else if err != nil {
		s.logger.Error("delete device", "device_id", id, "error", err)
		writeError(w, http.StatusInternalServerError, "database_error", "无法移除设备")
		return
	}
	s.hub.Disconnect(id, "device removed")
	s.publishRemoved(id)
	w.WriteHeader(http.StatusNoContent)
}
