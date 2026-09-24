# syntax=docker/dockerfile:1
ARG BUILDER_IMAGE=rust:1.98.1-slim-bookworm
ARG RUNTIME_IMAGE=debian:bookworm-slim
FROM ${BUILDER_IMAGE} AS build
RUN apt-get update && apt-get install -y --no-install-recommends pkg-config && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY Cargo.toml Cargo.lock rust-toolchain.toml ./
COPY src ./src
RUN --mount=type=cache,target=/usr/local/cargo/registry \
    --mount=type=cache,target=/usr/local/cargo/git \
    --mount=type=cache,target=/src/target \
    --mount=type=secret,id=cargo_config,target=/usr/local/cargo/config.toml \
    cargo build --locked --release && cp target/release/moenotes-assets /moenotes-assets

FROM ${RUNTIME_IMAGE} AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates ffmpeg util-linux tini \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data && chown 65532:65532 /data
COPY LICENSE THIRD_PARTY_NOTICES.md /usr/share/doc/moenotes-assets/
COPY third_party /usr/share/doc/moenotes-assets/third_party
USER 65532:65532
WORKDIR /data
EXPOSE 8091
ENTRYPOINT ["/usr/bin/tini", "-g", "--", "/usr/local/bin/moenotes-assets"]
CMD ["serve", "/etc/moenotes-assets/config.toml"]

# Local smoke stage accepts a previously built compatible Linux binary.
FROM runtime AS prebuilt
COPY --chmod=755 target/release/moenotes-assets /usr/local/bin/moenotes-assets

FROM runtime AS final
COPY --from=build /moenotes-assets /usr/local/bin/moenotes-assets
