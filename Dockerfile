FROM golang:1.26.5-alpine AS build

WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN mkdir -p /out/data && \
    CGO_ENABLED=0 GOOS=linux go build -trimpath -ldflags="-s -w" -o /out/wakelet ./cmd/wakelet

FROM scratch
COPY --from=build /out/wakelet /wakelet
COPY --from=build --chown=65532:65532 /out/data /data
USER 65532:65532
ENTRYPOINT ["/wakelet", "serve"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 CMD ["/wakelet", "healthcheck"]
