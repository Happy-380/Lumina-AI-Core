# Lumina AI Core

**[简体中文](README.md) | English**

---
An all‑in‑one AI assistant built on **llama.cpp + local GGUF models** (.NET 8 console application).

Lumina‑AI runs completely offline, integrating local large‑model chat, Miya style transfer, role‑playing (Ewin / Miya), and Windows control via the MCP protocol.

Lumina‑AI uses models from the Bonsai family, which are 1‑Bit LLMs. This means they have minimal resource overhead while delivering strong performance for your applications.

> **"Core"** means this is the brain – it processes inputs without a graphical interface, just a console. The goal is to build a local, efficient, secure AI backend that is fully controllable, easy to extend, and easy to use. For a full graphical application, check out my (`Happy-380`) `380AI` project, though it hasn't been adapted for `Lumina-AI-Core` yet – stay tuned!

## 📇 Table of Contents
1. **Features**
2. **Tech Stack**
3. **Directory Structure**
4. **System Requirements**
5. **Build & Run**
6. **Usage**
7. **Configuration**
8. **Core Implementation Highlights**
9. **Important Notes**

## ✨ Features

- **Local Model Inference** – Built‑in llama.cpp runtime with 3 GGUF models; no internet or API key required.
- **Three Model Modes** – Fast (Bonsai‑1.7B), Balanced (Bonsai‑4B), Quality (Bonsai‑8B) with runtime hot‑switching.
- **Miya Style Transfer** – A separate small model (Qwen2.5‑0.5B) converts the assistant’s replies into a "colloquial, cute, feminine, slightly coquettish" style, with automatic Chinese/English detection.
- **Role‑Play Templates** – `CharacterIdentityService` provides two personas: Ewin (male photography enthusiast) and Miya (female who loves plants and baking). Greetings, identity queries, self‑introductions, and personal preferences are answered directly from templates without consuming AI inference.
- **MCP Computer Control** – Integrates `WindowsMcp.exe` via the [Model Context Protocol](https://modelcontextprotocol.io). The AI can call tools to control Windows (open programs, read/write files, move mouse, etc.). Every action requires user confirmation; dangerous actions need a second confirmation.
- **Intelligent Context Management**:
  - Sliding window + context length automatically computed based on available memory.
  - Semantic cache (Trigram similarity deduplication, default threshold 0.85).
  - BM25 history retriever (inverted index + relevance recall).
  - Relevance check with recent conversations to decide whether to include context automatically.
- **Markdown Structure Protection** – Style transfer preserves code blocks, tables, links, bold/italic, strikethrough, inline code, footnotes, and other formatting.

## 🧱 Tech Stack

| Component | Description |
| --- | --- |
| .NET 8 | Target framework `net8.0`, console application |
| llama.cpp | Local inference backend (`llama-server.exe`, OpenAI‑compatible HTTP API) |
| GGUF Models | Bonsai‑1.7B / 4B / 8B, Qwen2.5‑0.5B |
| ModelContextProtocol 2.0.0 | MCP client (stdio transport) |
| Newtonsoft.Json 13.0.4 | JSON serialization |

## 📁 Directory Structure

```
Lumina-AI/
├── Lumina-AI.csproj              # Project file (net8.0)
├── Lumina-AI.sln                 # Solution
├── Program.cs                    # Entry point + core services (LlamaChatService, context/cache/retriever)
├── LuminaOptions.cs               # Tunable configuration (host injection callbacks/events, library API)
├── StyleTransferService.cs       # Miya language style transfer service (incremental generation + semantic drift stop)
├── CharacterIdentityService.cs   # Character identity templates (Ewin / Miya)
├── llama/                        # llama.cpp runtime + models (copied to output directory on build)
│   ├── llama-server.exe          # Inference server
│   ├── Bonsai-1.7B.gguf          # Fast mode
│   ├── Bonsai-4B.gguf            # Balanced mode (default)
│   ├── Bonsai-8B.gguf            # Quality mode
│   └── Qwen2.5-0.5B-Q4_K_M.gguf  # Style transfer model
└── mcp/
    └── WindowsMcp.exe            # MCP server (Windows manipulation tools)
```

## ⚙️ System Requirements

- **Windows** (relies on Win32 API for memory queries and console ANSI colours)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Recommended memory:
  - Fast (1.7B): ≥ 4 GB
  - Balanced (4B): ≥ 8 GB
  - Quality (8B): ≥ 16 GB
  **The program automatically calculates context length based on available memory.**
- Minimum running memory: 2 GB (but prone to crashes; **not recommended** unless your hardware is extremely limited)

## 🚀 Build & Run

Download `llama.zip.001`, `llama.zip.002` and `mcp.zip` from the Release assets. Then double‑click `llama.zip.001` to open it, extract the `llama` folder and place it in the project root. Next, double‑click `mcp.zip`, extract the `mcp` folder, and also place it in the project root. The final folder structure should be as shown below.

```
Lumina-AI/
├── Lumina-AI.csproj              # Project file (net8.0)
├── Lumina-AI.sln                 # Solution
├── Program.cs                    # Entry point + core services (LlamaChatService, context/cache/retriever)
├── LuminaOptions.cs               # Tunable configuration (host injection callbacks/events, library API)
├── StyleTransferService.cs       # Miya language style transfer service (incremental generation + semantic drift stop)
├── CharacterIdentityService.cs   # Character identity templates (Ewin / Miya)
├── llama/                        # llama.cpp runtime + models (copied to output directory on build)
│   ├── llama-server.exe          # Inference server
│   ├── Bonsai-1.7B.gguf          # Fast mode
│   ├── Bonsai-4B.gguf            # Balanced mode (default)
│   ├── Bonsai-8B.gguf            # Quality mode
│   └── Qwen2.5-0.5B-Q4_K_M.gguf  # Style transfer model
└── mcp/
    └── WindowsMcp.exe            # MCP server (Windows manipulation tools)
```

### Dual‑mode Build

The project supports two application modes, switched via `-p:BuildAsLibrary`:

```bash
# Mode 1: Console application (default)
dotnet build -c Release              # Produces Lumina-AI.exe

# Mode 2: Class library (for reference by other projects)
dotnet build -c Release -p:BuildAsLibrary=true   # Produces Lumina-AI.dll
```

In library mode, the console entry point `Program.Main` is automatically excluded via the `LIBRARY_MODE` conditional compilation symbol, exposing only the public APIs such as `LlamaChatService` / `LuminaOptions`.

### Run the Console Application

```bash
# Run (default Balanced mode)
dotnet run --project Lumina-AI.csproj

# Or directly run the executable in the output directory
cd bin/Release/net8.0
Lumina-AI.exe
```

On first startup, `llama-server` is launched automatically (context initialised based on available memory). Once ready, you can start chatting.

### Command‑line Arguments

```bash
# Specify the initial model mode
Lumina-AI.exe --mode fast        # fast | balanced | quality

# Import conversation history (JSON array format: [{"role":"user","content":"..."}, ...])
Lumina-AI.exe --history history.json
```

## 💬 Usage

### Chatting

After startup, type your input and chat directly. Before each round, you will be asked to choose the responding character:

```
Select response character: 1) Ewin  2) Miya-Bonsai  [Enter for default Miya-Bonsai]
```

- **Ewin**: The model outputs directly, without style transfer.
- **Miya-Bonsai**: The model’s reply is then transformed by Qwen2.5‑0.5B into Miya’s style (colloquial, cute, slightly coquettish).

### Built‑in Commands

| Command | Description |
| --- | --- |
| `/mode fast\|balanced\|quality` | Switch model mode (restarts the llama‑server on the corresponding port) |
| `/clear` | Clear conversation history (including retrieval index) |
| `/stats` | View semantic cache statistics |
| `exit` | Exit the program (automatically cleans up llama‑server / MCP processes) |

### Computer Control (MCP)

When MCP tools are loaded successfully, each round will ask whether you allow AI to control your computer:

1. Type `y` to allow control for this round, `n` for normal chat only.
2. If the AI thinks a tool is needed, it will issue a tool call, which is executed after a second confirmation.
3. **Dangerous operations** (e.g., `file_write`, `process`, `registry_set`, `power_action` and other blacklisted tools) will trigger an extra confirmation.

> The tool blacklist is defined in the `_dangerousTools` collection inside `Program.cs` – you can add or remove items as needed.

## ⚙️ Configuration

All tunable parameters are centralised in the `LuminaOptions` class (`LuminaOptions.cs`). Their default values come from the original `AppConfig` static constants (in `Program.cs`):

| Parameter | Default | Description |
| --- | --- | --- |
| `ManualContextSize` | 0 | Manually set context length (0 = auto‑calculate based on memory) |
| `MaxResponseTokens` | 1024 | Maximum tokens per response |
| `EnableSemanticCache` | true | Enable semantic caching |
| `SimilarityThreshold` | 0.85 | Cache hit similarity threshold |
| `MaxCacheEntries` | 100 | Maximum cache entries |
| `HistoryRetrievalTopK` | 5 | Number of history items to retrieve |
| `RelevanceCheckRounds` | 5 | Recent rounds used for relevance determination |
| `RelevanceThreshold` | 0.3 | Relevance threshold |
| `DefaultMode` | Balanced | Default model mode (`LuminaOptions.InitialMode`) |
| `StyleTransferPort` | 38090 | Style transfer server port |

> Style‑transfer‑related settings (`StyleTransferPort` / `StyleTransferModel` / `StyleTransferContextSize`) are managed internally by `StyleTransferService` and are not exposed for configuration.

### Port Mapping

| Port | Purpose |
| --- | --- |
| 38080 | Quality mode (Bonsai‑8B) |
| 38081 | Balanced mode (Bonsai‑4B) |
| 38082 | Fast mode (Bonsai‑1.7B) |
| 38090 | Style transfer (Qwen2.5‑0.5B) |

## 📦 Library API (Reserved Interface)

The project is ready to be packaged as a class library: all console interactions are abstracted as **callbacks/events**, so host applications can freely inject their own UI implementations.

### 1. Tunable Configuration `LuminaOptions` (except style‑transfer settings)

```csharp
var options = new LuminaOptions
{
    InitialMode = ModelMode.Balanced,      // Initial model mode
    ManualContextSize = 16384,             // Manual context length (null = auto based on memory)
    MaxResponseTokens = 1024,              // Maximum tokens per response
    EnableSemanticCache = true,            // Semantic cache
    SimilarityThreshold = 0.85,            // Cache hit threshold
    MaxCacheEntries = 100,
    HistoryRetrievalTopK = 5,              // History retrieval top‑K
    RelevanceCheckRounds = 5,              // Rounds for relevance check
    RelevanceThreshold = 0.3,
    MaxToolCallIterations = 10,            // Tool call loop limit
    LlamaFolderName = "llama",             // llama.cpp folder
    McpFolderName = "mcp",                 // MCP folder
    McpExeName = "WindowsMcp.exe",
    SystemPrompt = "...",                  // Custom normal‑chat system prompt
    ControlSystemPrompt = "...",           // Custom control‑mode system prompt

    // Callbacks (UI‑agnostic):
    ConfirmCallback = async prompt => true,     // User confirmation (default deny if not set, safe)
    LogCallback = (level, msg) => Console.WriteLine(msg) // Logging
};
```

> You can also override model files and port mappings via `options.ModelFiles` / `options.ModelPorts` (set to `null` to use defaults).

### 2. Service Lifecycle (Asynchronous Initialisation)

```csharp
await using var service = new LlamaChatService(options); // Constructor does memory init only, no process start
await service.InitializeAsync();                          // Starts llama‑server + MCP
// ... use ...
await service.DisposeAsync();                             // Clean up processes and resources
```

### 3. Import Context from External Sources

```csharp
// Import entire history (JSON array)
service.ImportHistory(jArray);
service.ImportHistoryFromFile("history.json");

// Add messages one by one (also updates the retrieval index)
service.AddContextMessage("user", "Content");
service.AddContextMessage("assistant", "Content");

// Custom system prompts
service.SetSystemPrompt("Normal chat prompt", "Control mode prompt");

// Export current context
JArray history = service.CurrentHistory;
```

### 4. Send Messages and Receive Replies

```csharp
// Method 1: Return value (recommended)
string answer = await service.SendMessageAsync("Hello");          // Uses SelectedRole to decide style transfer
string answer = await service.SendMessageAsync("Hello", LlamaChatService.ChatRole.MiyaBonsai); // Explicit role

// Method 2: Event subscription (fires after each round: (user input, answer))
service.AnswerReceived += (input, answer) => Console.WriteLine($"{input} → {answer}");

// Template reply (greetings/identity/self‑intro/preferences – no AI; on hit, it auto‑records context and triggers AnswerReceived)
string? template = service.GetTemplateReply("Hello", LlamaChatService.ChatRole.MiyaBonsai);

// Other operations
await service.SwitchModeAsync(ModelMode.Quality); // Hot‑switch model
service.ClearHistory();                            // Clear history
service.GetCacheStats();                           // Cache statistics
```

> If `ConfirmCallback` is not injected, AI control actions are **denied by default** (safe default). The host can implement its own confirmation UI (pop‑ups, buttons, etc.).

### 5. Complete Usage Example (Console Host)

#### 5.1 Referencing the Library

Add a reference in your host project’s csproj (e.g., WinForms, WPF, ASP.NET Core, console):

```xml
<ItemGroup>
  <!-- Option 1: Project reference (recommended, stays in sync with source) -->
  <ProjectReference Include="..\Lumina-AI\Lumina-AI.csproj" />

  <!-- Option 2: DLL reference (first build the library with -p:BuildAsLibrary=true) -->
  <!-- <Reference Include="Lumina-AI">
       <HintPath>..\Lumina-AI\bin\Release\net8.0\Lumina-AI.dll</HintPath>
     </Reference> -->
</ItemGroup>
```

> **Deployment note**: The service looks for the `llama/` and `mcp/` folders in `AppDomain.CurrentDomain.BaseDirectory` (the host’s output directory). The library reference does **not** automatically copy content files; you must copy `llama/` (including GGUF models) and `mcp/` (WindowsMcp.exe) to your host output directory, e.g.:
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

#### 5.2 Complete Sample Code

```csharp
using LlamaChat;              // LuminaOptions / LlamaChatService / ModelMode / LogLevel
using Newtonsoft.Json.Linq;   // Import / export history

// ============================================================
// 1. Configuration: inject host‑specific confirmation UI and logging (console example)
// ============================================================
var options = new LuminaOptions
{
    InitialMode = ModelMode.Balanced,   // Initial model: Bonsai-4B
    ManualContextSize = 16384,          // Manual context length; leave null for auto based on memory
    MaxResponseTokens = 1024,
    EnableSemanticCache = true,
    RelevanceCheckRounds = 5,

    // Confirmation callback: triggered for AI computer control / dangerous operations.
    // In WinForms/WPF, replace with MessageBox etc.; only return true to allow execution.
    ConfirmCallback = prompt =>
    {
        Console.WriteLine();
        Console.Write($"{prompt} (y/n): ");
        var key = Console.ReadKey();
        Console.WriteLine();
        return Task.FromResult(key.KeyChar is 'y' or 'Y');
    },

    // Log callback: all internal status outputs go here
    LogCallback = (level, msg) => Console.WriteLine($"[{level}] {msg}"),
};

// ============================================================
// 2. Create and initialise the service (constructor does memory init only, not process start)
// ============================================================
await using var service = new LlamaChatService(options);
await service.InitializeAsync();      // Starts llama-server + MCP

// ============================================================
// 3. Receive answers (event approach, fires after each round)
// ============================================================
service.AnswerReceived += (input, answer) =>
    Console.WriteLine($"\nAssistant: {answer}\n");

// ============================================================
// 4. Import context from external sources
// ============================================================
// 4a. Add messages one by one (also writes to BM25 index for later relevance recall)
service.AddContextMessage("user", "My name is Xiao Ming, I like photography.");
service.AddContextMessage("assistant", "Nice to meet you, Xiao Ming!");

// 4b. Import entire history (JSON array, same format as export)
service.ImportHistory(JArray.Parse("""
    [
      { "role": "user", "content": "Where did we leave off?" },
      { "role": "assistant", "content": "We were discussing photography composition techniques." }
    ]
    """));

// 4c. Custom system prompts (normal chat / control mode)
service.SetSystemPrompt("You are a friendly photography advisor.", "You are an AI assistant that can control Windows.");

// ============================================================
// 5. Send messages and receive replies
// ============================================================
// 5a. Explicit role: MiyaBonsai will apply Miya style transfer (lazy‑loads Qwen2.5-0.5B)
string answer = await service.SendMessageAsync("Hello there", LlamaChatService.ChatRole.MiyaBonsai);
Console.WriteLine($"Answer: {answer}");

// 5b. Template reply (greetings/identity/self‑intro/preferences – no AI)
string? template = service.GetTemplateReply("Who are you", LlamaChatService.ChatRole.Ewin);
if (template != null)
    Console.WriteLine($"Template: {template}");

// 5c. Hot‑switch model (restarts llama-server on the corresponding port)
await service.SwitchModeAsync(ModelMode.Quality);

// 5d. Interactive loop: uses SelectedRole (default MiyaBonsai) to decide style transfer automatically
while (true)
{
    Console.Write("User: ");
    string? input = Console.ReadLine();
    if (string.IsNullOrEmpty(input)) continue;
    if (input == "exit") break;

    string reply = await service.SendMessageAsync(input);   // Return‑value approach
    Console.WriteLine($"Assistant: {reply}");
}

// ============================================================
// 6. Exit (automatically cleans up llama-server / MCP processes)
// ============================================================
await service.DisposeAsync();
```

## 🧠 Core Implementation Highlights

### LlamaChatService (Program.cs)

- Manages the lifecycle of `llama-server` processes (startup, health checks, cleaning of stale processes, hot‑switching modes).
- Builds the request: system prompts (two modes: normal chat and control), retrieved relevant history, sliding window, and current input.
- Tool call loop: up to 10 iterations, handling `tool_calls` → executing MCP tools → feeding back results.
- Context is stored as `user` / `assistant` messages (the role is not written into the context; it only decides whether to apply style transfer).

### StyleTransferService

- Incrementally generates via `/completion` with `cache_prompt`.
- **Semantic drift stopping criterion**: compares the similarity between the generated window and the token set of the original text. Stops when similarity drops (current < peak × 0.70) or diversity becomes too low, preventing the model from “parroting” the original.
- Preheats stopword / punctuation token IDs at startup for drift detection.
- Markdown line‑by‑line conversion: code blocks, tables, images, and footnote definitions are skipped entirely; titles/lists/blockquotes have their prefix extracted and only the body is converted; links only convert the description text; Chinese uses Z‑placeholder and English uses named placeholders to protect inline formatting.

### CharacterIdentityService

- Rule engine identifies four intents: greetings, identity queries, self‑introductions, and personal preferences (with extensive false‑positive prevention logic).
- Multiple template variants are maintained for both Ewin and Miya (greetings are time‑sensitive: morning/noon/afternoon/evening/late night) and randomly combined to ensure varied responses.
- Template replies are also written into the context, but not cached semantically.

## ⚠️ Important Notes

- Model files are large (Bonsai‑8B is about 1.1 GB). The repository uses `CopyToOutputDirectory="PreserveNewest"` in the csproj to automatically copy them to the output directory.
- `WindowsMcp.exe` is a third‑party MCP server. Allowing AI to control your computer carries security risks – use only in trusted environments.
- Upon exit, the program actively cleans up all `llama-server` processes. MCP client disposal may hang; a 5‑second timeout guard is built in.
- The style‑transfer server is lazy‑loaded, starting only when the Miya role is selected for the first time.
- **The project does not automatically save conversation history – all context is lost when the program is closed!**
