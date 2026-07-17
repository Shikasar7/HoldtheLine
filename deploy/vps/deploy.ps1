# 一键部署对战服务器到 VPS:发布 → 打包 → 上传 → 服务器上原子切换 + systemd 重启
# 用法:.\deploy\vps\deploy.ps1    (同 Portcialio 的部署模式;首次运行会自动装 systemd 服务)
# 自包含 linux-x64 发布 —— 服务器上不需要安装 .NET,不触碰现有 nginx/pm2/项目。
param([string]$Server = "root@212.64.21.174")

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..\..")

Write-Host "[1/5] 发布 linux-x64 自包含构建 ..." -ForegroundColor Cyan
dotnet publish src/HoldTheLine.Server -c Release -r linux-x64 --self-contained true -o build/server-linux -v q --nologo
if ($LASTEXITCODE -ne 0) { throw "发布失败,已中止部署" }

Write-Host "[2/5] 打包(服务器 + 卡表数据 + systemd 单元)..." -ForegroundColor Cyan
# 布局:server/ = 发布产物(去 pdb), data/ = game/data 卡表, holdtheline.service = systemd 单元
$stage = Join-Path $env:TEMP "holdtheline-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory "$stage" | Out-Null
Copy-Item build/server-linux "$stage/server" -Recurse
Get-ChildItem "$stage/server" -Filter *.pdb | Remove-Item
Copy-Item game/data "$stage/data" -Recurse
Copy-Item deploy/vps/holdtheline.service "$stage/holdtheline.service"
$tgz = Join-Path $env:TEMP "holdtheline-server.tgz"
tar czf $tgz -C $stage server data holdtheline.service
if ($LASTEXITCODE -ne 0) { throw "打包失败" }
Remove-Item $stage -Recurse -Force
$size = [math]::Round((Get-Item $tgz).Length / 1MB, 1)
Write-Host "        $size MB" -ForegroundColor DarkGray

Write-Host "[3/5] 上传到 $Server (跨境线路慢,耐心等待;超时就重跑一次)..." -ForegroundColor Cyan
scp -o ServerAliveInterval=10 $tgz "${Server}:/tmp/holdtheline-server.tgz"
if ($LASTEXITCODE -ne 0) { throw "上传失败(多半是网络抖动),重新运行即可" }

Write-Host "[4/5] 服务器解包、原子切换、装/重启 systemd 服务 ..." -ForegroundColor Cyan
ssh -o ServerAliveInterval=10 $Server @'
set -e
mkdir -p /opt/holdtheline /var/lib/holdtheline/match-logs
rm -rf /opt/holdtheline/new
mkdir -p /opt/holdtheline/new
tar xzf /tmp/holdtheline-server.tgz -C /opt/holdtheline/new
chmod +x /opt/holdtheline/new/server/HoldTheLine.Server
# 首次或单元变更:安装 systemd 服务
if ! cmp -s /opt/holdtheline/new/holdtheline.service /etc/systemd/system/holdtheline.service 2>/dev/null; then
  cp /opt/holdtheline/new/holdtheline.service /etc/systemd/system/holdtheline.service
  systemctl daemon-reload
  systemctl enable holdtheline >/dev/null 2>&1 || true
fi
systemctl stop holdtheline 2>/dev/null || true
rm -rf /opt/holdtheline/old
for d in server data; do
  [ -d /opt/holdtheline/$d ] && mkdir -p /opt/holdtheline/old && mv /opt/holdtheline/$d /opt/holdtheline/old/$d
  mv /opt/holdtheline/new/$d /opt/holdtheline/$d
done
rm -rf /opt/holdtheline/new /tmp/holdtheline-server.tgz
systemctl start holdtheline
sleep 2
systemctl is-active holdtheline
# 注意:此 here-string 经 PS5.1 → ssh 传输会丢内嵌双引号,这里只能用无引号无重定向符的写法
curl -sf http://127.0.0.1:5210/healthz
echo
echo healthz-ok
'@
if ($LASTEXITCODE -ne 0) { throw "服务器端切换/启动失败,查看:ssh $Server 'journalctl -u holdtheline -n 50'" }

Remove-Item $tgz -ErrorAction SilentlyContinue
Write-Host "[5/5] 完成!" -ForegroundColor Green
Write-Host ""
Write-Host "  客户端服务器地址:ws://212.64.21.174:5210/ws" -ForegroundColor Green
Write-Host "  ※ 首次部署需在腾讯云控制台【安全组】放行 TCP 5210 入站,否则外网连不上。" -ForegroundColor Yellow
Write-Host "  验证:浏览器打开 http://212.64.21.174:5210/healthz 应显示 ok" -ForegroundColor DarkGray
Write-Host "  日常运维:ssh $Server 'systemctl status holdtheline' / 'journalctl -u holdtheline -f'" -ForegroundColor DarkGray
