FROM golang:1.22 AS builder
RUN go build ./...

FROM gcr.io/distroless/base
COPY --from=builder /app /app
