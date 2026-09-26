# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY global.json ./
COPY CSharp/MoenotesAssets.csproj CSharp/packages.lock.json CSharp/
RUN dotnet restore CSharp/MoenotesAssets.csproj --locked-mode
COPY CSharp/ CSharp/
RUN dotnet publish CSharp/MoenotesAssets.csproj -c Release --no-restore -o /app -p:UseAppHost=false

# playfetch pulls the game's APK from Google Play for the Live2D model site (docs/MODEL_SITE.md).
FROM golang:1.25-bookworm AS playfetch
ARG PLAYFETCH_VERSION=8bf14e52133d9609ad91ff830252414716d79017
RUN CGO_ENABLED=0 GOBIN=/out go install github.com/Exmeaning/playfetch/cmd/playfetch@${PLAYFETCH_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
# The listener comes from config.toml; clearing the base image's port avoids the override warning.
ENV ASPNETCORE_HTTP_PORTS=
# playfetch keeps its account store and session cache on the data volume.
ENV XDG_CONFIG_HOME=/data/.config XDG_CACHE_HOME=/data/.cache
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg tini \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data && chown 65532:65532 /data
WORKDIR /app
COPY --from=build /app/ ./
COPY --from=playfetch /out/playfetch /usr/local/bin/playfetch
COPY LICENSE THIRD_PARTY_NOTICES.md /usr/share/doc/moenotes-assets/
COPY third_party/ /usr/share/doc/moenotes-assets/third_party/
USER 65532:65532
WORKDIR /data
EXPOSE 8091
ENTRYPOINT ["/usr/bin/tini", "-g", "--", "dotnet", "/app/MoenotesAssets.dll"]
CMD ["serve", "/etc/moenotes-assets/config.toml"]
