# Lumina-AI

一个基于 **llama.cpp + 本地 GGUF 模型** 的全能 AI 助手（.NET 8 控制台应用）。

Lumina-AI 完全离线运行，集成了本地大模型对话、Miya 语言风格转换、角色扮演（埃文 / 米娅）、以及通过 MCP 协议操控 Windows 电脑的能力。

## ✨ 功能特性

- **本地模型推理**：内置 llama.cpp 运行时与 3 个 GGUF 模型，无需联网、无需 API Key。
- **三档模型模式**：Fast（Bonsai-1.7B）/ Balanced（Bonsai-4B）/ Quality（Bonsai-8B），支持运行时热切换。
- **Miya 语言风格转换**：独立的小模型（Qwen2.5-0.5B）将模型回答转换为"口语化、可爱、女性化、略带撒娇"的风格，支持中英文自动检测。
- **角色扮演模板**：`CharacterIdentityService` 提供埃文（Ewin，摄影爱好者男生）与米娅（Miya，爱花草烘焙的女生）两套人设，问候 / 身份询问 / 自我介绍 / 个人偏好等场景直接走模板回复，无需消耗 AI 推理。
- **MCP 电脑操控**：通过 [Model Context Protocol](https://modelcontextprotocol.io) 接入 `WindowsMcp.exe`，AI 可调用工具操控 Windows（打开程序、读写文件、移动鼠标等），每次操作需用户确认，危险操作二次确认。
- **智能上下文管理**：
  - 滑动窗口 + 基于可用内存自动计算的上下文长度；
  - 语义缓存（Trigram 相似度去重，默认阈值 0.85）；
  - BM25 历史检索器（倒排索引 + 相关性召回）；
  - 与最近对话的相关性判断，自动决定是否携带上下文。
- **Markdown 结构保护**：风格转换时保留代码块、表格、链接、粗体/斜体、删除线、行内代码、脚注等格式不被破坏。

## 🧱 技术栈

| 组件 | 说明 |
| --- | --- |
| .NET 8 | 目标框架 `net8.0`，控制台应用 |
| llama.cpp | 本地推理后端（`llama-server.exe`，OpenAI 兼容 HTTP API） |
| GGUF 模型 | Bonsai-1.7B / 4B / 8B、Qwen2.5-0.5B |
| ModelContextProtocol 2.0.0 | MCP 客户端（stdio 传输） |
| Newtonsoft.Json 13.0.4 | JSON 序列化 |

## 📁 目录结构

```
Lumina-AI/
├── Lumina-AI.csproj              # 项目文件（net8.0）
├── Lumina-AI.sln                 # 解决方案
├── Program.cs                    # 入口 + 核心服务（LlamaChatService、上下文/缓存/检索器）
├── StyleTransferService.cs       # Miya 语言风格转换服务（增量生成 + 语义漂移停止）
├── CharacterIdentityService.cs   # 角色身份模板（埃文 / 米娅）
├── llama/                        # llama.cpp 运行时 + 模型（构建时复制到输出目录）
│   ├── llama-server.exe          # 推理服务器
│   ├── Bonsai-1.7B.gguf          # Fast 模式
│   ├── Bonsai-4B.gguf            # Balanced 模式（默认）
│   ├── Bonsai-8B.gguf            # Quality 模式
│   └── Qwen2.5-0.5B-Q4_K_M.gguf  # 风格转换模型
└── mcp/
    └── WindowsMcp.exe            # MCP 服务器（Windows 操控工具）
```

## ⚙️ 环境要求

- **Windows**（依赖 Win32 API 内存查询与控制台 ANSI 颜色）
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- 建议内存：
  - Fast（1.7B）：≥ 8 GB
  - Balanced（4B）：≥ 16 GB
  - Quality（8B）：≥ 24 GB（程序会按可用内存自动计算上下文长度）

## 🚀 构建与运行

```bash
# 构建
dotnet build -c Release

# 运行（默认 Balanced 模式）
dotnet run --project Lumina-AI.csproj

# 或直接运行输出目录中的可执行文件
cd bin/Release/net8.0
Lumina-AI.exe
```

首次启动会自动拉起 `llama-server`（根据可用内存初始化上下文），启动完成后即可对话。

### 命令行参数

```bash
# 指定初始模型模式
Lumina-AI.exe --mode fast        # fast | balanced | quality

# 导入历史对话（JSON 数组格式：[{"role":"user","content":"..."}, ...]）
Lumina-AI.exe --history history.json
```

## 💬 使用说明

### 对话

启动后输入内容直接对话。每轮对话前会询问回答角色：

```
选择回答角色：1) Ewin  2) Miya-Bonsai  [回车默认 Miya-Bonsai]
```

- **Ewin**：模型直接输出，不做风格转换。
- **Miya-Bonsai**：模型回答后再经 Qwen2.5-0.5B 转换为米娅风格（口语化、可爱、略带撒娇）。

### 内置命令

| 命令 | 说明 |
| --- | --- |
| `/mode fast\|balanced\|quality` | 切换模型模式（会重启对应端口的 llama-server） |
| `/clear` | 清除对话历史（含检索索引） |
| `/stats` | 查看语义缓存统计 |
| `exit` | 退出程序（自动清理 llama-server / MCP 进程） |

### 电脑操控（MCP）

当 MCP 工具加载成功后，每轮对话会询问是否允许 AI 操控电脑：

1. 输入 `y` 允许本次操控，`n` 仅普通对话。
2. AI 认为需要工具时会发起工具调用，再次确认后执行。
3. 涉及**危险操作**（如 `file_write`、`process`、`registry_set`、`power_action` 等黑名单工具）会额外弹一次确认。

> 工具名单见 `Program.cs` 中 `_dangerousTools` 集合，可按需增删。

## 🔧 配置说明

所有可调参数集中在 `Program.cs` 的 `AppConfig` 静态类中：

| 参数 | 默认值 | 说明 |
| --- | --- | --- |
| `ManualContextSize` | 0 | 手动指定上下文长度（0 = 按内存自动计算） |
| `MaxResponseTokens` | 1024 | 单次回答最大 token 数 |
| `EnableSemanticCache` | true | 是否启用语义缓存 |
| `SimilarityThreshold` | 0.85 | 缓存命中相似度阈值 |
| `MaxCacheEntries` | 100 | 缓存条目上限 |
| `HistoryRetrievalTopK` | 5 | 历史检索召回条数 |
| `RelevanceCheckRounds` | 5 | 相关性判断参考最近 N 轮 |
| `RelevanceThreshold` | 0.3 | 相关性阈值 |
| `DefaultMode` | Balanced | 默认模型模式 |
| `StyleTransferPort` | 38090 | 风格转换服务器端口 |

### 端口规划

| 端口 | 用途 |
| --- | --- |
| 38080 | Quality 模式（Bonsai-8B） |
| 38081 | Balanced 模式（Bonsai-4B） |
| 38082 | Fast 模式（Bonsai-1.7B） |
| 38090 | 风格转换（Qwen2.5-0.5B） |

## 🧠 核心实现要点

### LlamaChatService（Program.cs）

- 负责 llama-server 进程生命周期管理（启动、健康检查、残留进程清理、模式热切换）。
- 组装请求：系统提示（普通对话 / 操控模式两种）、检索到的相关历史、滑动窗口、当前输入。
- 工具调用循环：最多 10 轮迭代，处理 `tool_calls` → 执行 MCP 工具 → 回填结果。
- 上下文按 `user` / `assistant` 消息保存（角色不写入上下文，仅决定是否风格转换）。

### StyleTransferService

- 移植自 Python 原型，通过 `/completion` + `cache_prompt` 增量生成。
- **语义漂移停止准则**：比较生成窗口与原文 token 集合的相似度，相似度回落（当前 < 峰值 × 0.70）或多样性过低时停止，防止模型"复读"原文。
- 启动时预热停用词 / 标点 token ID，用于漂移检测。
- Markdown 逐行转换：代码块、表格、图片、脚注定义整体跳过；标题 / 列表 / 引用提取前缀后仅转换正文；链接仅转换描述文字；中英文分别用 Z 占位符 / 命名占位符保护行内格式。

### CharacterIdentityService

- 规则引擎识别问候、身份询问、自我介绍、个人偏好四类意图（含大量误判排除逻辑）。
- 埃文与米娅各维护多套模板（问候按时间段区分：早晨/中午/下午/晚上/深夜），随机组合，保证每次回复不重复。
- 模板回复同样写入上下文，但不进语义缓存。

## ⚠️ 注意事项

- 模型文件较大（Bonsai-8B 约 1.1 GB），仓库通过 csproj 的 `CopyToOutputDirectory="PreserveNewest"` 自动拷贝到输出目录。
- `WindowsMcp.exe` 为第三方 MCP 服务器，允许 AI 操控电脑存在安全风险，请仅在可信环境中使用。
- 程序退出时会主动清理所有 `llama-server` 进程；MCP 客户端释放可能挂起，内置了 5 秒超时保护。
- 风格转换服务器为懒加载，仅在第一次选择 Miya 角色时启动。
