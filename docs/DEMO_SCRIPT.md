# Demo script

## Three-minute version

1. Open the deployed app URL in Edge or Chrome. Sign in with Entra ID if the environment is in production auth mode, and allow the microphone.
2. Start with Dunkin'. Say: "I need a large hot regular coffee and a 25 count MUNCHKINS with glazed, chocolate glazed, and jelly." Point out that regular coffee becomes cream and sugar, MUNCHKINS are captured by flavor and count, and the ticket is priced server-side.
3. Switch to Sonic. Confirm the switch. Say: "Make a SuperSONIC Double Cheeseburger Combo with tots and a medium Cherry Limeade, then make the drink large." Point out combo slots, drink resize, and the upcharge when the combo drink is larger than the included Medium.
4. Switch to McDonald's. Say: "I want a Big Mac Meal, large, with fries and a Diet Coke. Actually make the whole meal medium." Point out whole-meal resizing and off-menu rejection by asking for a Whopper.
5. Open Settings. Show Demo Mode, the model picker grouped by Realtime and Cascade, the voice picker, and breakfast or lunch menu mode for daypart personas. Explain that realtime uses `gpt-realtime-2.1`, while cascade uses STT, a reasoning model, and TTS.

## Setup checklist

- URL: your deployed Container Apps URL, or `http://localhost:8000` for a local run.
- Browser: Edge or Chrome.
- Sign in through Entra ID before the demo when `AUTH_MODE=Entra`.
- Allow microphone access.
- Use a quiet room or headset. Laptop speakers can work, but headset audio reduces echo.
- Keep Azure Portal tabs ready for Foundry deployments, Azure AI Search indexes, Container Apps, and Log Analytics.
- Happy-hour windows: Sonic 2 to 4 PM Central, Dunkin' 2 to 5 PM Eastern. McDonald's has no happy hour.
- Use **Start a new order** between runs if the ticket has old items.

## Demo Mode beat

Open **Settings** and enable **Demo Mode**. If the active persona has `assets/demo/guestScript.json`, the center controls show **Run demo: This brand** and **Full tour**.

Point out:

- The demo uses synthetic guest audio and an Ava guest voice card with captions.
- **Run demo: This brand** runs only the active persona's script.
- **Full tour** runs the available persona scripts in sequence.
- **Stop demo** ends the scripted conversation cleanly.

## Opening talk track

"This is one Microsoft Foundry AI Drive Thru app with shared frontend, backend, tools, and Azure resources. The persona supplies the menu, prompts, theme, search index name, happy-hour rules, bundle rules, demo script, and UI copy. The demo is grounded by Azure AI Search and the order is priced by server-side code, not by the model."

## Dunkin' beat

Select Dunkin' if it is not already active.

Say exactly:

> "Hi, can I get a large hot regular coffee and a 25 count MUNCHKINS? Make the MUNCHKINS glazed, chocolate glazed, and jelly."

Point out:

- The ticket shows a large Original Blend Coffee with cream and sugar because the persona treats regular coffee as cream and sugar.
- MUNCHKINS are real menu items with count sizes and flavor choices. The prompt tells the assistant to speak the name as "Munchkins".
- The `search` tool grounds menu names and the `update_order` tool creates priced line items.

If it is between 2 and 5 PM Eastern, say:

> "Add a medium Pumpkin Spice Signature Latte."

Point out:

- Dunkin' happy hour applies 25 percent off only to standalone signature lattes and cold beverages.
- Donuts, bakery, and breakfast sandwiches stay full price.

## Sonic beat

Use the persona picker and choose Sonic Drive-In. Confirm the switch if asked.

Say exactly:

> "I want a SuperSONIC Double Cheeseburger Combo with tots and a medium Cherry Limeade."

Then say:

> "Make the Cherry Limeade large."

Point out:

- The app keeps combo parts in bundle slots.
- Medium side and drink components are included. Upsizing a combo drink above Medium adds the backend-calculated component upcharge.
- The live ticket updates without waiting for checkout.

Show breakfast vs lunch mode with the menu mode control. Say:

> "Switch to breakfast mode and show that breakfast items are available. Now switch back to lunch."

If it is between 2 and 4 PM Central, say:

> "Add a large Blue Raspberry Slush."

Point out:

- Sonic happy hour applies half-price pricing only to standalone slushes and fountain drinks.
- Shakes, ice cream, and combo components are not discounted.

## McDonald's beat

Switch to McDonald's and confirm the switch.

Say exactly:

> "Can I get a Big Mac Meal, large, with fries and a Diet Coke?"

Then say:

> "Make the whole meal medium."

Point out:

- McDonald's meals use the `wholeBundleSize` resize rule, so the meal size changes the included components together.
- The ticket remains priced server-side with tax.

Show breakfast mode:

> "Switch to breakfast mode and ask for a Big Breakfast with Hotcakes Meal."

Show off-menu rejection:

> "Can I get a Whopper?"

Point out:

- The assistant should reject the off-menu item and steer back to grounded menu choices.
- McDonald's has no happy-hour rule.

## Switching personas and models

- Persona switching is runtime only. The URL can also deep link with `?persona=sonic`, `?persona=dunkin`, or `?persona=mcdonalds`.
- If an order or conversation is active, the app shows a confirm dialog before switching.
- The model picker groups choices by pipeline.
- Realtime is the lowest-latency voice-to-voice path: `gpt-realtime-2.1-mini`, voice `marin`, and `whisper-1` transcription by default. `gpt-realtime-2.1` stays selectable for deeper reasoning.
- Cascade uses `gpt-4o-transcribe`, `gpt-5-mini` reasoning for current persona allow-lists, and `gpt-4o-mini-tts`.

## Azure views to show

In Azure Portal or CLI, show read-only views only.

- Foundry deployments: `gpt-realtime-2.1`, `gpt-realtime-2.1-mini`, `text-embedding-3-large`, `gpt-5-mini`, `phi-4`, `gpt-4o-transcribe`, and `gpt-4o-mini-tts`.
- Azure AI Search indexes: `sonic-menu-items`, `dunkin-menu-items`, and `mcdonalds-menu-items`.
- Azure Container Apps: one public app serving the frontend and Python backend.
- Log Analytics: backend logs, session ids, tool calls, rate-limit recovery, and auth posture checks.
- Architecture: one app, personas as data, keyless managed identity to Foundry and Search, and a .NET parity backend guarded by conformance tests.

## Recovery tips

- If the assistant mishears, say: "No, change that to ..." or use **Start a new order**.
- If the mic does not start, refresh the page and allow microphone permission again.
- If audio echoes, lower speaker volume or use a headset.
- If a persona switch feels stuck, stop the mic, confirm or cancel the dialog, then start again.
- If the model hits a rate limit, wait for the recovery prompt and repeat the last sentence.

## Talking points

- Latency: realtime is optimized for natural turn taking. Cascade proves the same frontend and tool contract can run on STT, reasoning, and TTS components.
- Grounding: menu answers come from Azure AI Search, not model memory.
- Determinism: pricing, tax, happy hour, bundles, dayparts, and off-menu checks run server-side.
- Security: Azure resources use managed identity and `DefaultAzureCredential`. Azure OpenAI-compatible and Search local auth are disabled in provisioned resources.
- Auth: the app uses MSAL in the SPA and JWT validation in the API with the `DriveThru.User` app role. EasyAuth is off.
- Parity: the production backend is Python. The C# .NET 11 backend is a parity backend, with the conformance suite proving behavior on both legs.
