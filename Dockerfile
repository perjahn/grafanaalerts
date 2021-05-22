FROM mcr.microsoft.com/dotnet/sdk:latest AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app

RUN apt-get update && \
    apt-get -y upgrade && \
    apt-get -y autoremove

COPY . .

RUN dotnet publish -c Release


FROM mcr.microsoft.com/dotnet/runtime:latest AS runtime
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /app

RUN apt-get update && \
    apt-get -y upgrade && \
    apt-get -y autoremove

COPY --from=build /app/bin/Release/*/publish /app/

RUN ls | xargs sha256sum

ENTRYPOINT ["./grafanaalerts"]
