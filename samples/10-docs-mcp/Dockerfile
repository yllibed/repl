FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json nuget.config version.json .editorconfig ./
COPY src/ src/
COPY samples/ samples/
RUN dotnet publish samples/10-docs-mcp/DocsMcpSample.csproj -c Release -r linux-x64 --self-contained false -o /out
RUN DOCS_ASSETS=/assets dotnet /out/docs-index.dll assets fetch linux-x64

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /out/ ./
COPY --from=build /assets/ /assets/
RUN mkdir /documents /index && chown "$APP_UID" /index
ENV DOCS_DOCUMENTS=/documents DOCS_INDEX=/index DOCS_ASSETS=/assets DOCS_ADMIN_ADDRESS=0.0.0.0
EXPOSE 8080 8081
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=10s --start-period=15s --retries=3 \
    CMD ["/app/docs-index", "health"]
ENTRYPOINT ["/app/docs-index"]
CMD ["serve"]
