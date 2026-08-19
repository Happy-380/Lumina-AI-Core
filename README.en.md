# 🌟 Lumina AI Core

> **A fully offline, resource-friendly, “hands-on” local AI core**  
> — Turn your ordinary PC into a versatile assistant that can both chat and control your computer, at low cost.

---

[简体中文](README.md) | **English**

---

## ✨ What Makes It Cool?

| Feature | Description |
|---------|-------------|
| 🧠 **Ultra-lightweight Brain** | Based on the **Bonsai 1-Bit LLM**, runs smoothly with just 8GB RAM, no GPU required, and no API fees. Reaches about **100 tokens/s** on a 12th-gen Intel i5. |
| 🎭 **Built-in Personas** | Comes with two roles: “Evan” (precise) and “Mia” (lively) — zero-cost small talk. |
| ✨ **Cute Output Style** | Toggle **Miya language style** with one click to make AI responses colloquial, adorable, and warm. |
| 🖥️ **Hands-on Ability** | Truly controls your Windows PC via the **MCP protocol** (open apps, read/write files, etc.). Each operation requires your confirmation for safety and control. |
| 🧩 **Developer Friendly** | Designed as a **.NET class library** — easily integrate into WinForms, WPF, Web, WinUI applications, and quickly build your own AI products. |

---

## Demo Video

https://github.com/user-attachments/assets/13d7b12d-a524-4139-a287-4145b8ba5d2b

---

## 🚀 Quick Start

**Just three steps:**
1. Download `bin.zip.001` and `bin.zip.002` from the latest [Release](https://github.com/Happy-380/Lumina-AI-Core/releases)
2. Unzip and double-click `Lumina-AI.exe`
3. Type your question in the console

---

## 🛠️ I Want to Integrate into My Own Project

If you're a .NET developer, follow the instructions in the wiki to build and run the program.

---

## 🤝 Contributions & Feedback

Issues and PRs are welcome.  
If you find this project helpful, don't forget to give it a ⭐!

---

## ⚠️ Notes

- The model file is large (Bonsai-8B ~1.1 GB). The repo uses `CopyToOutputDirectory="PreserveNewest"` in the csproj to automatically copy it to the output directory.
- `WindowsMcp.exe` is a third-party MCP server. Allowing AI to control your computer carries security risks — please use it only in trusted environments.
- The program cleans up all `llama-server` processes on exit. MCP client release may hang, so a 5‑second timeout protection is built in.
- The style conversion server is lazily loaded and starts only when the Mia role is selected for the first time.
- **The project does not automatically save chat history — all previous context is cleared when the program is closed!**

---

**Enjoy using it~ ❤️**
