# 一次性:在 VPS 上装 Caddy,给对战服务器(5210)加 TLS 反代 → wss://htl.cicala.chat/ws。docs/12 B0。
# 模式仿 deploy.ps1:本地 ssh 到 VPS 执行远端块;here-string 内避免内嵌双引号(PS5.1→ssh 会丢)。
#
# ⚠︎ 运行本脚本前必须先做(见 docs/12 §B0 手动步骤):
#   1) DNS:给 cicala.chat 加 A 记录  htl → 212.64.21.174
#   2) 腾讯云安全组:放行 TCP 80 + 443 入站(80 是 Let's Encrypt HTTP 挑战用)
# 用法:.\deploy\vps\setup-tls.ps1
param([string]$Server = "root@212.64.21.174")

$ErrorActionPreference = "Stop"

Write-Host "在 $Server 上安装 Caddy 并配置 htl.cicala.chat → 127.0.0.1:5210 反代 ..." -ForegroundColor Cyan

# 远端块:无内嵌双引号;Caddyfile 用 heredoc 写入(内容无双引号);证书由 Caddy/Let's Encrypt 自动签发续期。
ssh -o ServerAliveInterval=10 $Server @'
set -e
echo == os-release ==
head -n 2 /etc/os-release
export DEBIAN_FRONTEND=noninteractive
echo == 安装 Caddy 官方源 ==
apt install -y debian-keyring debian-archive-keyring apt-transport-https curl
rm -f /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/gpg.key | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt | tee /etc/apt/sources.list.d/caddy-stable.list
apt update
apt install -y caddy
echo == 写 /etc/caddy/Caddyfile ==
cat > /etc/caddy/Caddyfile <<EOF
htl.cicala.chat {
    reverse_proxy 127.0.0.1:5210
}
EOF
systemctl reload caddy
sleep 3
set +e
echo == 验证 https healthz ==
curl -sf https://htl.cicala.chat/healthz && echo && echo tls-setup-ok || echo tls-verify-pending-检查DNS和安全组80-443
'@
if ($LASTEXITCODE -ne 0) { throw "远端配置失败,查看:ssh $Server 'journalctl -u caddy -n 50'" }

Write-Host ""
Write-Host "完成。客户端默认地址已是 wss://htl.cicala.chat/ws。" -ForegroundColor Green
Write-Host "  若上面 healthz 未显示 tls-setup-ok:多半是 DNS 未生效或安全组未放行 80/443,稍等重跑本脚本。" -ForegroundColor Yellow
Write-Host "  浏览器验证:https://htl.cicala.chat/healthz 应显示 ok" -ForegroundColor DarkGray
Write-Host "  排障:ssh $Server 'journalctl -u caddy -f'" -ForegroundColor DarkGray
