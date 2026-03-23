FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Jellyfin.Plugin.ProviderStuff/ ./Jellyfin.Plugin.ProviderStuff/
WORKDIR /src/Jellyfin.Plugin.ProviderStuff
RUN dotnet restore
RUN dotnet publish -c Release -o /out

FROM scratch AS export
COPY --from=build /out/Jellyfin.Plugin.ProviderStuff.dll /
