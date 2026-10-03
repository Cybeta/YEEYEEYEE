# YEEYEEYEE Web 服务的镜像。
#
# 三段构建：前端产物 → 后端发布 → 运行时。这样镜像里没有 SDK、没有 node_modules，
# 也没有源码，只有一个能直接跑的服务。
#
# 为什么后端阶段能编得过：`YEEYEEYEE.Web` 的目标框架是 **net10.0**（不是 net10.0-windows），
# 它引用的 Host、Desktop.Core 与 Desktop.Shared 也一样。见 YEEYEEYEE.Web.csproj 里的注释。
# 其中 Desktop.Shared 是泳道布局引擎所在的库：Web 直接引用**同一份**，不在前端另写一份。
# 注意 Shared 里带着 DPAPI 那套（SecretProtector）：Linux 上编译得过、调用会抛，所以别在这条链上碰它。
#
# 一条命令：docker compose up -d --build
# 数据（账号库、任务库、资产、场景）都在 /data 这个卷里，容器换了数据还在。

# ---------- ① 前端产物 ----------
FROM node:22-alpine AS canvas
WORKDIR /src
# 先只拷依赖清单再安装：源码改动不会让这一层失效，重复构建快得多。
COPY YEEYEEYEE.Canvas/package.json YEEYEEYEE.Canvas/package-lock.json ./
RUN npm ci
COPY YEEYEEYEE.Canvas/ ./
RUN npm run build

# ---------- ② 后端发布 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# 同理：先拷项目文件把还原做掉，再拷源码。
COPY YEEYEEYEE.slnx ./
COPY YEEYEEYEE.Core/YEEYEEYEE.Core.csproj YEEYEEYEE.Core/
COPY YEEYEEYEE.Host/YEEYEEYEE.Host.csproj YEEYEEYEE.Host/
COPY YEEYEEYEE.Desktop.Core/YEEYEEYEE.Desktop.Core.csproj YEEYEEYEE.Desktop.Core/
COPY YEEYEEYEE.Desktop.Shared/YEEYEEYEE.Desktop.Shared.csproj YEEYEEYEE.Desktop.Shared/
COPY YEEYEEYEE.Web/YEEYEEYEE.Web.csproj YEEYEEYEE.Web/
RUN dotnet restore YEEYEEYEE.Web/YEEYEEYEE.Web.csproj
COPY YEEYEEYEE.Core/ YEEYEEYEE.Core/
COPY YEEYEEYEE.Host/ YEEYEEYEE.Host/
COPY YEEYEEYEE.Desktop.Core/ YEEYEEYEE.Desktop.Core/
COPY YEEYEEYEE.Desktop.Shared/ YEEYEEYEE.Desktop.Shared/
COPY YEEYEEYEE.Web/ YEEYEEYEE.Web/
RUN dotnet publish YEEYEEYEE.Web/YEEYEEYEE.Web.csproj -c Release -o /app --no-restore

# ---------- ③ 运行时 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl 只为了 HEALTHCHECK 与出问题时手工探一下；不装它就得靠看日志猜。
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

COPY --from=build /app ./
# 前端产物放在程序目录旁；服务端靠 YEEYEEYEE__CanvasDistPath 找它（默认那个相对路径在镜像里不成立）。
COPY --from=canvas /src/dist ./canvas-dist

# ASPNETCORE_URLS 会被 appsettings.json 里的 Kestrel 端点压过（那里写的是
# http://localhost:5000，适合本机开发），所以必须再用环境变量把端点覆盖成容器里的
# 对外地址，否则进程只听 127.0.0.1，端口映射进不来。
ENV ASPNETCORE_URLS=http://+:8080 \
    Kestrel__Endpoints__Http__Url=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    YEEYEEYEE__CanvasDistPath=/app/canvas-dist \
    YEEYEEYEE__JobDatabasePath=/data/jobs.db \
    YEEYEEYEE__UserDatabasePath=/data/users.db \
    YEEYEEYEE__AssetDirectory=/data/assets

# 用镜像自带的非 root 用户（.NET 8 起就有，uid 由 APP_UID 给出）。不用 root 跑服务是底线。
RUN mkdir -p /data && chown -R $APP_UID /data /app
USER $APP_UID

VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/health || exit 1

# 必须有这一行：aspnet 基础镜像自带的 CMD 是 /bin/bash，不覆盖它容器会立刻退出。
ENTRYPOINT ["dotnet", "YEEYEEYEE.Web.dll"]
