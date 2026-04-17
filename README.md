# EcAuth IdentityProvider のコンテナ操作

開発・E2E テスト環境は Linux および Docker for Windows (WSL2) を対象としています。本番環境では `docker compose` は使用しません。

- `-p ec-auth` はプロジェクト名の指定です

## 起動方法

HTTPS 対応の IdentityProvider と SQL Server を起動します。初回ビルド時に mkcert を用いて証明書が自動生成されるため、事前の証明書生成は不要です。

```bash
docker compose -p ec-auth up -d --build
```

### HTTPS エンドポイント

- IdentityProvider: https://localhost:8081

自己署名証明書を使用しているため、ブラウザで証明書の警告が表示されます。

### E2E テスト用 MockIdP

MockIdP は Azure 環境で運用されています。詳細は [docs/CLAUDE.md](docs/CLAUDE.md) を参照してください。

## DB のバックアップ

```shell
docker compose -p ec-auth --ansi never exec db /opt/mssql-tools18/bin/sqlcmd -S localhost -U SA -P '<YourStrong@Passw0rd>' -C -Q "BACKUP DATABASE [EcAuthDb] TO DISK = N'/var/opt/mssql/backup/EcAuthDb.bak' WITH NOFORMAT, NOINIT, NAME = 'EcAuthDbBackup', SKIP, NOREWIND, NOUNLOAD, STATS = 10"
```

## DB のリストア

### バックアップファイルのパスの確認

```shell
docker compose -p ec-auth --ansi never exec db /opt/mssql-tools18/bin/sqlcmd -S localhost -U SA -P '<YourStrong@Passw0rd>' -C -Q 'RESTORE FILELISTONLY FROM DISK = "/var/opt/mssql/backup/EcAuthDb.bak"' | tr -s ' ' | cut -d ' ' -f 1-2
```

### リストア

```shell
docker compose -p ec-auth --ansi never exec db /opt/mssql-tools18/bin/sqlcmd -S localhost -U SA -P '<YourStrong@Passw0rd>' -C -Q 'RESTORE DATABASE EcAuthDb FROM DISK = "/var/opt/mssql/backup/EcAuthDb.bak" WITH MOVE "EcAuthDb" TO "/var/opt/mssql/data/EcAuthDb.mdf", MOVE "EcAuthDb_log" TO "/var/opt/mssql/data/EcAuthDb_log.ldf"'
```

## See Also

- https://github.com/efcore/EFCore.FSharp/blob/master/GETTING_STARTED.md
