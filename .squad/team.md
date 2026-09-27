# Squad Team

> Azure AI Drive-Thru — a Microsoft Foundry voice-ordering demo with persona switching and model flexibility

## Coordinator

| Name | Role | Notes |
|------|------|-------|
| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |

## Members

| Name | Role | Charter | Status |
|------|------|---------|--------|
| Rick | Lead | `.squad/agents/rick/charter.md` | 🏗️ Active |
| Morty | Frontend Dev | `.squad/agents/morty/charter.md` | ⚛️ Active |
| Summer | Backend Dev | `.squad/agents/summer/charter.md` | 🔧 Active |
| Birdperson | Tester | `.squad/agents/birdperson/charter.md` | 🧪 Active |
| Squanchy | DevOps | `.squad/agents/squanchy/charter.md` | ⚙️ Active |
| Unity | AI / Realtime Expert | `.squad/agents/unity/charter.md` | 🤖 Active |
| Beth | .NET / C# Backend Dev | `.squad/agents/beth/charter.md` | 🩺 Active |
| Scribe | Session Logger | `.squad/agents/scribe/charter.md` | 📋 Active |
| Ralph | Work Monitor | — | 🔄 Monitor |

## Project Context

- **Owner:** Brian Swiger
- **Project:** Azure AI Drive-Thru — a voice-driven drive-thru ordering demo on Microsoft Foundry (Azure OpenAI GPT-4o Realtime, Azure AI Search, Azure Container Apps) with persona switching across drive-thru brands (Sonic, McDonald's, Dunkin) and model flexibility.
- **Repo:** https://github.com/swigerb/AzureAIDriveThru
- **Stack:**
  - **Frontend:** React, TypeScript, Vite, Tailwind CSS, shadcn/ui
  - **Backend (Python, reference):** Python (aiohttp, WebSockets), Azure OpenAI Realtime API (gpt-realtime-2.1), Azure AI Search
  - **Backend (C#, in progress):** .NET 11 RC 1, ASP.NET Core, System.Net.WebSockets — feature-equivalent, validated by the shared conformance suite
  - **Infrastructure:** Bicep IaC, Azure Container Apps, Docker, azd CLI
  - **Data:** Jupyter notebooks for menu ingestion, JSON/PDF parsing, semantic hybrid search
- **Key Files:**
  - `app/backend/app.py` — Main backend entry point (aiohttp server)
  - `app/backend/rtmt.py` — Real-time middle tier for Azure OpenAI Realtime API
  - `app/backend/tools.py` — Azure AI Search tool calling integration
  - `app/backend/order_state.py` — Order state management
  - `app/frontend/src/` — React frontend with Vite
  - `infra/main.bicep` — Azure infrastructure definitions
  - `azure.yaml` — azd deployment configuration
- **Created:** 2026-03-19
