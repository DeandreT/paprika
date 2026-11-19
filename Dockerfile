FROM oven/bun:1.4.5-debian@sha256:3dddea58e1f8f2240eca906b023fedb944f9a8352515a35309fd0c879fe00aaa AS ui
WORKDIR /src/client
COPY client/package.json client/bun.lock ./
RUN bun install --frozen-lockfile
COPY client/angular.json client/tsconfig*.json ./
COPY client/src/ ./src/
RUN bun --bun run build --configuration production && test -s dist/client/browser/assets/pdf.worker.min.mjs

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:bef136a371963aaa46ec97882845ef6f54dec15728bb4b128b1c2a400ffbc9b4 AS api
WORKDIR /src/server
COPY server/paprika.Api.csproj ./
RUN dotnet restore -r linux-x64
COPY server/*.cs ./
RUN dotnet publish --no-restore -c Release -r linux-x64 --self-contained false -p:UseAppHost=false -o /out \
    && test -s /out/pdfium.so && test -s /out/libSkiaSharp.so
COPY COPYING THIRD_PARTY_NOTICES.md /src/
COPY scripts/licensing/ /src/scripts/licensing/
COPY --from=ui /src/client/dist/client/browser/ /out/wwwroot/
RUN --mount=type=bind,from=ui,source=/src/client,target=/src/client \
    pwsh -NoProfile -File /src/scripts/licensing/collect-notices.ps1 -PackageDirectory /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:12b1135f5f8b3c0a214f2a680ff2033dff4f684f445d6c697d9524ceeb303586
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -o app -g app -m 0700 /data
WORKDIR /app
COPY --from=api /out/ ./
RUN --mount=type=bind,source=scripts/licensing,target=/tmp/licensing \
    sh /tmp/licensing/collect-system.sh
ENV paprika__Operations__Mode=shared \
    paprika__Hosting__Url=http://0.0.0.0:8080 \
    paprika__DataDirectory=/data \
    paprika__Mode=Simulation
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "paprika.Api.dll"]
