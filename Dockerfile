FROM --platform=$BUILDPLATFORM golang:1.26.5-alpine AS build

ARG GOPROXY=https://goproxy.cn,direct
ARG TARGETOS
ARG TARGETARCH
ARG VERSION=unknown
ENV GOPROXY=${GOPROXY}

WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN mkdir -p /out/data && \
    CGO_ENABLED=0 GOOS=${TARGETOS} GOARCH=${TARGETARCH} \
    go build -trimpath -ldflags="-s -w -X github.com/fishy-stick/wollet/internal/compatibility.Version=${VERSION#v}" -o /out/wollet ./cmd/wollet

FROM scratch
COPY --from=build /out/wollet /wollet
COPY --from=build --chown=65532:65532 /out/data /data
USER 65532:65532
ENTRYPOINT ["/wollet", "serve"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 CMD ["/wollet", "healthcheck"]
