# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

WeChatAutomation is a **general-purpose Windows desktop automation recording and playback tool** built on .NET 9 + WPF. Despite the name, it works with any Windows application. It records user actions (mouse clicks, keyboard input) via global Windows hooks and plays them back with human-like mouse movement. Three click modes are supported: Coordinate, UIA Path (UI Automation with XPath), and Vision (YOLOv8 ONNX detection).

## Build & Run Commands

```bash
# Build (from WeChatAutomation/ directory)
dotnet build
dotnet build -c Release

# Run the WPF app
dotnet run --project src\WeChatAutomation.App

# Publish single-file self-contained (x64)
.\publish-quick.ps1

# Publish all architectures (x64, x86, ARM64)
.\publish-all.ps1
```

No test project exists. No linter is configured.

## Architecture

The solution (`WeChatAutomation.sln`) contains two projects:

- **WeChatAutomation.Core** — All business logic, native interop, and services
- **WeChatAutomation.App** — WPF frontend (references Core)

### Core Module Map

```
Native/           Win32 P/Invoke (User32.cs), global hooks (MouseHook, KeyboardHook),
                  human-like input simulation (HumanInputSimulator — Bezier curves + jitter)

Recording/        ActionRecorder (captures events from hooks → RecordedAction objects),
                  ActionPlayer (plays back RecordedAction list), RecordedAction (data models),
                  XPathBuilder (builds ancestor-chain XPath for UIA elements)

Services/         IScriptExecutor / ScriptExecutor (load/save/run scripts, parameter substitution),
                  HttpApiService (ASP.NET Core minimal APIs, IHostedService),
                  MqttService (MQTTnet client, IHostedService),
                  McpServerService (JSON-RPC over stdio, MCP protocol),
                  YoloTrainer (Python subprocess bridge for YOLO training pipeline),
                  LlmCommandParser (regex + LLM natural language → RecordedAction)

Vision/           WindowCapturer (GDI+ screenshots), VisionDetector (ONNX Runtime YOLO inference),
                  DetectionResult (data model)
```

### Data Flow

**Recording**: MouseHook/KeyboardHook → ActionRecorder → RecordedAction list (with XPath) → RecordingFile JSON in `scripts/` directory

**Playback**: ScriptExecutor/ActionPlayer loads RecordingFile → dispatches each action by type and click mode:
- Coordinate: direct SendInput
- UIA Path: XPath lookup (primary) → flat property fallback (AutomationId → Name+ControlType → ClassName) → coordinate fallback
- Vision: screenshot → YOLO detect → click detection center (with `FindNearest()` for disambiguation, label fallback to "button")

**Remote execution**: HTTP API / MQTT / MCP receive script name + parameters → ScriptExecutor resolves `{placeholder}` substitution → ActionPlayer executes → results returned

**LLM command parsing**: Natural language input → LlmCommandParser (regex pre-router → optional LLM API) → List\<RecordedAction\> → added to script

### App Layer

- `App.xaml.cs` — DI host, starts IHostedService instances based on `appsettings.json`
- `MainWindow.xaml.cs` — Main UI (code-behind pattern, no MVVM)
- `LabelWindow.xaml.cs` — Bounding-box annotation UI for YOLO training data
- `YoloTrainWizard.xaml.cs` — 4-step training wizard

## Key Conventions

- **Language**: All UI strings, comments, log messages, and documentation are in Chinese (Simplified). Code identifiers are in English.
- **UI Automation**: Uses FlaUI.UIA3 (v5.0.0) — NOT raw `System.Windows.Automation`. FlaUI provides XPath building, typed ControlType enum, and `ConditionFactory` for element queries.
- **WPF threading**: All cross-thread UI updates use `Dispatcher.BeginInvoke()`
- **FlaUI + WPF namespace conflict**: In App project, use `using FlaUIAutomation = FlaUI.Core.AutomationElements;` alias to avoid conflicts with `System.Windows.Window` etc.
- **Singleton logger**: `Logger.Instance` with sink pattern (console/file)
- **Event-driven**: C# events decouple UI from logic (e.g., `NodeRecorded`, `RecordingStarted`, `PlayCompleted`)
- **JSON serialization**: `System.Text.Json` with `JsonStringEnumConverter` and `WriteIndented = true`
- **DPI awareness**: `SetProcessDpiAwareness(2)` called on startup
- **Self-exclusion**: Recorder filters out clicks on its own window handle

## XPath-Based UIA Path System

When recording in UIA Path mode, the system captures not just flat properties but a full ancestor-chain XPath (e.g., `/Window[@Name='微信']/Pane/Button[@Name='发送'][2]`). The XPath is built by `XPathBuilder` walking the FlaUI element tree upward.

RecordedAction fields for UIA identification:
- `XPath` — Full ancestor-chain XPath string
- `SiblingIndex` — 0-based index among same-ControlType siblings (for `[n]` disambiguation)
- `RuntimeId` — Comma-separated RuntimeId string (auxiliary)

Playback strategy cascade: XPath → AutomationId → Name+ControlType+SiblingIndex → ClassName+ControlType → coordinate fallback. Old scripts without XPath fall back to the flat-property strategies automatically.

## Script Format

Scripts are JSON files in the `scripts/` directory. Structure:
- `Name`, `Description`, `CreatedAt`
- `Actions[]` — list of `RecordedAction` steps (each has `NodeId`, `Order`, `ActionType`, `Parameter`, `DelayMs`, `IsEnabled`, click mode fields, XPath fields)
- `Parameters[]` — list of `ScriptParameter` definitions with `{paramName}` placeholder syntax
- `DefaultClickMode`, `VisionModel` filename

Action types: Click, TypeText, SendKeys, Copy, Paste, InsertText, Wait, Screenshot, OpenApp, WaitApp, ReadContent, ScrollRead, Scroll, InputParam, RegexMatch

## YOLO Vision Pipeline

20 UI element classes: button, input, checkbox, radio, dropdown, tab, menu_item, icon, link, text_field, search_box, send_button, close_button, minimize_button, maximize_button, scrollbar, slider, toggle, tooltip, image

VisionLabel mapping: `ControlTypeToVisionLabel()` in ActionRecorder uses an extended mapping table with fuzzy matching. Unrecognized types log a warning instead of silently defaulting to "button". Vision mode playback falls back to "button" label when the specific label fails.

Training pipeline: ActionRecorder captures screenshots + YOLO labels → `yolo_train/label_and_train.py` (Python CLI: label, collect, train, export, deploy) → ONNX model → VisionDetector loads for inference

Training data collection and model selection UI are only visible when Vision click mode is selected.

## Remote Services Configuration

All services are toggled via `appsettings.json`:
- `HttpApi.Enabled` + `Port` (default 5000)
- `Mqtt.Enabled` + `BrokerHost/Port/TopicPrefix`
- `Mcp.Enabled` (stdio JSON-RPC, MCP 2024-11-05)
- `Llm.Enabled` + `Endpoint`/`ApiKey`/`Model` (default gpt-4o-mini)

MCP tools: `list_scripts`, `get_script_info`, `execute_script`, `check_script_exists`, `parse_command`

HTTP endpoints: `GET /api/scripts`, `GET /api/scripts/{name}`, `POST /api/scripts/execute`, `POST /api/parse-command`, etc.

## CI/CD

GitHub Actions (`release.yml`): publishes x64/x86/ARM64 self-contained single-file ZIPs on `v*` tag push or manual dispatch.
