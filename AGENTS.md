# Agents

## GitHub 推送（本仓库专用）

origin 指向 gh-proxy.com 镜像：**fetch 可用，push 会被拒/重置**；github.com 直连 HTTPS 大包传输也常被重置（curl 55）。22 端口 SSH 被拦。

**推荐：SSH over 443**（稳定）：

```bash
git push ssh://git@ssh.github.com:443/gzzhangliangjie/MusicGame.git main
```

前提：`~/.ssh/id_ed25519` 已绑定账号 gzzhangliangjie。自检：`ssh -p 443 -T git@ssh.github.com` 应回 "Hi gzzhangliangjie!"。

**备选：HTTPS + gh CLI 凭据**（直连可用时；两条 `-c url...` 是恒等 insteadOf，用于绕过全局 gh-proxy 重写——全局 ~/.gitconfig 把所有 github.com 地址重写到 gh-proxy）：

```bash
git -c http.sslBackend=schannel \
  -c url."https://github.com/gzzhangliangjie/MusicGame".insteadof="https://github.com/gzzhangliangjie/MusicGame" \
  -c credential.https://github.com.helper= -c credential.helper= \
  -c credential.https://github.com.helper='!"/c/Program Files/GitHub CLI/gh.exe" auth git-credential' \
  push https://github.com/gzzhangliangjie/MusicGame.git main
```

注意事项：

- 大包（字体等二进制）易被重置：加 `-c http.version=HTTP/1.1`，或分批提交多次推。
- 凭据由 `gh auth` 管理，不要把 Token 写进远程 URL 或 git 配置持久化。
- 排查顺序：`git ls-remote ssh://git@ssh.github.com:443/...` 看远端是否已到位，避免重复推。
