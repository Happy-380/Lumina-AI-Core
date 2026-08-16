🌟 Lumina AI Core

A fully offline, resource-friendly, "hands-on" local AI core
— Run a versatile assistant that chats and controls your computer on ordinary hardware, at low cost, with no GPU required.

---

✨ What Makes It Cool?

Feature Description
🧠 Ultra-lightweight Brain Powered by Bonsai 1-Bit LLM, runs smoothly on just 8 GB RAM — no GPU needed, no API fees. Achieves about 100 tokens/s on a 12th-gen Intel i5.
🎭 Built-in Personas Comes with two roles: "Evan" (precise) and "Mia" (lively). Zero-cost casual chats.
✨ Cute Output Style One‑click enable Miya language style to make responses more colloquial, warm, and adorable.
🖥️ Hands-on Capability Actually controls your Windows PC (open apps, read/write files, etc.) via the MCP protocol. Every action requires your confirmation — safe and controllable.
🧩 Developer Friendly Designed as a .NET class library — easily integrate into WinForms, WPF, Web, or WinUI apps to quickly build your own AI product.

---

🚀 Quick Start

Just three steps:

1. Download bin.zip.001 and bin.zip.002 from the latest Release
2. Unzip and double‑click Lumina-AI.exe
3. Type your question in the console

---

🛠️ I Want to Integrate into My Own Project

If you're a .NET developer, follow the instructions in the wiki to compile and launch the program.

---

🤝 Contributions & Feedback

Issues and PRs are welcome.
If you find this project helpful, don't forget to give it a ⭐!

---

⚠️ Important Notes

· The model file is large (Bonsai‑8B ~1.1 GB). The repository uses CopyToOutputDirectory="PreserveNewest" in the csproj to automatically copy it to the output directory.
· WindowsMcp.exe is a third‑party MCP server. Allowing the AI to control your PC carries security risks; please use it only in trusted environments.
· The program actively terminates all llama-server processes on exit. MCP client disposal may hang, so a 5‑second timeout guard is built in.
· The style‑conversion server is lazily loaded — it starts only the first time you select the Miya persona.
· The project does not automatically save history — all context is cleared when you close the program!

---

Enjoy! ~❤️
