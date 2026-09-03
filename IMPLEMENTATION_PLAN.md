# Enterprise AI Summarizer - WPF Desktop Application Implementation Plan

## Problem & Architectural Vision
This project implements an enterprise-grade document intelligence and summarization platform in C# (.NET Windows Desktop) leveraging the **`Microsoft.Extensions.AI`** abstraction ecosystem and **Microsoft Foundry Local** for privacy-first, offline-capable processing.

The solution delivers a native **WPF Desktop Application** (`net10.0-windows` with `<UseWPF>true</UseWPF>`) featuring a modern Windows 11 Fluent dark-mode UI with MVVM architecture across six extended enterprise pillars:
1. **Multi-Format Ingestion Pipeline**: Native extraction and chunking for `.docx` (Word), `.pptx` (PowerPoint), `.pdf`, text/markdown, and meeting audio (`.mp3`, `.wav`, and transcripts).
2. **Advanced Summary Personas (Prompty)**: Prompty template engine with decoupled `.prompty` assets (*Executive Bullets*, *Action-Item Extractor*, *Legal Compliance Check*), plus an in-app Prompty template editor.
3. **Local-First & Hybrid Foundry Router**: Real-time **Privacy Mode** toggle enforcing zero data egress to local Foundry (`http://127.0.0.1:63715/v1`, dynamic `~/.foundry/daemon.json` auto-discovery, or Ollama `http://localhost:11434/v1` with Qwen2.5/Phi-3/Phi-4) at $0.00 cost, with smart escalation to cloud frontier models for complex documents.
4. **Semantic Grounding (RAG)**: Cross-referencing against internal corporate policies (e.g., Q2 Financial Framework, Compliance Policies) using `Microsoft.Extensions.VectorData` and vector similarity search.
5. **Agentic Summarization & Tool Calling**: `AIFunction` tool invocation featuring an **Auto-Filing Agent** (SharePoint archival, email dispatch, department categorization, task creation) and an **Interactive Summary Chat** for follow-up interrogation.
6. **Automated Rigor & Evaluation**: `Microsoft.Extensions.AI.Evaluation` pipeline assessing completeness, persona adherence, grounding/anti-hallucination, and content safety guardrails.

---

## Technical Architecture & Project Structure

```
d:\Study\foundry-summerize-app\
│
├── FoundrySummarizer.sln
│
├── src/
│   ├── FoundrySummarizer.Core/              (Class Library: net10.0)
│   │   ├── Ingestion/
│   │   │   ├── IDocumentParser.cs           (Parser interface)
│   │   │   ├── DocxDocumentParser.cs        (OpenXML Wordprocessing)
│   │   │   ├── PptxDocumentParser.cs        (OpenXML Presentation)
│   │   │   ├── PdfDocumentParser.cs         (UglyToad.PdfPig)
│   │   │   ├── PlainTextParser.cs           (Text/MD/JSON parser)
│   │   │   ├── AudioTranscriptionService.cs (MP3/WAV/Transcript parsing)
│   │   │   ├── SemanticChunker.cs           (Boundary-preserving sliding chunker)
│   │   │   └── DocumentIngestionPipeline.cs (Pipeline orchestrator)
│   │   │
│   │   ├── Personas/
│   │   │   ├── PromptyDocument.cs           (Frontmatter model & template binder)
│   │   │   ├── PromptyEngine.cs             (Prompty engine & built-in templates)
│   │   │   └── Prompts/
│   │   │       ├── ExecutiveBullets.prompty
│   │   │       ├── ActionItemExtractor.prompty
│   │   │       └── LegalComplianceCheck.prompty
│   │   │
│   │   ├── Routing/
│   │   │   ├── FoundryOptions.cs            (PrivacyMode, Local & Cloud config sections)
│   │   │   ├── HybridChatClientRouter.cs   (IChatClient router & daemon health ping)
│   │   │   └── LocalFoundryFallbackClient.cs(Offline engine implementing IChatClient)
│   │   │
│   │   ├── Grounding/
│   │   │   ├── GroundingRecord.cs          ([VectorStoreKey], [VectorStoreData], [VectorStoreVector])
│   │   │   ├── IVectorGroundingService.cs   (Grounding interface)
│   │   │   ├── VectorGroundingService.cs   (Microsoft.Extensions.VectorData store)
│   │   │   └── SemanticEmbeddingGenerator.cs(IEmbeddingGenerator<string, Embedding<float>>)
│   │   │
│   │   ├── Agentic/
│   │   │   ├── AutoFilingTools.cs          (AIFunctions: Department, SharePoint, Email, Tasks)
│   │   │   ├── AutoFilingAgent.cs          (Orchestrated tool workflow)
│   │   │   └── InteractiveSummaryChatAgent.cs (Multi-turn conversational co-pilot)
│   │   │
│   │   └── Evaluation/
│   │       ├── IEvaluationPipeline.cs       (Evaluation pipeline interface)
│   │       ├── EvaluationPipeline.cs       (Microsoft.Extensions.AI.Evaluation)
│   │       ├── Evaluators/
│   │       │   ├── CompletenessEvaluator.cs
│   │       │   ├── PersonaAdherenceEvaluator.cs
│   │       │   ├── GroundingEvaluator.cs
│   │       │   └── ContentSafetyGuardrailEvaluator.cs
│   │       └── Models/
│   │           └── SummaryEvaluationReport.cs
│   │
│   └── FoundrySummarizer.Wpf/               (WPF Desktop Application: net10.0-windows)
│       ├── appsettings.json                 (Dynamic Configuration for Local and Cloud endpoints)
│       ├── appsettings.example.json         (Template configuration with Ollama & Azure AI Foundry options)
│       ├── App.xaml / App.xaml.cs           (ConfigurationBuilder & Dependency Injection)
│       ├── MainWindow.xaml / MainWindow.xaml.cs (Header, Tabs, View Container)
│       ├── ViewModels/                      (CommunityToolkit.Mvvm)
│       │   ├── MainViewModel.cs             (Global state, samples, privacy toggle)
│       │   ├── IngestionViewModel.cs        (File browsing, chunk visualizer)
│       │   ├── SummarizerViewModel.cs       (Persona selector, Prompty editor, summary output)
│       │   ├── GroundingViewModel.cs        (Policy vector store & similarity search)
│       │   ├── AgenticViewModel.cs          (Auto-Filing execution & tool logs)
│       │   ├── ChatViewModel.cs             (Interactive co-pilot Q&A)
│       │   └── EvaluationViewModel.cs       (Score meters & guardrail test)
│       ├── Views/
│       │   ├── IngestionView.xaml
│       │   ├── SummarizerView.xaml
│       │   ├── GroundingView.xaml
│       │   ├── AgenticView.xaml
│       │   ├── ChatView.xaml
│       │   └── EvaluationView.xaml
│       ├── Styles/
│       │   └── ModernDarkTheme.xaml         (Windows 11 Fluent Dark Palette)
│       └── Converters/
│           └── ValueConverters.cs          (BoolToVis, ScoreBrush, StatusBrush)
│
├── samples/                                 (Ready-to-test sample files)
│   ├── sample_project_proposal.docx         (Real OpenXml Word Document)
│   ├── sample_executive_deck.pptx           (Real OpenXml PowerPoint Deck)
│   ├── sample_contract.txt                  (Master Services Agreement)
│   ├── sample_meeting_transcript.txt        (Project Helios Review Transcript)
│   ├── sample_financial_proposal.txt        (Capital Budget Proposal)
│   └── corporate_policies/
│       ├── Q2_Financial_Framework.txt       ($100k VP approval gate)
│       └── Cloud_Compliance_Policy.txt      (1x/2x liability caps & local inference standard)
│
└── tests/
    └── FoundrySummarizer.Tests/             (xUnit Automated Tests: 23 passing tests)
        ├── IngestionTests.cs
        ├── PersonaTests.cs
        ├── HybridRouterTests.cs
        ├── VectorGroundingTests.cs
        ├── AgenticToolsTests.cs
        └── EvaluationTests.cs
```

---

## WPF Desktop UI Layout & Features

- **Header Bar & Window Bounds**:
  - Screen WorkArea auto-fit: guarantees top window title bar with Minimize, Maximize, and Close buttons is visible across laptop resolutions and scaling factors.
  - Dark Mode ComboBox: fully custom control template eliminating white-on-white text rendering in dropdown selections.
  - Application Title & Ecosystem Subtitle ("Powered by Microsoft.Extensions.AI & Foundry Local")
  - **Quick Sample Pickers**: One-click instant loading for Meeting Transcript, Project Proposal (.docx), Contract, Deck (.pptx).
  - **Privacy Mode Toggle**: Visual switch between `🔒 Privacy Mode: Local Foundry Only (100% Offline, $0.00)` and `☁️ Hybrid Cloud Mode`.
  - **Connection Badge**: Real-time ping check for local Foundry daemon (dynamic port e.g. `http://127.0.0.1:63715/v1`).

- **Main Navigation Tabs**:
  1. **📄 Ingestion Pipeline**:
     - File browsing / drag & drop for `.docx`, `.pptx`, `.pdf`, `.txt`, `.mp3`/`.wav`.
     - Character and token counts.
     - Extracted raw text editor.
     - Semantic Chunks Visualizer list with token counts and IDs.
  2. **✨ Persona Summarizer**:
     - Persona selector: *Executive Bullets*, *Action-Item Extractor*, *Legal Compliance Check*.
     - RAG Semantic Grounding toggle.
     - Live Prompty System & User Template editor.
     - Routing Decision Card (showing endpoint, model, privacy enforcement, and $0.00 cost).
     - Formatted Summary Output with Copy to Clipboard.
  3. **🧠 Semantic Grounding (RAG)**:
     - Vector store policy manager (view indexed reference documents like Q2 Financial Framework).
     - Live cosine similarity tester with percentage score badges.
     - Add new custom policy to vector store.
  4. **🤖 Agentic Workflows**:
     - "Run Auto-Filing Agent" trigger.
     - 4-step pipeline status cards:
       - 1. Department Categorizer (`DetermineDepartment`) -> e.g. `Finance & Accounting`
       - 2. SharePoint Archival (`SaveToSharePoint`) -> `https://sharepoint.enterprise.local/sites/...`
       - 3. Email Notification (`EmailSummary`) -> dispatched via Enterprise SMTP
       - 4. Task Board Tickets (`CreateTask`) -> Jira/ADO tickets created
     - Real-time function calling audit log feed.
  5. **💬 Interactive Summary Chat**:
     - Chat panel to interrogate the generated summary and original document.
     - Quick starter question chips ("Why was the budget flagged?", "Who is assigned to tasks?", "What are the liability caps?").
     - Multi-turn conversational memory.
  6. **🛡️ Automated Rigor & Evaluation**:
     - Powered by `Microsoft.Extensions.AI.Evaluation`.
     - Progress meters for Completeness, Persona Adherence, and Grounding (Anti-Hallucination).
     - Content Safety Guardrail (PASS / INTERCEPTED).
     - "Test Unsafe PII Guardrail" demonstration button.

---

## Verification Plan

### Automated Tests
Run via dotnet CLI:
```powershell
dotnet test
```
All 23 automated tests pass in ~600 ms, testing all 6 architectural subsystems.

### Running the Desktop Application
```powershell
dotnet run --project src/FoundrySummarizer.Wpf/FoundrySummarizer.Wpf.csproj
```
