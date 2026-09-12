# Local AI worker

`Invoke-LocalAiWorker.ps1` sends a narrowly scoped coding task to the local Ollama model and returns its proposed unified diff. It never applies that diff itself.

Start Ollama once:

```powershell
ollama serve
```

Ask for a small change with selected source files as context:

```powershell
.\tools\Invoke-LocalAiWorker.ps1 `
  -Task 'Add a guard for a null settings value. Return a unified diff only.' `
  -ContextPath .\TEHhub\Settings\SettingsWindow.cs `
  -OutputPath .\artifacts\ai-proposals\settings-guard.diff
```

The default model is `qwen2.5-coder:7b-instruct-q4_K_M`. Review the returned diff, build the affected project, and only then apply it.

For a large design review or a dump investigation, raise both limits explicitly. The model can be given up to 256K tokens when the local Ollama configuration supports it:

```powershell
.\tools\Invoke-LocalAiWorker.ps1 `
  -Task 'Review the supplied offset recovery design and return only prioritized risks.' `
  -ContextPath .\Documentation\guides\PRIMARY_ROOT_RESEARCH.md,.\Documentation\guides\OFFSET_TRYFIX.md `
  -ContextLength 262144 `
  -ContextCharactersPerFile 32768 `
  -MaxTotalContextCharacters 900000
```

Use 32K for ordinary implementation tasks. A 256K context needs substantially more memory for the attention cache, so it is most useful for read-heavy reviews rather than every small change.
