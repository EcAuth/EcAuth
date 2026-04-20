FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /App

# Copy everything
COPY EcAuth.sln /
COPY ./IdP/IdP.fsproj /App/IdP/IdP.fsproj
COPY ./.paket /.paket
COPY ./.config /.config
COPY ./paket.lock ./
COPY ./paket.dependencies ./
# Restore as distinct layers
RUN dotnet tool restore
RUN dotnet restore /App/IdP/IdP.fsproj
RUN dotnet paket restore
# Build and publish a release
COPY ./IdP /App/IdP
RUN ls -al /App/IdP
RUN dotnet publish /App/IdP/IdP.fsproj -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /App
COPY --from=build-env /App/out .
ENTRYPOINT ["dotnet", "IdP.dll"]
