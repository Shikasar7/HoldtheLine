# 一次性:用 VPS 上现有的 nginx 反代给对战服(5210)加 TLS → wss://htl.cicala.chat/ws。docs/12 B0(nginx 版)。
# 这台机 80/443 已被现有 nginx 占用(www/@/podecho),所以走 nginx 反代,不装 Caddy、不动现有项目。
# 用 scp 送配置文件(避免 here-string 丢内嵌双引号 / nginx $变量),ssh 只跑无引号命令。
#
# ⚠︎ 运行本脚本前必须先做(见 docs/12 §B0 手动步骤):
#   1) DNS:给 cicala.chat 加 A 记录  htl → 212.64.21.174
#   2) 腾讯云安全组:80 + 443 已放行(现有 HTTP(80)/HTTPS(443) 规则即可,无需另加)
# 脚本跑完后,再由你手动 `certbot --nginx -d htl.cicala.chat` 签证书(首次要邮箱/同意条款)。
# 用法:.\deploy\vps\setup-tls.ps1
param([string]$Server = "root@212.64.21.174")

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..\..")

Write-Host "[1/2] 上传 nginx 反代配置到 $Server:/etc/nginx/conf.d/htl.conf ..." -ForegroundColor Cyan
scp -o ServerAliveInterval=10 deploy/vps/htl.nginx.conf "${Server}:/etc/nginx/conf.d/htl.conf"
if ($LASTEXITCODE -ne 0) { throw "上传失败(多半是网络抖动),重跑即可" }

Write-Host "[2/2] 校验并 reload nginx ..." -ForegroundColor Cyan
# 远端块:无内嵌双引号 / 无重定向符(PS5.1 → ssh 会丢)
ssh -o ServerAliveInterval=10 $Server @'
set -e
nginx -t
systemctl reload nginx
echo nginx-reloaded
'@
if ($LASTEXITCODE -ne 0) { throw "nginx 校验/reload 失败,查看:ssh $Server nginx -t" }

Write-Host ""
Write-Host "配置已就位。现在手动签证书(首次会问邮箱/同意条款):" -ForegroundColor Green
Write-Host "  ssh $Server" -ForegroundColor Yellow
Write-Host "  certbot --nginx -d htl.cicala.chat" -ForegroundColor Yellow
Write-Host "  curl -sf https://htl.cicala.chat/healthz    # 看到 ok 即成" -ForegroundColor Yellow
Write-Host "  (若没装 certbot:apt install -y certbot python3-certbot-nginx)" -ForegroundColor DarkGray
Write-Host "客户端默认地址已是 wss://htl.cicala.chat/ws。" -ForegroundColor Green
