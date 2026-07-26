# OpenNet.Server

OpenNet 的 ASP.NET Core 协调服务。目前提供 IPv4 NAT/端口探测节点目录。

## API

- `GET /api/v1/traversal/servers`：返回数据库中已启用、心跳未过期的 IPv4 探测节点。
- `PUT /api/v1/traversal/servers`：注册或更新节点，需要 `X-OpenNet-Api-Key`。
- `POST /api/v1/traversal/servers/heartbeat`：刷新节点心跳，需要 `X-OpenNet-Api-Key`。
- `GET /health`：数据库与服务健康检查。

目录响应以节点 IPv4 为主，并可包含用于纯端口探测的 IPv6；同时包含 HTTP
探测端口、STUN 主端口、备用端口和可选备用 IPv4 地址。RFC 5780 的完整过滤行为检测要求探测节点拥有两个公网 IPv4
地址；只有一个地址时仍可完成映射行为和实际入站端口探测。

## 本机启动

开发环境默认使用 SQLite 文件 `opennet-server.db` 并植入一个本地节点：

```powershell
cd src/OpenNet.Server
dotnet restore OpenNet.Server.slnx
dotnet test OpenNet.Server.slnx
dotnet run --project OpenNet.Server/OpenNet.Server.csproj --launch-profile http
```

随后访问：

```text
http://localhost:5090/health
http://localhost:5090/api/v1/traversal/servers
```

## MySQL

项目支持 `Sqlite` 和 `MySql` 两种 `DatabaseProvider`。直接连接现有 MySQL
8.0+ 时，不要把密码写进 `appsettings.json`，使用环境变量：

```powershell
$env:DatabaseProvider = "MySql"
$env:ConnectionStrings__TraversalDirectory = "Server=127.0.0.1;Port=3306;Database=opennet;User=opennet;Password=replace-me;SslMode=Preferred"
$env:TraversalDirectory__ManagementApiKey = "replace-with-a-long-random-key"
dotnet run --project src/OpenNet.Server/OpenNet.Server/OpenNet.Server.csproj --urls http://0.0.0.0:5090
```

当前初始化器使用 `EnsureCreatedAsync`，适合首次测试和空数据库。正式生产环境在
模型开始演进后应改用经过审查的 EF Core migrations/SQL 脚本，不能在已有
`EnsureCreated` 数据库上直接混用 migrations。

## Docker Compose

默认 `compose.yaml` 只启动 ASP.NET Core Server，并连接 Linux 宿主机上已经
安装的 MySQL。`host.docker.internal` 通过 `host-gateway` 映射到宿主机：

```sh
cp .env.example .env
# 编辑 MySQL 地址、账号、密码和 OPENNET_MANAGEMENT_API_KEY
docker compose up --build -d
docker compose ps
docker compose logs server
curl http://127.0.0.1:5090/health
curl http://127.0.0.1:5090/api/v1/traversal/servers
```

宿主机 MySQL 不能只监听 `127.0.0.1`；应监听 Docker bridge 可达的地址，
并授权 `opennet` 用户从容器网段连接。只应在宿主机防火墙中允许 Docker
bridge 网段访问 3306，不要向公网开放 MySQL。

如果某台机器没有自己的 MySQL，可叠加可选配置启动 MySQL 8.4 容器：

```sh
docker compose \
  -f compose.yaml \
  -f compose.mysql.yaml \
  up --build -d
```

该模式的 MySQL 数据保存在 `opennet-mysql` volume，应用通过服务名 `mysql`
连接数据库。MySQL 默认只发布为宿主机的 `127.0.0.1:13306`。推荐从外部机器
建立 SSH 隧道后再连接：

```sh
ssh -L 13306:127.0.0.1:13306 <linux-user>@<server-ip>
mysql -h 127.0.0.1 -P 13306 -u opennet -p opennet
```

如果服务器和客户端位于可信的局域网或 VPN，可在 `.env` 中将
`MYSQL_PUBLISH_ADDRESS` 设置为服务器的局域网/VPN 地址，并仅向指定客户端
IP 放行 `MYSQL_PUBLISHED_PORT`。不建议把它设置为 `0.0.0.0` 并向公网开放。

公网部署时建议让 Nginx/Caddy 终止 HTTPS，只将应用的 5090 端口暴露给反向代理。

## 与 Traversal 节点连接

OpenNet.Server 只是节点目录，不执行公网回连。Linux
`OpenNet.Traversal` 节点需要使用同一个管理密钥注册：

```sh
export OPENNET_DIRECTORY_API_KEY='与 Server 相同的管理密钥'
OpenNet.Traversal \
  --bind 0.0.0.0 \
  --advertise 203.0.113.10 \
  --directory-url http://server.example:5090/api/v1/traversal/servers
```

节点需要开放 TCP `48100` 和 UDP `3478/3479`。完整 RFC 5780 过滤检测还
需要第二个公网 IPv4，并配置 `--alternate-bind`/`--alternate-advertise`。
