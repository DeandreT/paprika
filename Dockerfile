FROM node:22-bookworm-slim@sha256:e9f565d32859f0cbfb52125eafe8be39390f24dac302dc3cdf566e8ba3ab21ee AS ui
WORKDIR /src/client
COPY client/package*.json ./
RUN npm ci --no-audit --no-fund
COPY client/ ./
RUN npm run build -- --configuration production && test -s dist/client/browser/assets/pdf.worker.min.mjs

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble@sha256:185d1315aced781f8b6d12e7ed415d929bdbef411c5136da2c0ea95c2932788a AS api
WORKDIR /src/server
COPY server/paprika.Api.csproj ./
RUN dotnet restore -r linux-x64
COPY server/ ./
RUN dotnet publish --no-restore -c Release -r linux-x64 --self-contained false -p:UseAppHost=false -o /out \
    && test -s /out/pdfium.so && test -s /out/libSkiaSharp.so

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:c7c140506083e49847e13111509a56bf937075e685c53f6baa035ebce547b03b
RUN apt-get update && apt-get install -y --no-install-recommends libfontconfig1 fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* \
    && install -d -o app -g app -m 0700 /data
WORKDIR /app
COPY --from=api /out/ ./
COPY --from=ui /src/client/dist/client/browser/ ./wwwroot/
ENV paprika__Operations__Mode=shared \
    paprika__Hosting__Url=http://0.0.0.0:8080 \
    paprika__DataDirectory=/data \
    paprika__Mode=Simulation
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "paprika.Api.dll"]
