# Agents

## 文档同步（硬性要求）

完成新功能/玩法/工具后，**同一次交付里必须更新 `docs/DEVELOPMENT_PLAN.md`**：

- 勾掉对应 Phase 条目（`[ ]` → `[x]`），补一行日期标注的实现说明（参数、验收数据）。
- 涉及数据格式变更（如 `analysis.json` 新字段）时同步「三、谱面与中间数据格式」。
- 大节点完成时更新文档头的「当前状态」总览。
- 用户验收通过后，文档更新与功能代码同一批提交推送，不得让文档长期落后于实现。

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


## 工具脚本规范

- **禁止用 heredoc（<< EOF）向 python/bash 内嵌长文本或代码**：定界符可能被内容静默吞掉导致脚本截断、只执行一半还不报错。需要临时脚本时用 Write 工具写成文件再执行（如 tools/ 下）。
- 编辑器实例开着时禁止再起第二个 Unity 实例（项目锁会弹窗打断用户）；批量验证前先 `Get-CimInstance Win32_Process` 查 Unity.exe。
