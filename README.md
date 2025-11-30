# Visual Studio の docker compose から起動した場合のコンテナ操作

- `-p ec-auth` は [`docker-compose.dcproj`](./docker-compose.dcproj) の `<DockerComposeProjectName>` で指定している

## Linux環境でのHTTPS対応起動方法

Linux環境でHTTPSを有効にしてコンテナを起動する場合は、以下のコマンドを実行してください：

### コンテナの起動（証明書は自動生成されます）
```bash
docker compose -f docker-compose.yml -f docker-compose.override.yml -f docker-compose.linux.yml -p ec-auth up -d --build
```

初回ビルド時にmkcertを使用して証明書が自動生成されるため、事前の証明書生成は不要です。

### HTTPSエンドポイント
- IdentityProvider: https://localhost:8081

注意: 自己署名証明書を使用しているため、ブラウザで証明書の警告が表示されます。

### E2Eテスト用 MockIdP
MockIdP は Azure 環境で運用されています。詳細は [docs/CLAUDE.md](docs/CLAUDE.md) を参照してください。

## Windows環境での起動方法

Windows環境では従来通りの方法で起動できます：

```bash
docker compose -f docker-compose.yml -f docker-compose.override.yml -f docker-compose.vs.debug.yml -p ec-auth up -d
```

## DB のバックアップ

``` shell
docker compose -f docker-compose.yml -f docker-compose.override.yml -f obj/Docker/docker-compose.vs.debug.g.yml -f docker-compose.vs.debug.yml -p ec-auth --ansi never exec db /opt/mssql-tools18/bin/sqlcmd -S localhost -U SA -P '<YourStrong@Passw0rd>' -C -Q "BACKUP DATABASE [EcAuthDb] TO DISK = N'/var/opt/mssql/backup/EcAuthDb.bak' WITH NOFORMAT, NOINIT, NAME = 'EcAuthDbBackup', SKIP, NOREWIND, NOUNLOAD, STATS = 10"
```

## DB のリストア

### バックアップファイルのパスの確認
``` shell
docker compose -f docker-compose.yml -f docker-compose.override.yml -f obj/Docker/docker-compose.vs.debug.g.yml -f docker-compose.vs.debug.yml -p ec-auth --ansi never exec db /opt/mssql-tools18/bin/sqlcmd -S localhost -U SA -P '<YourStrong@Passw0rd>' -C -Q 'RESTORE FILELISTONLY FROM DISK = "/var/opt/mssql/backup/EcAuthDb.bak"'  | tr -s ' ' | cut -d ' ' -f 1-2
```

### リストア

``` shell
docker compose -f docker-compose.yml -f docker-compose.override.yml -f obj/Docker/docker-compose.vs.debug.g.yml -f docker-compose.vs.debug.yml -p ec-auth --ansi never exec db /opt/mssql-tools18/bin/sqlcmd -S localhost -U SA -P '<YourStrong@Passw0rd>' -C -Q 'RESTORE DATABASE EcAuthDb FROM DISK = "/var/opt/mssql/backup/EcAuthDb.bak" WITH MOVE "EcAuthDb" TO "/var/opt/mssql/data/EcAuthDb.mdf", MOVE "EcAuthDb_log" TO "/var/opt/mssql/data/EcAuthDb_log.ldf"'
```

## See Also
- https://github.com/efcore/EFCore.FSharp/blob/master/GETTING_STARTED.md
