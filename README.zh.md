# Lumina-AI-Core

一个基于 **llama.cpp + 本地 GGUF 模型** 的全能 AI 助手（.NET 8 控制台应用）。

Lumina-AI 完全离线运行，集成了本地大模型对话、Miya 语言风格转换、角色扮演（埃文 / 米娅）、以及通过 MCP 协议操控 Windows 电脑的能力。

Lumina-AI 使用 Bonsai 家族的模型，它们是 1-Bit LLM 。也就是说，它们的资源开销极小，但能为你的应用程序提供强劲的性能。

> **Core是“核心”的意思，也就是说，它只实现处理输入，就像人的大脑一样，所以它并不是一个图形化窗口，而是一个控制台。这个项目的构建目的就是实现一个本地的、高效的、安全的AI后端，并且完全可控，易于拓展和使用。完整的图形化应用程序可以查阅我（`Happy-380`）的`380AI`项目，不过到目前我们还没有为它适配`Lumina-AI-Core`，尽请期待吧~**

## 📇目录
1. **功能特性**
2. **技术栈**
3. **目录结构**
4. **环境要求**
5. **构建与运行**
6. **使用说明**
7. **配置说明**
8. **核心实现要点**
9. **注意事项**

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
├── LuminaOptions.cs               # 可调配置（宿主注入回调/事件，类库 API）
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
  - Fast（1.7B）：≥ 4 GB
  - Balanced（4B）：≥ 8 GB
  - Quality（8B）：≥ 16 GB
  **程序会按可用内存自动计算上下文长度**
 - 最低运行内存：2GB（但易崩溃，除非你的硬件资源及其有限，否则**不推荐**）

## 🚀 构建与运行

请下载`Release`中的`llama.zip.001`，`llama.zip.002`和`mcp.zip`。然后双击打开`llama.zip.001`，解压出其中的`llama`文件夹并放置在项目的根目录。再双击打开`mcp.zip`，解压出其中的`mcp`文件夹，也放置在项目根目录。最终文件夹结构应如下所示。
```
Lumina-AI/
├── Lumina-AI.csproj              # 项目文件（net8.0）
├── Lumina-AI.sln                 # 解决方案
├── Program.cs                    # 入口 + 核心服务（LlamaChatService、上下文/缓存/检索器）
├── LuminaOptions.cs               # 可调配置（宿主注入回调/事件，类库 API）
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

### 双模式构建

项目支持两种应用模式，通过 `-p:BuildAsLibrary` 切换：

```bash
# 模式 1：控制台应用（默认）
dotnet build -c Release              # 生成 Lumina-AI.exe

# 模式 2：类库（供其他项目引用）
dotnet build -c Release -p:BuildAsLibrary=true   # 生成 Lumina-AI.dll
```

类库模式下会自动通过 `LIBRARY_MODE` 条件编译排除控制台入口 `Program.Main`，只暴露 `LlamaChatService` / `LuminaOptions` 等公共 API。

### 运行控制台应用

```bash
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

## ⚙️ 配置说明

所有可调参数集中在 `LuminaOptions` 类（`LuminaOptions.cs`）中，其默认值取自原 `AppConfig` 静态常量（`Program.cs`）：

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
| `DefaultMode` | Balanced | 默认模型模式（`LuminaOptions.InitialMode`） |
| `StyleTransferPort` | 38090 | 风格转换服务器端口 |

> 风格转换相关配置（`StyleTransferPort` / `StyleTransferModel` / `StyleTransferContextSize`）由 `StyleTransferService` 内部管理，不对外开放配置。

### 端口规划

| 端口 | 用途 |
| --- | --- |
| 38080 | Quality 模式（Bonsai-8B） |
| 38081 | Balanced 模式（Bonsai-4B） |
| 38082 | Fast 模式（Bonsai-1.7B） |
| 38090 | 风格转换（Qwen2.5-0.5B） |

## 📦 类库 API（预留接口）

项目已为打包为类库做好准备：所有控制台交互均抽象为**回调/事件**，宿主程序可自由注入自己的 UI 实现。

### 1. 可调配置 `LuminaOptions`（语言风格转换器配置除外）

```csharp
var options = new LuminaOptions
{
    InitialMode = ModelMode.Balanced,      // 初始模型模式
    ManualContextSize = 16384,             // 手动上下文长度（null = 按内存自动计算）
    MaxResponseTokens = 1024,              // 单次回答最大 token
    EnableSemanticCache = true,            // 语义缓存
    SimilarityThreshold = 0.85,            // 缓存命中阈值
    MaxCacheEntries = 100,
    HistoryRetrievalTopK = 5,              // 历史检索召回条数
    RelevanceCheckRounds = 5,              // 相关性判断轮数
    RelevanceThreshold = 0.3,
    MaxToolCallIterations = 10,            // 工具调用循环上限
    LlamaFolderName = "llama",             // llama.cpp 目录
    McpFolderName = "mcp",                 // MCP 目录
    McpExeName = "WindowsMcp.exe",
    SystemPrompt = "...",                  // 自定义普通对话系统提示词
    ControlSystemPrompt = "...",           // 自定义操控模式系统提示词

    // 回调（UI 无关）：
    ConfirmCallback = async prompt => true,     // 用户确认（未设置时默认拒绝，安全）
    LogCallback = (level, msg) => Console.WriteLine(msg) // 日志
};
```

> 也可通过 `options.ModelFiles` / `options.ModelPorts` 覆盖模型文件与端口映射（`null` 时使用默认）。

### 2. 服务生命周期（异步初始化）

```csharp
await using var service = new LlamaChatService(options); // 构造函数只做内存初始化，不启动进程
await service.InitializeAsync();                          // 启动 llama-server + MCP
// ... 使用 ...
await service.DisposeAsync();                             // 清理进程与资源
```

### 3. 从外部导入上下文

```csharp
// 导入整段历史（JSON 数组）
service.ImportHistory(jArray);
service.ImportHistoryFromFile("history.json");

// 逐条导入（同时写入检索索引）
service.AddContextMessage("user", "内容");
service.AddContextMessage("assistant", "内容");

// 自定义系统提示词
service.SetSystemPrompt("普通对话提示词", "操控模式提示词");

// 导出当前上下文
JArray history = service.CurrentHistory;
```

### 4. 发送消息与接收回答

```csharp
// 方式一：返回值（推荐）
string answer = await service.SendMessageAsync("你好");          // 按 SelectedRole 决定是否风格转换
string answer = await service.SendMessageAsync("你好", LlamaChatService.ChatRole.MiyaBonsai); // 显式指定角色

// 方式二：事件订阅（每轮完成时触发：(用户输入, 回答)）
service.AnswerReceived += (input, answer) => Console.WriteLine($"{input} → {answer}");

// 角色模板直接回复（问候/身份/自我介绍/个人偏好，不走 AI；命中时自动记录上下文并触发 AnswerReceived）
string? template = service.GetTemplateReply("你好", LlamaChatService.ChatRole.MiyaBonsai);

// 其他
await service.SwitchModeAsync(ModelMode.Quality); // 热切换模型
service.ClearHistory();                            // 清除历史
service.GetCacheStats();                           // 缓存统计
```

> 未注入 `ConfirmCallback` 时，AI 操控电脑的操作会被**默认拒绝**（安全默认），宿主可自行实现弹窗/按键等确认 UI。

### 5. 完整使用实例（控制台宿主）

#### 5.1 引用类库

在宿主项目（如 WinForms / WPF / ASP.NET Core / 控制台）的 csproj 中添加引用：

```xml
<ItemGroup>
  <!-- 方式一：项目引用（推荐，随源码同步更新） -->
  <ProjectReference Include="..\Lumina-AI\Lumina-AI.csproj" />

  <!-- 方式二：DLL 引用（先以 -p:BuildAsLibrary=true 构建类库） -->
  <!-- <Reference Include="Lumina-AI">
       <HintPath>..\Lumina-AI\bin\Release\net8.0\Lumina-AI.dll</HintPath>
     </Reference> -->
</ItemGroup>
```

> **部署注意**：服务运行时从 `AppDomain.CurrentDomain.BaseDirectory`（宿主程序输出目录）查找 `llama/` 与 `mcp/` 目录。
> 引用类库不会自动传递内容文件，需将 `llama/`（含 GGUF 模型）与 `mcp/`（WindowsMcp.exe）复制到宿主输出目录，例如：
>
> ```xml
> <ItemGroup>
>   <Content Include="..\Lumina-AI\llama\**\*.*"
>            Link="llama\%(RecursiveDir)%(Filename)%(Extension)"
>            CopyToOutputDirectory="PreserveNewest" />
>   <Content Include="..\Lumina-AI\mcp\WindowsMcp.exe"
>            Link="mcp\WindowsMcp.exe"
>            CopyToOutputDirectory="PreserveNewest" />
> </ItemGroup>
> ```

#### 5.2 完整示例代码

```csharp
using LlamaChat;              // LuminaOptions / LlamaChatService / ModelMode / LogLevel
using Newtonsoft.Json.Linq;   // 导入 / 导出历史

// ============================================================
// 1. 配置：注入宿主自己的确认 UI 与日志（控制台示例）
// ============================================================
var options = new LuminaOptions
{
    InitialMode = ModelMode.Balanced,   // 初始模型：Bonsai-4B
    ManualContextSize = 16384,          // 手动上下文长度；不设则按可用内存自动计算
    MaxResponseTokens = 1024,
    EnableSemanticCache = true,
    RelevanceCheckRounds = 5,

    // 确认回调：AI 操控电脑 / 危险操作时触发。
    // 在 WinForms / WPF 中可换成 MessageBox 等弹窗；返回 true 才允许执行。
    ConfirmCallback = prompt =>
    {
        Console.WriteLine();
        Console.Write($"{prompt} (y/n): ");
        var key = Console.ReadKey();
        Console.WriteLine();
        return Task.FromResult(key.KeyChar is 'y' or 'Y');
    },

    // 日志回调：服务内部的所有状态输出都走这里
    LogCallback = (level, msg) => Console.WriteLine($"[{level}] {msg}"),
};

// ============================================================
// 2. 创建服务并启动（构造函数只做内存初始化，不启动进程）
// ============================================================
await using var service = new LlamaChatService(options);
await service.InitializeAsync();      // 启动 llama-server + MCP

// ============================================================
// 3. 接收回答（事件方式，每轮对话完成时触发）
// ============================================================
service.AnswerReceived += (input, answer) =>
    Console.WriteLine($"\n助手: {answer}\n");

// ============================================================
// 4. 从外部导入上下文
// ============================================================
// 4a. 逐条导入（同时写入 BM25 检索索引，供后续相关性召回）
service.AddContextMessage("user", "我叫小明，喜欢摄影。");
service.AddContextMessage("assistant", "很高兴认识你，小明！");

// 4b. 整段导入（JSON 数组，与导出格式一致）
service.ImportHistory(JArray.Parse("""
    [
      { "role": "user", "content": "上次聊到哪了？" },
      { "role": "assistant", "content": "我们上次在聊摄影构图技巧。" }
    ]
    """));

// 4c. 自定义系统提示词（普通对话 / 操控模式各一份）
service.SetSystemPrompt("你是一个友好的摄影顾问。", "你是能操控 Windows 的 AI 助手。");

// ============================================================
// 5. 发送消息与接收回答
// ============================================================
// 5a. 显式指定角色：MiyaBonsai 会额外做 Miya 风格转换（懒加载启动 Qwen2.5-0.5B）
string answer = await service.SendMessageAsync("你好呀", LlamaChatService.ChatRole.MiyaBonsai);
Console.WriteLine($"回答: {answer}");

// 5b. 角色模板直接回复（问候/身份询问/自我介绍/个人偏好，不走 AI）
string? template = service.GetTemplateReply("你是谁", LlamaChatService.ChatRole.Ewin);
if (template != null)
    Console.WriteLine($"模板: {template}");

// 5c. 热切换模型（会重启对应端口的 llama-server）
await service.SwitchModeAsync(ModelMode.Quality);

// 5d. 交互循环：按 SelectedRole（默认 MiyaBonsai）自动决定是否风格转换
while (true)
{
    Console.Write("用户: ");
    string? input = Console.ReadLine();
    if (string.IsNullOrEmpty(input)) continue;
    if (input == "exit") break;

    string reply = await service.SendMessageAsync(input);   // 返回值方式接收
    Console.WriteLine($"助手: {reply}");
}

// ============================================================
// 6. 退出（自动清理 llama-server / MCP 进程）
// ============================================================
await service.DisposeAsync();
```

## 🧠 核心实现要点

### LlamaChatService（Program.cs）

- 负责 llama-server 进程生命周期管理（启动、健康检查、残留进程清理、模式热切换）。
- 组装请求：系统提示（普通对话 / 操控模式两种）、检索到的相关历史、滑动窗口、当前输入。
- 工具调用循环：最多 10 轮迭代，处理 `tool_calls` → 执行 MCP 工具 → 回填结果。
- 上下文按 `user` / `assistant` 消息保存（角色不写入上下文，仅决定是否风格转换）。

### StyleTransferService

- 通过 `/completion` + `cache_prompt` 增量生成。
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
- **项目不会自动保存历史记录，关闭程序后以前的上下文就会删除！**
