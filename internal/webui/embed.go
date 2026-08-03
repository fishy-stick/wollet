package webui

import "embed"

//go:embed index.html
var Index []byte

//go:embed assets/*
var Files embed.FS
