# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY global.json ./
COPY CSharp/MoenotesAssets.csproj CSharp/packages.lock.json CSharp/
RUN dotnet restore CSharp/MoenotesAssets.csproj --locked-mode
COPY CSharp/ CSharp/
RUN dotnet publish CSharp/MoenotesAssets.csproj -c Release --no-restore -o /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg tini \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir /data && chown 65532:65532 /data
WORKDIR /app
COPY --from=build /app/ ./
COPY LICENSE THIRD_PARTY_NOTICES.md /usr/share/doc/moenotes-assets/
COPY third_party/ /usr/share/doc/moenotes-assets/third_party/
USER 65532:65532
WORKDIR /data
EXPOSE 8091
ENTRYPOINT ["/usr/bin/tini", "-g", "--", "dotnet", "/app/MoenotesAssets.dll"]
CMD ["serve", "/etc/moenotes-assets/config.toml"]
