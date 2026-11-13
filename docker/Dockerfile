# Build any service: docker build -f docker/Dockerfile --build-arg PROJECT=LeadPilot.Web -t leadpilot-web .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# Infrastructure uses the ASP.NET shared framework, so every service (including the Worker) runs on the aspnet image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG PROJECT
ENV PROJECT_DLL=${PROJECT}.dll
WORKDIR /app
COPY --from=build /app/publish .
RUN useradd --system --uid 10001 appuser && chown -R appuser /app
USER appuser
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet ${PROJECT_DLL}"]
