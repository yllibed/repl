FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish samples/05-hosting-remote/HostingRemoteSample.csproj -c Release -o /app

FROM build AS tests
RUN dotnet build src/Repl.RemoteTests/Repl.RemoteTests.csproj -c Release
RUN dotnet src/Repl.RemoteTests/bin/Release/net11.0/Repl.RemoteTests.dll --timeout 2m

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=10s --start-period=10s --retries=3 \
    CMD ["dotnet", "/app/HostingRemoteSample.dll", "--remote-exec", "repl-hosting-sample", "health", "--liveness"]
ENTRYPOINT ["dotnet", "HostingRemoteSample.dll"]
