# Uviewer-Legado

> **Uviewer-Legado** = [Uviewer](https://github.com/kirinonakar/Uviewer) + **Legado 模式网络书源**。

![Uviewer Screenshot](Uviewer/Assets/screenshot.jpg)

## 项目内容

Uviewer 是一个基于 **WinUI 3 / .NET 10** 的 Windows 阅读与看图工具（图片、文本、PDF、EPUB、压缩包、WebDAV，支持竖排与青空文库格式）。

本分支在它之上集成了 **Legado（阅读 App）同构的书源引擎**：导入 legado 书源 JSON 后，可在应用内**搜索书籍 → 看详情 → 取目录 → 在线阅读 → 整本下载**，正文经「替换净化 + 标题规则」处理后交给原生阅读器排版，与本地文档共用同一套阅读界面与快捷键。

书源引擎随宿主一同编译（`Uviewer/Services/Legado/`），**不再有独立类库**。

## 与原版 Uviewer 的差异

| 方面 | 原版 Uviewer | Uviewer-Legado |
|---|---|---|
| 内容来源 | 本地文件、压缩包、WebDAV | 增加 **legado 网络书源**（JSON 导入 / 导出 / 启停 / 编辑） |
| 阅读链路 | 打开本地文档 | **搜索 → 详情 → 目录 → 正文 → 整本下载** |
| 文本处理 | — | 替换净化（正则 / 字面 / `@js:`）、标题与分章规则、TXT 自动分章 |
| 目录 | 文档内标题、PDF 书签 | 书源目录（多页跟随、倒序、`formatJs`、卷章层级） |
| 缓存 | 文档级 | 章节级缓存（原文 + 净化文本），前后各 3 章窗口预取，净化开关可逆 |
| 进度 | 书签 / 最近文件 | 书源**断点续读**（按书源 + 书籍 URL 记录章节与位置） |
| 网络 | WebDAV | 书源 HTTP 抓取：每源 Cookie、GB18030/GBK 等编码探测、超时重试、Referer 防盗链 |
| 界面 | — | 工具栏「书源」入口：书源管理、多源搜索（逐源流式渲染 + X/X 进度）、下载设置 |
| 工程结构 | 单一应用工程 | 同上，书源引擎内嵌 `Services/Legado`（`Models` / `BookSources`）；另含 `Uviewer.Bench` 基准与 `Uviewer.Legado.Tests` 测试 |

除上述内容外，**看图、PDF、EPUB、青空模式、WebDAV、托盘、快捷键等能力与原版完全一致**（完整功能列表见[上游 README](https://github.com/kirinonakar/Uviewer)）。

## 构建

```bash
dotnet build Uviewer/Uviewer.csproj -c Debug -p:Platform=x64
dotnet test  Uviewer.Legado.Tests/Uviewer.Legado.Tests.csproj
dotnet run --project Uviewer.Bench -c Release      # 引擎性能基准（-- p0|p1|p2|p3|p4）
```

## 致谢

- 上游原项目：**Uviewer** by [kirinonakar](https://github.com/kirinonakar)
- 书源规则语义对齐：**legado**（阅读）与 **reader**
